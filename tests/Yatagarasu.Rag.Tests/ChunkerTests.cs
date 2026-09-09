using FluentAssertions;
using Xunit;

namespace Yatagarasu.Rag.Tests;

/// <summary>
/// Chunker の純粋ロジック検証。見出し階層 / 段落分割 + オーバーラップ / Table・Code 非分割 /
/// 空文書 / charStart-End 整合、および「連結すると (オーバーラップ除き) 原文復元」property をカバーする。
/// </summary>
public sealed class ChunkerTests
{
    [Theory]
    [InlineData(BlockKind.Paragraph, 1, 0)]
    [InlineData(BlockKind.Paragraph, 2, 0)]
    [InlineData(BlockKind.Paragraph, 2, 1)]
    [InlineData(BlockKind.Table, 1, 0)]
    [InlineData(BlockKind.Code, 1, 0)]
    public void Small_windows_keep_each_supplementary_scalar_whole(BlockKind kind, int size, int overlap)
    {
        var chunks = Chunker.Chunk(new[] { new IngestedBlock(kind, "😀😀") },
            new ChunkingOptions { TargetSize = size, Overlap = overlap, MaxChunkSize = size });
        chunks.Select(c => c.Text).Should().Equal("😀", "😀");
        chunks.Select(c => c.CharStart).Should().Equal(0, 2);
        chunks.Select(c => c.CharEnd).Should().Equal(2, 4);
    }

    [Fact]
    public void Unicode_corpus_preserves_scalars_coverage_and_overlap_bound()
    {
        string[] texts = ["", "日本語。\n次の文！", "😀😀", "a😀b😀c", "✈️👨‍👩‍👧‍👦e\u0301", "a\n😀 xyz 👩‍💻 next"];
        var random = new Random(20260908);
        string[] alphabet = ["a", "日", "😀", "𝄞", " ", "\n", "\u0301", "\u200d", "\ufe0f"];
        texts = texts.Concat(Enumerable.Range(0, 40).Select(_ =>
            string.Concat(Enumerable.Range(0, 25).Select(_ => alphabet[random.Next(alphabet.Length)])))).ToArray();
        foreach (string text in texts)
        foreach (BlockKind kind in new[] { BlockKind.Paragraph, BlockKind.Table, BlockKind.Code })
        for (int size = 1; size <= 6; size++)
        for (int overlap = 0; overlap < size; overlap++)
        {
            var chunks = Chunker.Chunk(new[] { new IngestedBlock(kind, text) },
                new ChunkingOptions { TargetSize = size, MaxChunkSize = size, Overlap = overlap });
            int previousStart = -1, covered = 0;
            foreach (var chunk in chunks)
            {
                chunk.CharStart.Should().BeGreaterThan(previousStart);
                chunk.CharStart.Should().BeLessOrEqualTo(covered);
                (covered - chunk.CharStart).Should().BeLessOrEqualTo(kind == BlockKind.Paragraph ? overlap : 0);
                chunk.Text.Should().Be(text[chunk.CharStart..chunk.CharEnd]);
                chunk.Text.Length.Should().BeInRange(1, Math.Max(size, 2));
                int position = 0;
                while (position < chunk.Text.Length)
                {
                    System.Text.Rune.DecodeFromUtf16(chunk.Text.AsSpan(position), out _, out int consumed)
                        .Should().Be(System.Buffers.OperationStatus.Done);
                    position += consumed;
                }
                previousStart = chunk.CharStart;
                covered = chunk.CharEnd;
            }
            covered.Should().Be(text.Length);
            Reconstruct(chunks).Should().Be(text);
        }
    }

    [Fact]
    public void Malformed_utf16_is_preserved_without_splitting_valid_pairs()
    {
        string text = "a\udc00b\ud800c😀\ud800";
        var chunks = Chunker.Chunk(new[] { Para(text) }, new ChunkingOptions { TargetSize = 1, Overlap = 0 });
        chunks.Select(c => c.Text).Should().Equal("a", "\udc00", "b", "\ud800", "c", "😀", "\ud800");
        Reconstruct(chunks).Should().Be(text);
    }

