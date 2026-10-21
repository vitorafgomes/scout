using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Skills;

/// <summary>
/// Embedding-driven skill selector. The skill index (loading markdown files and
/// embedding their descriptions) is built lazily on the first
/// <see cref="SelectAsync"/> call and cached for the process lifetime, so DI
/// build and host startup perform zero file I/O, embedding, or network work.
/// Never throws: returns an empty list on any failure.
/// </summary>
public sealed class SkillSelector : ISkillSelector
{
    private readonly SkillFileLoader _loader;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly SkillsOptions _options;
    private readonly ILogger<SkillSelector> _logger;
    private readonly Lazy<Task<IReadOnlyList<IndexedSkill>>> _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillSelector"/> class.
    /// </summary>
    /// <param name="loader">The skill file loader.</param>
    /// <param name="embedder">The embedding generator.</param>
    /// <param name="options">The skills options.</param>
    /// <param name="logger">The logger.</param>
    public SkillSelector(
        SkillFileLoader loader,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IOptions<SkillsOptions> options,
        ILogger<SkillSelector> logger)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException(
            "SkillsOptions must be configured.", nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Lazy + publication-only execution guarantees the index is built once,
        // on first use, never in the constructor or DI factory.
        _index = new Lazy<Task<IReadOnlyList<IndexedSkill>>>(
            BuildIndexAsync, LazyThreadSafetyMode.ExecutionAndPublication);
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
            var index = await _index.Value.ConfigureAwait(false);
            if (index.Count == 0)
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
            _logger.LogWarning("Failed to select skills ({Reason}); returning none.", ex.Message);
            _logger.LogDebug(ex, "Skill selection failure detail.");
            return [];
        }
    }

    private async Task<IReadOnlyList<IndexedSkill>> BuildIndexAsync()
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
            // A failed index build must not poison the conversation: log and act
            // as if no skills exist.
            _logger.LogWarning("Failed to build skill index ({Reason}); running with no skills.", ex.Message);
            _logger.LogDebug(ex, "Skill index build failure detail.");
            return [];
        }
    }

    private static string ResolveDirectory(string directory)
        => Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(AppContext.BaseDirectory, directory);

    private readonly record struct IndexedSkill(Skill Skill, ReadOnlyMemory<float> Vector);
}
