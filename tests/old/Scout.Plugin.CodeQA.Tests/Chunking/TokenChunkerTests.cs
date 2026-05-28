using System.Text;
using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA;
using Scout.Plugin.CodeQA.Chunking;

namespace Scout.Plugin.CodeQA.Tests.Chunking;

public class TokenChunkerTests
{
    private static TokenChunker BuildChunker(CodeQAOptions? options = null)
    {
        return new TokenChunker(Options.Create(options ?? new CodeQAOptions()));
    }

    private static string BuildLines(int count, string line, string terminator = "\n")
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Append(line);
            sb.Append(terminator);
        }
        return sb.ToString();
    }

    [Fact]
    public void Chunk_NullContent_Throws()
    {
        var chunker = BuildChunker();

        Action act = () => chunker.Chunk(null!, "file.cs", CancellationToken.None);

        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Chunk_NullFilePath_Throws()
    {
        var chunker = BuildChunker();

        Action act = () => chunker.Chunk("content", null!, CancellationToken.None);

        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Chunk_WhitespaceFilePath_Throws()
    {
        var chunker = BuildChunker();

        Action act = () => chunker.Chunk("content", "   ", CancellationToken.None);

        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Ctor_InvalidChunkTokens_Throws()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 0;

        Action act = () => new TokenChunker(Options.Create(options));

        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Ctor_OverlapGreaterThanOrEqualChunk_Throws()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 10;
        options.Chunking.OverlapTokens = 10;

        Action act = () => new TokenChunker(Options.Create(options));

        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Ctor_InvalidApproxBytesPerToken_Throws()
    {
        var options = new CodeQAOptions();
        options.Chunking.ApproxBytesPerToken = 0;

        Action act = () => new TokenChunker(Options.Create(options));

        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Ctor_ChunkBytesOverflow_Throws()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = int.MaxValue;
        options.Chunking.ApproxBytesPerToken = 2;
        options.Chunking.OverlapTokens = 0;

        Should.Throw<ArgumentException>(() => new TokenChunker(Options.Create(options)));
    }

    [Fact]
    public void Chunk_EmptyContent_ReturnsEmptyList()
    {
        var chunker = BuildChunker();

        var result = chunker.Chunk(string.Empty, "file.cs", CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Chunk_SingleLineShorterThanWindow_ReturnsOneChunkSpanningLineOne()
    {
        var chunker = BuildChunker();

        var result = chunker.Chunk("hello world", "file.cs", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(1);
        result[0].EndLine.ShouldBe(1);
        result[0].Content.ShouldBe("hello world");
    }

    [Fact]
    public void Chunk_SingleLineWithTrailingNewline_EndLineIsOne()
    {
        var chunker = BuildChunker();

        var result = chunker.Chunk("hello\n", "file.cs", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(1);
        result[0].EndLine.ShouldBe(1);
    }

    [Fact]
    public void Chunk_MultiLineWithinWindow_ReturnsSingleChunkWithFullLineRange()
    {
        var chunker = BuildChunker();
        var content = "line one\nline two\nline three\n";

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(1);
        result[0].EndLine.ShouldBe(3);
        result[0].Content.ShouldBe(content);
    }

    [Fact]
    public void Chunk_ContentExceedingWindow_SplitsIntoMultipleChunks()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 10;
        options.Chunking.OverlapTokens = 2;
        options.Chunking.ApproxBytesPerToken = 4;
        var chunker = BuildChunker(options);

        // Window = 40 bytes, 20 lines of "abcdefghij" (10 chars + \n = 11 bytes each).
        var content = BuildLines(20, "abcdefghij");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Chunk_ExactlyFillsOneWindow_ReturnsOneChunk()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 4;
        options.Chunking.OverlapTokens = 0;
        options.Chunking.ApproxBytesPerToken = 4;
        var chunker = BuildChunker(options);

        // Window = 16 bytes; one line of 15 chars + \n = 16 bytes.
        var content = "abcdefghijklmno\n";

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(1);
        result[0].EndLine.ShouldBe(1);
    }

    [Fact]
    public void Chunk_TwoWindowsExactBoundary_ReturnsTwoChunks()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 4;
        options.Chunking.OverlapTokens = 0;
        options.Chunking.ApproxBytesPerToken = 4;
        var chunker = BuildChunker(options);

        // Window = 16 bytes. Two lines of 15 chars + \n = 16 bytes each.
        var content = "abcdefghijklmno\nABCDEFGHIJKLMNO\n";

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].StartLine.ShouldBe(1);
        result[0].EndLine.ShouldBe(1);
        result[1].StartLine.ShouldBe(2);
        result[1].EndLine.ShouldBe(2);
    }

    [Fact]
    public void Chunk_OverlapProducesSharedLines()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 50;
        options.Chunking.OverlapTokens = 20;
        options.Chunking.ApproxBytesPerToken = 4;
        var chunker = BuildChunker(options);

        // 40 lines, each 9 chars + \n = 10 bytes.
        var content = BuildLines(40, "short-lin");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBeGreaterThan(1);
        for (var i = 1; i < result.Count; i++)
        {
            result[i].StartLine.ShouldBeLessThanOrEqualTo(result[i - 1].EndLine);
        }
    }

    [Fact]
    public void Chunk_ZeroOverlap_ChunksAreContiguousNonOverlapping()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 10;
        options.Chunking.OverlapTokens = 0;
        options.Chunking.ApproxBytesPerToken = 4;
        var chunker = BuildChunker(options);

        var content = BuildLines(30, "abcdefghi");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBeGreaterThan(1);
        for (var i = 1; i < result.Count; i++)
        {
            result[i].StartLine.ShouldBe(result[i - 1].EndLine + 1);
        }
    }

    [Fact]
    public void Chunk_LargeFile_ProducesManyChunksInOrder()
    {
        var chunker = BuildChunker();
        var content = BuildLines(5000, "public int Foo() => 42;");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBeGreaterThan(10);
        for (var i = 1; i < result.Count; i++)
        {
            result[i].StartLine.ShouldBeGreaterThan(result[i - 1].StartLine);
        }
    }

    [Fact]
    public void Chunk_SameInputTwice_ProducesIdenticalChunks()
    {
        var chunker = BuildChunker();
        var content = BuildLines(200, "public int Foo() => 42;");

        var first = chunker.Chunk(content, "file.cs", CancellationToken.None);
        var second = chunker.Chunk(content, "file.cs", CancellationToken.None);

        first.Count.ShouldBe(second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            first[i].Id.ShouldBe(second[i].Id);
            first[i].Content.ShouldBe(second[i].Content);
            first[i].StartLine.ShouldBe(second[i].StartLine);
            first[i].EndLine.ShouldBe(second[i].EndLine);
        }
    }

    [Fact]
    public void Chunk_IdFormat_IsFilePathHashLineRange()
    {
        var chunker = BuildChunker();
        var content = "alpha\nbeta\ngamma\n";

        var result = chunker.Chunk(content, "src/File.cs", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("src/File.cs#L1-L3");
    }

    [Fact]
    public void Chunk_CrlfLineEndings_ProducesSameLineCountsAsLf()
    {
        var chunker = BuildChunker();
        var lf = "alpha\nbeta\ngamma\n";
        var crlf = "alpha\r\nbeta\r\ngamma\r\n";

        var lfResult = chunker.Chunk(lf, "file.cs", CancellationToken.None);
        var crlfResult = chunker.Chunk(crlf, "file.cs", CancellationToken.None);

        lfResult.Count.ShouldBe(crlfResult.Count);
        lfResult[0].StartLine.ShouldBe(crlfResult[0].StartLine);
        lfResult[0].EndLine.ShouldBe(crlfResult[0].EndLine);
    }

    [Fact]
    public void Chunk_LeadingBom_DoesNotOffsetLineNumbers()
    {
        var chunker = BuildChunker();
        var content = "﻿alpha\nbeta\n";

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(1);
        result[0].EndLine.ShouldBe(2);
    }

    [Fact]
    public void Chunk_NoTrailingNewline_LastChunkEndLineIsTotalLineCount()
    {
        var chunker = BuildChunker();
        var content = "alpha\nbeta\ngamma";

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result[^1].EndLine.ShouldBe(3);
    }

    [Fact]
    public void Chunk_TrailingNewline_LastChunkEndLineIsLastNonEmptyLine()
    {
        var chunker = BuildChunker();
        var content = "a\nb\n";

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].EndLine.ShouldBe(2);
    }

    [Fact]
    public void Chunk_TrailingBlankLine_IsPreserved()
    {
        var chunker = BuildChunker();
        var content = "a\n\n";

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(1);
        result[0].EndLine.ShouldBe(2);
    }

    [Fact]
    public void Chunk_MultiByteCharacters_RespectsByteBudgetNotCharCount()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 50;
        options.Chunking.OverlapTokens = 0;
        options.Chunking.ApproxBytesPerToken = 4;
        var chunker = BuildChunker(options);

        // Each Japanese line is 21 UTF-8 bytes + newline = 22 bytes, well under the 200-byte budget.
        var content = BuildLines(500, "日本語の行です");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        var budget = options.Chunking.ChunkTokens * options.Chunking.ApproxBytesPerToken;
        foreach (var chunk in result)
        {
            var chunkBytes = Encoding.UTF8.GetByteCount(chunk.Content);
            chunkBytes.ShouldBeLessThanOrEqualTo(budget);
        }
    }

    [Fact]
    public void Chunk_SingleLineLongerThanWindow_EmittedWholeInOwnChunk()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 4;
        options.Chunking.OverlapTokens = 0;
        options.Chunking.ApproxBytesPerToken = 4;
        var chunker = BuildChunker(options);

        // Window = 16 bytes. One 100-char line easily overflows.
        var longLine = new string('x', 100);
        var content = longLine + "\n" + "short\n";

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].StartLine.ShouldBe(1);
        result[0].EndLine.ShouldBe(1);
        result[0].Content.ShouldContain(longLine);
    }

    [Fact]
    public void Chunk_ConcatenatingChunkContentsReproducesOriginal_WhenOverlapIsZero()
    {
        var options = new CodeQAOptions();
        options.Chunking.ChunkTokens = 10;
        options.Chunking.OverlapTokens = 0;
        options.Chunking.ApproxBytesPerToken = 4;
        var chunker = BuildChunker(options);

        var content = BuildLines(50, "abcdefghi");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        var rebuilt = string.Concat(result.Select(c => c.Content));
        rebuilt.ShouldBe(content);
    }

    [Fact]
    public void Chunk_AllChunks_HaveNonEmptyContent()
    {
        var chunker = BuildChunker();
        var content = BuildLines(500, "some content line");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        foreach (var chunk in result)
        {
            chunk.Content.ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Chunk_AllChunks_HaveStartLineLessThanOrEqualEndLine()
    {
        var chunker = BuildChunker();
        var content = BuildLines(500, "some content line");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        foreach (var chunk in result)
        {
            chunk.StartLine.ShouldBeLessThanOrEqualTo(chunk.EndLine);
        }
    }

    [Fact]
    public void Chunk_AllChunks_HaveEmbeddingEmpty()
    {
        var chunker = BuildChunker();
        var content = BuildLines(500, "some content line");

        var result = chunker.Chunk(content, "file.cs", CancellationToken.None);

        foreach (var chunk in result)
        {
            chunk.Embedding.IsEmpty.ShouldBeTrue();
        }
    }

    [Fact]
    public void Chunk_CancelledToken_ThrowsOperationCanceledException()
    {
        var chunker = BuildChunker();
        var content = BuildLines(5000, "public int Foo() => 42;");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => chunker.Chunk(content, "file.cs", cts.Token);

        act.ShouldThrow<OperationCanceledException>();
    }
}
