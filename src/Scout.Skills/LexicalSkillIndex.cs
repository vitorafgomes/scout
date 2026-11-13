using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Scout.Skills;

/// <summary>
/// Picks a skill by the words of a message, for a turn with no embeddings to compare: no model call and nothing leaves
/// the process. Words are lower-cased with accents folded, short, numeric and common English and Portuguese words are
/// dropped, and two words match when they are equal or one, of at least four letters, starts the other. Each skill is
/// indexed by its name (counted three times) and its description up to its first "Not for" clause. A message scores a
/// skill by the share of its known words, each weighted by how few skills it matches, that the skill matches; the best
/// skills at or above <see cref="Threshold"/>, most matches first on a tie, are picked, and only those that declare
/// functions are returned, so a skill that changes nothing the turn is offered stays for the model to load. A message
/// that names a pull request is first decided by <see cref="ReviewOrAudit"/>.
/// </summary>
internal sealed partial class LexicalSkillIndex
{
    /// <summary>The share of the message's known words a skill must match to be picked.</summary>
    internal const double Threshold = 0.55;

    private const int NameRepeat = 3;
    private const int PrefixLength = 4;
    private const string NotFor = "Not for";

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "an", "the", "of", "to", "in", "on", "for", "and", "or", "is", "are", "was", "be", "it", "its", "this", "that",
        "these", "those", "what", "which", "who", "how", "why", "when", "where", "do", "does", "did", "can", "could",
        "would", "should", "will", "me", "my", "mine", "you", "your", "we", "our", "us", "with", "from", "by", "at", "as",
        "about", "into", "please", "tell", "show", "give", "find", "look", "see", "there", "here", "any", "some", "all",
        "just", "not", "no", "yes", "ok", "okay", "hi", "hello", "thanks", "thank",
        "os", "um", "uma", "uns", "umas", "de", "da", "do", "das", "dos", "em", "no", "na", "nos", "nas", "ou", "que",
        "qual", "quais", "quem", "como", "por", "porque", "quando", "onde", "para", "pra", "pro", "com", "sem", "se",
        "sobre", "esse", "essa", "esses", "essas", "este", "esta", "isto", "isso", "aquilo", "meu", "minha", "meus",
        "minhas", "seu", "sua", "voce", "vc", "eu", "ele", "ela", "tem", "ter", "ser", "estar", "sao", "foi", "faz",
        "fazer", "favor", "oi", "ola", "obrigado", "obrigada", "tudo", "bem", "aqui", "ai", "la", "ja", "nao", "sim",
        "mais", "muito", "pouco",
        "scout", "repository", "repositories", "repositorio", "repositorios", "repo", "repos", "project", "projects",
        "projeto", "projetos", "code", "codigo",
    };

    private readonly List<(Skill Skill, string[] Words)> _skills;

    /// <summary>Indexes <paramref name="skills"/> by their names and descriptions.</summary>
    /// <param name="skills">The loaded skills.</param>
    public LexicalSkillIndex(IReadOnlyList<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        _skills = [.. skills.Select(skill => (skill, Indexed(skill)))];
    }

    /// <summary>The skills whose words best match <paramref name="message"/>, at most <paramref name="topN"/>, that declare functions.</summary>
    /// <param name="message">The user message.</param>
    /// <param name="topN">How many of the best skills to consider.</param>
    /// <returns>The picked skills, possibly none.</returns>
    public IReadOnlyList<Skill> Select(string message, int topN)
    {
        var (pick, excluded) = ReviewOrAudit(message ?? string.Empty);
        if (pick is not null && topN > 0
            && _skills.Select(skill => skill.Skill).FirstOrDefault(skill => IsNamed(skill, pick) && skill.Functions.Count > 0) is { } picked)
        {
            return [picked];
        }

        var words = Words(message ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        var hits = _skills
            .Select(skill => words.Select(word => skill.Words.Count(indexed => Matches(word, indexed))).ToArray())
            .ToArray();
        var weights = new double[words.Length];
        for (var w = 0; w < words.Length; w++)
        {
            var count = hits.Count(skill => skill[w] > 0);
            weights[w] = count > 0 ? Math.Log(1 + ((double)_skills.Count / count)) : 0;
        }

        var total = weights.Sum();
        if (total <= 0)
        {
            return [];
        }

        return
        [
            .. _skills
                .Select((skill, s) => (
                    skill.Skill,
                    Score: hits[s].Select((hit, w) => hit > 0 ? weights[w] : 0).Sum() / total,
                    Hits: hits[s].Sum()))
                .Where(scored => scored.Score >= Threshold && !IsNamed(scored.Skill, excluded))
                .OrderByDescending(scored => scored.Score)
                .ThenByDescending(scored => scored.Hits)
                .Take(Math.Max(0, topN))
                .Where(scored => scored.Skill.Functions.Count > 0)
                .Select(scored => scored.Skill),
        ];
    }

    /// <summary>
    /// Decides between code-review and code-audit before any score is compared, for a message that names a pull request
    /// ("PR 5", "pull request 5", "os PRs abertos", a pull request link, an owner/repo reference with a number): asked for a
    /// review of the whole repository or project in so many words ("audita o repositório", "review the whole
    /// repository", "a review do projeto inteiro") it is an audit; otherwise asked for a review or an analysis it is a
    /// review of that pull request, whatever else it asks to look for; and either way the audit is never picked by its
    /// score. A message that names no pull request is left to the scores.
    /// </summary>
    /// <param name="message">The user message.</param>
    /// <returns>The skill the message picks outright, if any, and the skill it may not pick by score, if any.</returns>
    internal static (string? Pick, string? Excluded) ReviewOrAudit(string message)
    {
        var folded = Fold(message);
        if (!PullRequest().IsMatch(folded))
        {
            return (null, null);
        }

        if (WholeRepository().IsMatch(folded))
        {
            return ("code-audit", null);
        }

        return (ReviewVerb().IsMatch(folded) ? "code-review" : null, "code-audit");
    }

    private static bool IsNamed(Skill skill, string? name)
        => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\bprs?\b|\bpull[\s-]*requests?\b|/pull/\d|[\w.-]+/[\w.-]+#\d", RegexOptions.CultureInvariant)]
    private static partial Regex PullRequest();

    [GeneratedRegex(@"\b(?:review|revis|analis|analy[sz]|audit)\w*", RegexOptions.CultureInvariant)]
    private static partial Regex ReviewVerb();

    [GeneratedRegex(@"\b(?:review|revis|analis|analy[sz]|audit)\w*\W+(?:\w+\W+){0,3}?(?:(?:whole|entire)\s+(?:repo|repository|project|codebase)|(?:repositorio|projeto|codigo)\s+(?:todo|inteiro)|todo\s+o\s+(?:repositorio|projeto|codigo))\b|\baudit\w*\s+(?:(?:the|this|o|a|esse|este|do|da)\s+)?(?:repo|repository|repositorio|project|projeto|codebase)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WholeRepository();

    private static string[] Indexed(Skill skill)
    {
        var description = skill.Description;
        var notFor = description.IndexOf(NotFor, StringComparison.Ordinal);
        if (notFor >= 0)
        {
            description = description[..notFor];
        }

        var name = Words(skill.Name.Replace('-', ' ')).ToList();
        return [.. Enumerable.Repeat(name, NameRepeat).SelectMany(words => words), .. Words(description)];
    }

    private static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(c);
            }
        }

        return folded.ToString();
    }

    private static IEnumerable<string> Words(string text)
        => new string([.. Fold(text).Select(c => char.IsLetterOrDigit(c) ? c : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 2 && !word.All(char.IsDigit) && !Ignored.Contains(word));

    private static bool Matches(string a, string b)
    {
        if (a.Length == b.Length)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        return shorter.Length >= PrefixLength && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the message names the audit (audit, auditoria, audita and their forms) or asks in so many words for the
    /// whole repository or project, the whole-repository words being the same that make a named pull request an audit.
    /// </summary>
    /// <param name="message">The user message.</param>
    /// <returns>True when the message names the audit or asks for the whole repository.</returns>
    internal static bool AsksForAnAudit(string message)
    {
        var folded = Fold(message ?? string.Empty);
        return AuditWord().IsMatch(folded) || WholeRepository().IsMatch(folded);
    }

    [GeneratedRegex(@"\baudit\w*", RegexOptions.CultureInvariant)]
    private static partial Regex AuditWord();
}
