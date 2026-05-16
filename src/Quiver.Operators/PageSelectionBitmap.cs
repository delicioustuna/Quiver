using System.Numerics;

namespace Quiver.Operators;

/// <summary>
/// PW-12: Minimal per-batch selection bitmap used by <see cref="BitmapFilterOperator"/>.
/// Wraps a caller-owned <see cref="Span{T}"/> of <see cref="ulong"/> words plus a
/// logical bit count (<c>≤ words.Length * 64</c>). Per <c>codex_advice_3.md 7.4</c>,
/// even on row-oriented pages a small bitmap lets multiple predicates evaluate
/// in selectivity order without re-touching rows that already failed.
/// </summary>
internal ref struct PageSelectionBitmap
{
    private readonly Span<ulong> _words;
    private readonly int _count;

    public PageSelectionBitmap(Span<ulong> words, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int needed = (count + 63) >> 6;
        if (words.Length < needed) throw new ArgumentException("words too small", nameof(words));
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
    /// Yields each set bit index in ascending order. Clears of bits the
    /// enumerator has already pulled into <c>_current</c> are NOT observed,
    /// which is the desired pass semantics: predicate <i>k</i> evaluates
    /// every row still set <i>at the start of pass k</i>.
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