    private static IngestedBlock Para(string text) => new(BlockKind.Paragraph, text);
    private static IngestedBlock Heading(int level, string text) => new(BlockKind.Heading, text, HeadingLevel: level);

    [Fact]
    public void Empty_document_yields_no_chunks()
    {
        Chunker.Chunk(Array.Empty<IngestedBlock>()).Should().BeEmpty();
    }

    [Fact]
    public void Single_small_paragraph_is_one_chunk()
    {
        var chunks = Chunker.Chunk(new[] { Para("hello world") });

        chunks.Should().HaveCount(1);
        var c = chunks[0];
        c.Text.Should().Be("hello world");
        c.Ordinal.Should().Be(0);
        c.HeadingPath.Should().BeEmpty();
        c.CharStart.Should().Be(0);
        c.CharEnd.Should().Be("hello world".Length);
    }

    [Fact]
    public void Heading_updates_path_and_is_not_emitted()
    {
        var blocks = new[]
        {
            Heading(1, "Chapter"),
            Para("intro"),
            Heading(2, "Section"),
            Para("body"),
        };

        var chunks = Chunker.Chunk(blocks);

        chunks.Should().HaveCount(2); // 見出しは本文に出ない
        chunks[0].Text.Should().Be("intro");
        chunks[0].HeadingPath.Should().Be("Chapter");
        chunks[1].Text.Should().Be("body");
        chunks[1].HeadingPath.Should().Be("Chapter > Section");
    }

    [Fact]
    public void Heading_of_same_or_shallower_level_pops_deeper_sections()
    {
        var blocks = new[]
        {
            Heading(1, "A"),
            Heading(2, "B"),
            Para("under B"),
            Heading(1, "C"),   // B (level 2) を pop し A も置き換える
            Para("under C"),
        };

        var chunks = Chunker.Chunk(blocks);

        chunks.Should().HaveCount(2);
        chunks[0].HeadingPath.Should().Be("A > B");
        chunks[1].HeadingPath.Should().Be("C");
    }

    [Fact]
    public void Long_paragraph_is_split_with_overlap_and_reconstructs()
    {
        string text = new string('a', 50) + new string('b', 50) + new string('c', 50); // 150 chars
        var opts = new ChunkingOptions { TargetSize = 60, Overlap = 10 };

        var chunks = Chunker.Chunk(new[] { Para(text) }, opts);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Text.Length <= 60);

        // 連続チャンクは Overlap=10 だけ重なる。
        for (int i = 1; i < chunks.Count; i++)
        {
            int overlap = chunks[i - 1].CharEnd - chunks[i].CharStart;
            overlap.Should().Be(10);
            chunks[i].Text.Should().StartWith(chunks[i - 1].Text[^10..]);
        }

