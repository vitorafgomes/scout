using System.Net;
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
/// call once <see cref="IndexRetryInterval"/> has passed; an embedding model the AI Source answers HTTP 404 for, on the
/// index build or on a query, is logged once, by name, without a stack. When the provider has no
/// embeddings (<see cref="NotSupportedException"/>), it selects nothing for the
/// process, never retries, and offers the loaded skills as
/// <see cref="ModelChoiceCatalog"/> so the model chooses one itself. Whenever the
/// embeddings cannot answer (none, a failed index build, a failed query embedding)
/// a skill that declares functions is picked by the words of the message instead
/// (<see cref="LexicalSkillIndex"/>), so the turn is still offered its functions; after a
/// failed query embedding the messages of the next <see cref="IndexRetryInterval"/> go
/// straight to the words, so a hanging embedder costs one timeout, not one per call.
/// On both paths a message that names a pull request is first decided by
/// <see cref="LexicalSkillIndex.ReviewOrAudit"/>, before any score is compared.
/// Never throws: returns an empty list on any other failure.
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
    private bool _buildFailureLogged;
    private int _missingModelLogged;
    private IReadOnlyList<Skill> _modelChoiceCatalog = [];
    private LexicalSkillIndex? _byWords;
    private long _queryRetryAfterTicks;

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
    public IReadOnlyList<Skill> ModelChoiceCatalog => Volatile.Read(ref _modelChoiceCatalog);

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
            if (index is null)
            {
                return ByWords(message);
            }

            if (index.Count == 0)
            {
                return [];
            }

            var (pick, excluded) = LexicalSkillIndex.ReviewOrAudit(message);
            if (pick is not null && _options.TopN > 0
                && index.FirstOrDefault(s => string.Equals(s.Skill.Name, pick, StringComparison.OrdinalIgnoreCase)) is { Skill: { } picked })
            {
                return [picked];
            }

            if (_timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref _queryRetryAfterTicks))
            {
                return ByWords(message);
            }

            var queryVector = await _embedder
                .GenerateVectorAsync(message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var query = queryVector;

            var ranked = index
                .Where(s => !string.Equals(s.Skill.Name, excluded, StringComparison.OrdinalIgnoreCase))
                .Select(s => (s.Skill, Score: VectorMath.Cosine(query.Span, s.Vector.Span)))
                .Where(x => x.Score >= _options.SimilarityThreshold)
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(0, _options.TopN))
                .Select(x => x.Skill)
                .ToList();

            return ranked;
        }
        catch (HttpRequestException missing) when (missing.StatusCode == HttpStatusCode.NotFound)
        {
            Volatile.Write(ref _queryRetryAfterTicks, (_timeProvider.GetUtcNow() + IndexRetryInterval).UtcTicks);
            LogMissingModelOnce();
            return ByWords(message);
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _queryRetryAfterTicks, (_timeProvider.GetUtcNow() + IndexRetryInterval).UtcTicks);
            }

            _logger.LogWarning(ex, "Failed to select skills by embeddings; picking by the words of the message.");
            return ByWords(message);
        }
    }

    private void LogMissingModelOnce()
    {
        if (Interlocked.Exchange(ref _missingModelLogged, 1) == 0)
        {
            _logger.LogWarning(
                "The embedding model {Model} is not installed on the AI Source (HTTP 404); skills are picked by the words of the message until it is. Download it in Scout Desktop, AI Sources.",
                _embedder.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId ?? "unknown");
        }
    }

    private IReadOnlyList<Skill> ByWords(string message)
        => Volatile.Read(ref _byWords)?.Select(message, _options.TopN) ?? [];

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
        IReadOnlyList<Skill> skills = [];
        try
        {
            var directory = ResolveDirectory(_options.Directory);
            skills = _loader.Load(directory);
            Volatile.Write(ref _byWords, new LexicalSkillIndex(skills));
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
        catch (NotSupportedException)
        {
            _retryAfter = DateTimeOffset.MaxValue;
            Volatile.Write(ref _modelChoiceCatalog, skills);
            _logger.LogInformation(
                "Skill auto-selection without embeddings: the AI provider has none, so a skill that declares functions is picked by the words of the message and the skill catalog is in the prompt for the model to load the others.");
            return null;
        }
        catch (HttpRequestException missing) when (missing.StatusCode == HttpStatusCode.NotFound)
        {
            _retryAfter = _timeProvider.GetUtcNow() + IndexRetryInterval;
            LogMissingModelOnce();
            return null;
        }
        catch (Exception ex)
        {
            _retryAfter = _timeProvider.GetUtcNow() + IndexRetryInterval;
            if (_buildFailureLogged)
            {
                _logger.LogWarning(
                    "Skill index build failed again ({Reason}); retrying after {RetryAfter}.",
                    ex.Message,
                    _retryAfter);
                return null;
            }

            _buildFailureLogged = true;
            _logger.LogWarning(
                ex,
                "Failed to build skill index; picking skills by the words of the message until a retry after {RetryAfter}.",
                _retryAfter);
            return null;
        }
    }

    private static string ResolveDirectory(string directory)
        => Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(AppContext.BaseDirectory, directory);

    /// <summary>
    /// Whether the message names the audit ("stop the audit", "continua a auditoria", "audita o acme/shop") or asks in so
    /// many words for the whole repository or project ("revisa o projeto inteiro"), the only words that turn a review into an audit.
    /// </summary>
    /// <param name="message">The user message, without its context data.</param>
    /// <returns>True when the message names the audit or asks for the whole repository.</returns>
    public static bool AsksForAnAudit(string message) => LexicalSkillIndex.AsksForAnAudit(message);

    private readonly record struct IndexedSkill(Skill Skill, ReadOnlyMemory<float> Vector);
}
