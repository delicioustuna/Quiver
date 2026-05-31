using System.Numerics;

namespace Quiver.Query.Physical;

/// <summary>
/// PW-12: <see cref="BitmapFilterOperator"/> が利用するバッチ単位の最小選択ビットマップ。
/// 呼び出し側が所有する <see cref="ulong"/> ワードの <see cref="Span{T}"/> と論理ビット数
/// (<c>≤ words.Length * 64</c>) をラップする。
/// codex_advice_3.md 7.4 節の通り、行指向ページでも小さなビットマップを介在させることで
/// 複数述語を選択度順に評価でき、既に失格となった行を再度走査せずに済む。
/// </summary>
internal ref struct PageSelectionBitmap
{
    private readonly Span<ulong> _words;
    private readonly int _count;

    public PageSelectionBitmap(Span<ulong> words, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int needed = (count + 63) >> 6;
        if (words.Length < needed) throw new ArgumentException("words が小さすぎます", nameof(words));
        _words = words;
        _count = count;
    }

    public int Capacity => _count;

    public void SetAll()
    {
        int fullWords = _count >> 6;
        for (int i = 0; i < fullWords; i++) _words[i] = ulong.MaxValue;
        int rem = _count & 63;
        if (rem > 0) _words[fullWords] = (1UL << rem) - 1UL;
        int tail = (_count + 63) >> 6;
        for (int i = (rem > 0 ? fullWords + 1 : fullWords); i < tail; i++) _words[i] = 0UL;
    }

    public void Clear()
    {
        int tail = (_count + 63) >> 6;
        for (int i = 0; i < tail; i++) _words[i] = 0UL;
    }

    public void AndEquals(int index, bool keep)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        if (keep) return;
        int w = index >> 6;
        ulong mask = 1UL << (index & 63);
        _words[w] &= ~mask;
    }

    public bool IsSet(int index)
    {
        if ((uint)index >= (uint)_count) return false;
        int w = index >> 6;
        ulong mask = 1UL << (index & 63);
        return (_words[w] & mask) != 0;
    }

    public int PopCount()
    {
        int tail = (_count + 63) >> 6;
        int c = 0;
        for (int i = 0; i < tail; i++) c += BitOperations.PopCount(_words[i]);
        return c;
    }

    public Enumerator GetEnumerator() => new(_words, _count);

    /// <summary>
    /// セットされているビットのインデックスを昇順で列挙する。enumerator が既に
    /// <c>_current</c> に取り込んだビットへの後続クリアは観測されない — これは意図した
    /// パスセマンティクス: 述語 <i>k</i> は「パス k 開始時にセットされていた全行」を評価する。
    /// </summary>
    public ref struct Enumerator
    {
        private readonly Span<ulong> _words;
        private readonly int _count;
        private int _wordIdx;
        private ulong _current;
        private int _bitIdx;

        public Enumerator(Span<ulong> words, int count)
        {
            _words = words;
            _count = count;
            _wordIdx = -1;
            _current = 0;
            _bitIdx = -1;
        }

        public int Current => _bitIdx;

        public bool MoveNext()
        {
            while (true)
            {
                if (_current != 0)
                {
                    int tz = BitOperations.TrailingZeroCount(_current);
                    _current &= _current - 1;
                    _bitIdx = (_wordIdx << 6) + tz;
                    return _bitIdx < _count;
                }
                _wordIdx++;
                if (_wordIdx >= _words.Length) return false;
                if ((_wordIdx << 6) >= _count) return false;
                _current = _words[_wordIdx];
            }
        }
    }
}
