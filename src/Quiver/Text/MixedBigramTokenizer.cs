namespace Quiver.Text;

/// <summary>
/// バイグラム / ワード混合トークナイザ。入力を NFKC + ASCII 小文字化で正規化し、
/// Unicode スクリプトごとに分割する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>CJK 連続 (かな / 漢字 / ハングル) は重なり<b>バイグラム</b>に分割する。
/// 孤立 CJK 1 文字は検索可能性のためユニグラムとして放出する。</item>
/// <item><paramref name="emitUnigrams"/> が <c>true</c> のとき、CJK 連続 2 文字以上のランでも
/// 各文字の<b>補足ユニグラム</b>をバイグラムと並行して放出する。
/// これにより 1 文字の CJK 検索クエリが隣接文字に関わらずヒットする。</item>
/// <item>ラテン / 数字の連続は単一の<b>ワード</b>トークンとして放出する。
/// 区切りは非英数字文字。</item>
/// <item>その他 (空白、句読点、記号、絵文字) はセパレータとして機能しトークンを生成しない。</item>
/// </list>
/// 形態素解析は行わない。バイグラムの不正確さはベクトル側と RRF 融合が RAG ユースケースで補償する。
/// </remarks>
public sealed class MixedBigramTokenizer : ITokenizer, INormTokenCounter
{
    /// <summary>全文索引カタログに記録されるバイグラム専用トークナイザ ID。</summary>
    public const string DefaultTokenizerId = "mixed-bigram-v1";

    /// <summary>CJK ユニグラム併用モードのトークナイザ ID。</summary>
    public const string UnigramTokenizerId = "mixed-bigram-unigram-v1";

    private readonly ITextNormalizer _normalizer;
    private readonly bool _emitUnigrams;
    private readonly string _tokenizerId;

    /// <summary>トークナイザを生成する。既定のノーマライザは NFKC + ASCII 小文字化を適用する。</summary>
    /// <param name="emitUnigrams">
    /// <c>true</c> のとき、CJK 連続 2 文字以上のランで各文字の補足ユニグラムも放出する。
    /// 既定は <c>false</c> (バイグラムのみ)。
    /// </param>
    /// <param name="normalizer">
    /// 分割前に適用するノーマライザ。<c>null</c> のとき NFKC + ASCII 小文字化ノーマライザを使う。
    /// インデックス構築時と検索時で同一の正規化を使う必要がある
    /// (<see cref="TokenizerId"/> がこれを保証する)。
    /// </param>
    public MixedBigramTokenizer(bool emitUnigrams = false, ITextNormalizer? normalizer = null)
    {
        _emitUnigrams = emitUnigrams;
        _tokenizerId = emitUnigrams ? UnigramTokenizerId : DefaultTokenizerId;
        _normalizer = normalizer ?? new JapaneseAwareNormalizer
        {
            DefaultFlags = NormalizationFlags.UnicodeNFKC | NormalizationFlags.LowerCaseAscii,
        };
    }

    /// <inheritdoc/>
    public string TokenizerId => _tokenizerId;

    /// <inheritdoc/>
    public void Tokenize(ReadOnlySpan<char> text, ITokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (text.IsEmpty) return;

        // Normalize first, then segment (spec: 07_fulltext.md#tokenizer).
        string normalized = _normalizer.Normalize(text).Text;
        ReadOnlySpan<char> s = normalized.AsSpan();
        int n = s.Length;

        int i = 0;
        while (i < n)
        {
            CharClass cls = Classify(s[i]);
            if (cls == CharClass.Other)
            {
                i++;
                continue;
            }

            // Extend the run while the script class stays the same.
            int start = i;
            i++;
            while (i < n && Classify(s[i]) == cls) i++;
            ReadOnlySpan<char> run = s.Slice(start, i - start);

            if (cls == CharClass.Word)
                sink.Accept(run);
            else
                EmitBigrams(run, sink, _emitUnigrams);
        }
    }

    /// <summary>
    /// BM25 norms 用の文書長を返す。補足ユニグラムを除外し、バイグラム専用モードと
    /// 同等のトークン数を返すため、ユニグラム併用で BM25 パラメータが崩れない。
    /// </summary>
    int INormTokenCounter.CountNormTokens(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return 0;

        string normalized = _normalizer.Normalize(text).Text;
        ReadOnlySpan<char> s = normalized.AsSpan();
        int n = s.Length;
        int count = 0;

        int i = 0;
        while (i < n)
        {
            CharClass cls = Classify(s[i]);
            if (cls == CharClass.Other) { i++; continue; }

            int start = i;
            i++;
            while (i < n && Classify(s[i]) == cls) i++;
            int runLen = i - start;

            if (cls == CharClass.Word)
                count++;
            else
                count += runLen == 1 ? 1 : runLen - 1; // bigrams or isolated unigram
        }

        return count;
    }

    private static void EmitBigrams(ReadOnlySpan<char> run, ITokenSink sink, bool emitUnigrams)
    {
        if (run.Length == 1)
        {
            sink.Accept(run); // isolated CJK char -> unigram (still searchable)
            return;
        }

        for (int j = 0; j + 1 < run.Length; j++)
            sink.Accept(run.Slice(j, 2));

        if (emitUnigrams)
        {
            for (int j = 0; j < run.Length; j++)
                sink.Accept(run.Slice(j, 1));
        }
    }

    private enum CharClass : byte { Cjk, Word, Other }

    private static CharClass Classify(char c)
    {
        if (IsCjk(c)) return CharClass.Cjk;
        if (char.IsLetterOrDigit(c)) return CharClass.Word;
        return CharClass.Other;
    }

    // BMP CJK scripts. Supplementary-plane ideographs (CJK Ext B+, U+20000 and
    // up) arrive as surrogate pairs and fall through to Other; treating them as
    // CJK would require surrogate-aware bigram slicing, deferred past the MVP.
    internal static bool IsCjk(char c)
    {
        int v = c;
        return
            (v >= 0x3005 && v <= 0x3007)   // 々 (iteration mark), 〆, 〇 — bind to adjacent kanji
         || (v >= 0x3040 && v <= 0x30FF)   // Hiragana + Katakana
         || (v >= 0x31F0 && v <= 0x31FF)   // Katakana phonetic extensions
         || (v >= 0x3400 && v <= 0x4DBF)   // CJK Unified Ideographs Extension A
         || (v >= 0x4E00 && v <= 0x9FFF)   // CJK Unified Ideographs
         || (v >= 0xF900 && v <= 0xFAFF)   // CJK Compatibility Ideographs
         || (v >= 0xAC00 && v <= 0xD7A3);  // Hangul syllables
    }
}
