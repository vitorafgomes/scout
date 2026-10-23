using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Skills;

/// <summary>
/// Embedding-driven skill selector. The skill index (loading markdown files and
/// embedding their descriptions) is built lazily on the first
/// <see cref="SelectAsync"/> call, so DI build and host startup perform zero
/// file I/O, embedding, or network work. A successful build is cached for the
/// process lifetime; a failed build is not, and is attempted again on a later
/// call once <see cref="IndexRetryInterval"/> has passed.
/// Never throws: returns an empty list on any failure.
/// </summary>
public sealed class SkillSelector : ISkillSelector
{
    /// <summary>Minimum wait between a failed index build and the next attempt.</summary>
    // ponytail: fixed retry interval; move to SkillsOptions if an operator needs to tune it.
    internal static readonly TimeSpan IndexRetryInterval = TimeSpan.FromMinutes(1);

    private readonly SkillFileLoader _loader;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly SkillsOptions _options;
    private readonly ILogger<SkillSelector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _indexGate = new();
    private Task<IReadOnlyList<IndexedSkill>?>? _index;
    private DateTimeOffset _retryAfter;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillSelector"/> class.
    /// </summary>
    /// <param name="loader">The skill file loader.</param>
    /// <param name="embedder">The embedding generator.</param>
    /// <param name="options">The skills options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock that paces index rebuild attempts; defaults to the system clock.</param>
    public SkillSelector(
        SkillFileLoader loader,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IOptions<SkillsOptions> options,
        ILogger<SkillSelector> logger,
        TimeProvider? timeProvider = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException(
            "SkillsOptions must be configured.", nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Skill>> SelectAsync(
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        try
        {
            var index = await GetIndexAsync().ConfigureAwait(false);
            if (index is null || index.Count == 0)
            {
                return [];
            }

            var queryVector = await _embedder
                .GenerateVectorAsync(message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var query = queryVector;

            var ranked = index
                .Select(s => (s.Skill, Score: VectorMath.Cosine(query.Span, s.Vector.Span)))
                .Where(x => x.Score >= _options.SimilarityThreshold)
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(0, _options.TopN))
                .Select(x => x.Skill)
                .ToList();

            return ranked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to select skills; returning none.");
            return [];
        }
    }

    private Task<IReadOnlyList<IndexedSkill>?> GetIndexAsync()
    {
        lock (_indexGate)
        {
            if (_index is { IsCompletedSuccessfully: true, Result: null }
                && _timeProvider.GetUtcNow() >= _retryAfter)
            {
                _index = null;
            }

            return _index ??= BuildIndexAsync();
        }
    }

    private async Task<IReadOnlyList<IndexedSkill>?> BuildIndexAsync()
    {
        try
        {
            var directory = ResolveDirectory(_options.Directory);
            var skills = _loader.Load(directory);
            if (skills.Count == 0)
            {
                _logger.LogInformation("No skills found in {Directory}.", directory);
                return [];
            }

            var descriptions = skills.Select(s => s.Description).ToList();
            var embeddings = await _embedder
                .GenerateAsync(descriptions, options: null, CancellationToken.None)
                .ConfigureAwait(false);

            var indexed = new List<IndexedSkill>(skills.Count);
            for (var i = 0; i < skills.Count; i++)
            {
                indexed.Add(new IndexedSkill(skills[i], embeddings[i].Vector));
            }

            _logger.LogInformation("Indexed {Count} skill(s) from {Directory}.", indexed.Count, directory);
            return indexed;
        }
        catch (Exception ex)
        {
            _retryAfter = _timeProvider.GetUtcNow() + IndexRetryInterval;
            _logger.LogWarning(
                ex,
                "Failed to build skill index; running with no skills until a retry after {RetryAfter}.",
                _retryAfter);
            return null;
        }
    }

    private static string ResolveDirectory(string directory)
        => Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(AppContext.BaseDirectory, directory);

    private readonly record struct IndexedSkill(Skill Skill, ReadOnlyMemory<float> Vector);
}
