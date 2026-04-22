using System.Text;
using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA.Domain;

namespace Scout.Plugin.CodeQA.Chunking;

/// <summary>
/// Deterministic, line-aligned chunker that approximates tokens via UTF-8 byte
/// count. Designed for source-code retrieval: chunks never split lines, and
/// successive chunks overlap by a configurable byte budget so semantic units
/// that straddle a window boundary remain discoverable.
/// </summary>
public sealed class TokenChunker : IChunker
{
    private readonly int _chunkBytes;
    private readonly int _overlapBytes;

    public TokenChunker(IOptions<CodeQAOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var chunking = options.Value.Chunking
            ?? throw new ArgumentException("Chunking options must be present.", nameof(options));

        if (chunking.ChunkTokens < 1)
        {
            throw new ArgumentException("ChunkTokens must be >= 1.", nameof(options));
        }
        if (chunking.ApproxBytesPerToken < 1)
        {
            throw new ArgumentException("ApproxBytesPerToken must be >= 1.", nameof(options));
        }
        if (chunking.OverlapTokens < 0 || chunking.OverlapTokens >= chunking.ChunkTokens)
        {
            // Strict `<`: equal overlap would never advance the window.
            throw new ArgumentException(
                "OverlapTokens must be >= 0 and strictly less than ChunkTokens.",
                nameof(options));
        }

        _chunkBytes = chunking.ChunkTokens * chunking.ApproxBytesPerToken;
        _overlapBytes = chunking.OverlapTokens * chunking.ApproxBytesPerToken;
    }

    public IReadOnlyList<CodeChunk> Chunk(string content, string filePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("FilePath must be non-whitespace.", nameof(filePath));
        }

        if (content.Length == 0)
        {
            return Array.Empty<CodeChunk>();
        }

        var lines = ScanLines(content);
        if (lines.Count == 0)
        {
            return Array.Empty<CodeChunk>();
        }

        var chunks = new List<CodeChunk>();
        var startIndex = 0;

        while (startIndex < lines.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endIndex = startIndex;
            var accumulated = 0;

            while (endIndex < lines.Count)
            {
                var lineBytes = lines[endIndex].ByteCount;
                if (accumulated > 0 && accumulated + lineBytes > _chunkBytes)
                {
                    break;
                }

                accumulated += lineBytes;
                endIndex++;

                // Lines longer than the window are emitted whole; AST-aware splitting is post-V1.
                if (accumulated >= _chunkBytes)
                {
                    break;
                }
            }

            var lastContentIndex = endIndex - 1;
            var builder = new StringBuilder();
            for (var i = startIndex; i <= lastContentIndex; i++)
            {
                builder.Append(lines[i].Raw);
            }

            var startLine = startIndex + 1;
            var endLine = lastContentIndex + 1;
            var chunkContent = builder.ToString();
            var id = $"{filePath}#L{startLine}-L{endLine}";

            chunks.Add(new CodeChunk(
                Id: id,
                FilePath: filePath,
                StartLine: startLine,
                EndLine: endLine,
                Content: chunkContent,
                Embedding: ReadOnlyMemory<float>.Empty));

            if (endIndex >= lines.Count)
            {
                break;
            }

            var nextStart = ComputeNextStart(lines, startIndex, lastContentIndex);
            if (nextStart <= startIndex)
            {
                // Defense in depth against a zero-advance pathological case.
                nextStart = startIndex + 1;
            }
            startIndex = nextStart;
        }

        return chunks;
    }

    private int ComputeNextStart(IReadOnlyList<Line> lines, int windowStart, int lastContentIndex)
    {
        if (_overlapBytes == 0)
        {
            return lastContentIndex + 1;
        }

        var accumulated = 0;
        var cursor = lastContentIndex;
        while (cursor > windowStart && accumulated < _overlapBytes)
        {
            accumulated += lines[cursor].ByteCount;
            if (accumulated >= _overlapBytes)
            {
                break;
            }
            cursor--;
        }

        return cursor;
    }

    private static List<Line> ScanLines(string content)
    {
        var lines = new List<Line>();
        var i = 0;
        while (i < content.Length)
        {
            var start = i;
            while (i < content.Length && content[i] != '\n')
            {
                i++;
            }

            int contentEndExclusive;
            int terminatorEndExclusive;
            if (i < content.Length && content[i] == '\n')
            {
                contentEndExclusive = (i > start && content[i - 1] == '\r') ? i - 1 : i;
                terminatorEndExclusive = i + 1;
                i = terminatorEndExclusive;
            }
            else
            {
                contentEndExclusive = i;
                terminatorEndExclusive = i;
            }

            // Skip a final phantom empty line produced by a trailing newline.
            if (start == contentEndExclusive && terminatorEndExclusive == content.Length && lines.Count > 0)
            {
                break;
            }

            var raw = content.Substring(start, terminatorEndExclusive - start);
            var byteCount = Encoding.UTF8.GetByteCount(raw);
            lines.Add(new Line(raw, byteCount));
        }

        return lines;
    }

    private readonly record struct Line(string Raw, int ByteCount);
}