        // オーバーラップを除いて連結すると原文に戻る。
        Reconstruct(chunks).Should().Be(text);
    }

    [Fact]
    public void Table_is_not_split_even_when_oversized()
    {
        string table = new string('x', 500);
        var opts = new ChunkingOptions { TargetSize = 50, Overlap = 5 };

        var chunks = Chunker.Chunk(new[] { new IngestedBlock(BlockKind.Table, table) }, opts);

        chunks.Should().HaveCount(1);
        chunks[0].Text.Should().Be(table);
    }

    [Fact]
    public void Code_is_not_split_even_when_oversized()
    {
        string code = new string('y', 300);
        var opts = new ChunkingOptions { TargetSize = 50, Overlap = 5 };

        var chunks = Chunker.Chunk(new[] { new IngestedBlock(BlockKind.Code, code) }, opts);

        chunks.Should().HaveCount(1);
        chunks[0].Text.Should().Be(code);
    }

    [Fact]
    public void MaxChunkSize_force_splits_oversized_table_as_safety_valve()
    {
        string table = new string('x', 250);
        var opts = new ChunkingOptions { TargetSize = 1000, Overlap = 0, MaxChunkSize = 100 };

        var chunks = Chunker.Chunk(new[] { new IngestedBlock(BlockKind.Table, table) }, opts);

        chunks.Should().HaveCount(3); // 100 + 100 + 50
        chunks.Should().OnlyContain(c => c.Text.Length <= 100);
        string.Concat(chunks.Select(c => c.Text)).Should().Be(table); // overlap=0 なので単純連結
    }

    [Fact]
    public void Small_paragraphs_pack_into_one_chunk_with_separator()
    {
        var blocks = new[] { Para("aaa"), Para("bbb"), Para("ccc") };
        var opts = new ChunkingOptions { TargetSize = 100, Overlap = 10 };

        var chunks = Chunker.Chunk(blocks, opts);

        chunks.Should().HaveCount(1);
        chunks[0].Text.Should().Be("aaa" + Chunker.BlockSeparator + "bbb" + Chunker.BlockSeparator + "ccc");
    }

    [Fact]
    public void Packing_respects_target_size_and_starts_new_chunk()
    {
        // 各 8 文字 + 区切り 2 文字。target 20 だと "p0\n\np1"(=... ) でちょうど。
        var blocks = new[] { Para("aaaaaaaa"), Para("bbbbbbbb"), Para("cccccccc") };
        var opts = new ChunkingOptions { TargetSize = 18, Overlap = 4 };
        // "aaaaaaaa"(8) + sep(2) + "bbbbbbbb"(8) = 18 <= 18 → パック。+ sep(2)+8 = 28 > 18 → 新チャンク。

        var chunks = Chunker.Chunk(blocks, opts);

        chunks.Should().HaveCount(2);
        chunks[0].Text.Should().Be("aaaaaaaa" + Chunker.BlockSeparator + "bbbbbbbb");
        chunks[1].Text.Should().Be("cccccccc");
    }

    [Fact]
    public void Table_breaks_packing_buffer()
    {
        var blocks = new[]
        {
            Para("before"),
            new IngestedBlock(BlockKind.Table, "TBL"),
            Para("after"),
        };
        var opts = new ChunkingOptions { TargetSize = 1000, Overlap = 10 };

        var chunks = Chunker.Chunk(blocks, opts);

        chunks.Select(c => c.Text).Should().Equal("before", "TBL", "after");
    }

    [Fact]
    public void Page_is_carried_from_first_block_of_chunk()
    {
        var blocks = new[]
        {
            new IngestedBlock(BlockKind.Paragraph, "p1", Page: 3),
            new IngestedBlock(BlockKind.Paragraph, "p2", Page: 4),
        };
        var opts = new ChunkingOptions { TargetSize = 1000, Overlap = 10 };

        var chunks = Chunker.Chunk(blocks, opts);

        chunks.Should().HaveCount(1);
        chunks[0].Page.Should().Be(3); // 先頭ブロックのページ
    }

    [Fact]
    public void Ordinals_are_dense_and_sequential()
    {
        string text = new string('a', 200);
        var opts = new ChunkingOptions { TargetSize = 50, Overlap = 10 };

        var chunks = Chunker.Chunk(new[] { Para(text) }, opts);

        chunks.Select(c => c.Ordinal).Should().Equal(Enumerable.Range(0, chunks.Count));
    }

    [Fact]
    public void Splitting_does_not_break_surrogate_pairs()
    {
        // "😀" は UTF-16 で 2 char のサロゲートペア。10 個 = 20 char。
        string text = string.Concat(Enumerable.Repeat("😀", 10));
        var opts = new ChunkingOptions { TargetSize = 5, Overlap = 0 }; // 素朴な境界はペアを割る

        var chunks = Chunker.Chunk(new[] { Para(text) }, opts);

        chunks.Should().HaveCountGreaterThan(1);
        foreach (var c in chunks)
        {
            c.Text.Should().NotBeEmpty();
            char.IsLowSurrogate(c.Text[0]).Should().BeFalse("チャンク先頭が孤立 low surrogate になってはいけない");
            char.IsHighSurrogate(c.Text[^1]).Should().BeFalse("チャンク末尾が孤立 high surrogate になってはいけない");
        }

        // overlap=0 なので単純連結で原文復元 (ペアを割らずに済んでいる)。
        string.Concat(chunks.Select(c => c.Text)).Should().Be(text);
    }

    [Fact]
    public void MaxChunkSize_force_split_does_not_break_surrogate_pairs()
    {
        string text = string.Concat(Enumerable.Repeat("😀", 8)); // 16 char
        var opts = new ChunkingOptions { TargetSize = 1000, Overlap = 0, MaxChunkSize = 5 };

        var chunks = Chunker.Chunk(new[] { new IngestedBlock(BlockKind.Table, text) }, opts);

        chunks.Should().HaveCountGreaterThan(1);
        foreach (var c in chunks)
        {
            char.IsLowSurrogate(c.Text[0]).Should().BeFalse();
            char.IsHighSurrogate(c.Text[^1]).Should().BeFalse();
        }
        string.Concat(chunks.Select(c => c.Text)).Should().Be(text);
    }

    // ── 単語境界を尊重する分割 ──

    /// <summary>cut 位置 b が語の途中でない (文書端 or 前後いずれかが空白) ことを表す。</summary>
    private static bool IsCleanBoundary(string s, int b)
        => b == 0 || b == s.Length || char.IsWhiteSpace(s[b - 1]) || char.IsWhiteSpace(s[b]);

    [Fact]
    public void Long_paragraph_with_spaces_splits_on_word_boundaries()
    {
        // 9 文字語 × 30 を半角空白で連結。窓 50 に語は収まるので常に語境界で切れるはず。
        var words = Enumerable.Range(0, 30).Select(i => new string((char)('a' + i % 26), 9));
        string text = string.Join(' ', words);
        var opts = new ChunkingOptions { TargetSize = 50, Overlap = 10 };

        var chunks = Chunker.Chunk(new[] { Para(text) }, opts);

        chunks.Should().HaveCountGreaterThan(1);
        foreach (var c in chunks)
        {
            IsCleanBoundary(text, c.CharStart).Should().BeTrue("チャンク先頭が語の途中であってはいけない");
            IsCleanBoundary(text, c.CharEnd).Should().BeTrue("チャンク末尾が語の途中であってはいけない");
            c.Text.Should().Be(text.Substring(c.CharStart, c.CharEnd - c.CharStart));
        }

        // 語境界で切っても全文は被覆される (オーバーラップ除去で原文復元)。
        Reconstruct(chunks).Should().Be(text);
    }

    [Fact]
    public void Word_longer_than_window_falls_back_to_hard_split_without_loss()
    {
        // 窓より長い 1 語 (空白なし) は語境界が無いので char 窓へフォールバックし、欠落なく被覆する。
        string longWord = new string('z', 120);
        string text = "ok " + longWord + " end";
        var opts = new ChunkingOptions { TargetSize = 40, Overlap = 0 };

        var chunks = Chunker.Chunk(new[] { Para(text) }, opts);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Text.Length <= 40);
        // overlap=0 なので単純連結で原文復元 (長語を割っても内容は失われない)。
        string.Concat(chunks.Select(c => c.Text)).Should().Be(text);
    }

    [Fact]
    public void Force_split_table_respects_word_boundaries_when_spaces_present()
    {
        var words = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i % 26), 7));
        string table = string.Join(' ', words);
        var opts = new ChunkingOptions { TargetSize = 1000, Overlap = 0, MaxChunkSize = 40 };

        var chunks = Chunker.Chunk(new[] { new IngestedBlock(BlockKind.Table, table) }, opts);

        chunks.Should().HaveCountGreaterThan(1);
        foreach (var c in chunks)
        {
            IsCleanBoundary(table, c.CharStart).Should().BeTrue();
            IsCleanBoundary(table, c.CharEnd).Should().BeTrue();
        }
        string.Concat(chunks.Select(c => c.Text)).Should().Be(table); // overlap=0 で被覆
    }

    [Fact]
    public void Invalid_options_throw()
    {
        var act = () => Chunker.Chunk(new[] { Para("x") }, new ChunkingOptions { TargetSize = 10, Overlap = 10 });
        act.Should().Throw<ArgumentException>();
    }

    // ── プロパティテスト (シード固定ランダム化、決定的) ──

    [Fact]
    public void Property_chunk_text_always_equals_source_slice()
    {
        var rng = new Random(20260612);
        for (int iter = 0; iter < 300; iter++)
        {
            var (blocks, source) = RandomParagraphDoc(rng);
            var opts = RandomOptions(rng);

            var chunks = Chunker.Chunk(blocks, opts);

            foreach (var c in chunks)
            {
                c.CharStart.Should().BeGreaterOrEqualTo(0);
                c.CharEnd.Should().BeLessOrEqualTo(source.Length);
                c.Text.Should().Be(source.Substring(c.CharStart, c.CharEnd - c.CharStart));
            }
        }
    }

    [Fact]
    public void Property_paragraph_doc_loses_no_content_and_bounds_overlap()
    {
        var rng = new Random(998877);
        for (int iter = 0; iter < 300; iter++)
        {
            var (blocks, source) = RandomParagraphDoc(rng);
            var opts = RandomOptions(rng);

            var chunks = Chunker.Chunk(blocks, opts);
            if (source.Length == 0) { chunks.Should().BeEmpty(); continue; }

            // 出力は ordinal=charStart 昇順で連続している。
            int cursor = 0;
            foreach (var c in chunks)
            {
                if (c.CharStart > cursor)
                {
                    // ギャップはブロック区切りのみであること (本文を落としていない)。
                    source.Substring(cursor, c.CharStart - cursor).Should().Be(Chunker.BlockSeparator);
                }
                else if (c.CharStart < cursor)
                {
                    // 重なりは Overlap 以内 (意図的なオーバーラップのみ)。
                    (cursor - c.CharStart).Should().BeLessOrEqualTo(opts.Overlap);
                }
                cursor = Math.Max(cursor, c.CharEnd);
            }

            // 末尾までフルカバー (末端の取りこぼし無し)。
            cursor.Should().Be(source.Length);
        }
    }

    /// <summary>オーバーラップを除去してチャンクを連結し原文を復元する (オフセットベース)。</summary>
    private static string Reconstruct(IReadOnlyList<ChunkDraft> chunks)
    {
        var sb = new System.Text.StringBuilder();
        int cursor = 0;
        foreach (var c in chunks.OrderBy(c => c.CharStart))
        {
            int from = Math.Max(c.CharStart, cursor);
            if (c.CharEnd > from) sb.Append(c.Text[(from - c.CharStart)..]);
            cursor = Math.Max(cursor, c.CharEnd);
        }
        return sb.ToString();
    }

    private static (IReadOnlyList<IngestedBlock> Blocks, string Source) RandomParagraphDoc(Random rng)
    {
        int n = rng.Next(0, 8);
        var blocks = new List<IngestedBlock>(n);
        for (int i = 0; i < n; i++)
        {
            // 非空段落のみ (空ブロックは区切りが連続しギャップ判定を曖昧にするため別扱い)。
            int len = rng.Next(1, 200);
            // 区切り文字を本文に含めない (ソーステキスト判定を曖昧にしないため)。
            var chars = new char[len];
            for (int j = 0; j < len; j++) chars[j] = (char)('a' + rng.Next(0, 5));
            blocks.Add(new IngestedBlock(BlockKind.Paragraph, new string(chars)));
        }
        string source = string.Join(Chunker.BlockSeparator, blocks.Select(b => b.Text));
        return (blocks, source);
    }

    private static ChunkingOptions RandomOptions(Random rng)
    {
        int target = rng.Next(5, 120);
        int overlap = rng.Next(0, target); // < target
        return new ChunkingOptions { TargetSize = target, Overlap = overlap };
    }
}
