using System.Reflection;
using Octokit;

namespace Scout.Plugin.GitHub.Tests.Internal;

/// <summary>
/// Reflection-based factories that build Octokit response DTOs for
/// tests. Octokit's public DTOs only expose mega-constructors with
/// 30+ positional parameters and no XML docs — listing them by hand in
/// every test would be brittle.
/// </summary>
internal static class OctokitFakes
{
    internal static Repository Repository(
        string owner = "octocat",
        string name = "Hello-World",
        string defaultBranch = "main",
        int openIssues = 0,
        int stars = 0,
        string? description = null,
        bool isPrivate = false,
        string htmlUrl = "https://github.com/octocat/Hello-World",
        DateTimeOffset? updatedAt = null)
    {
        var overrides = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = User(owner),
            ["name"] = name,
            ["fullName"] = $"{owner}/{name}",
            ["defaultBranch"] = defaultBranch,
            ["openIssuesCount"] = openIssues,
            ["stargazersCount"] = stars,
            ["description"] = description,
            ["private"] = isPrivate,
            ["htmlUrl"] = htmlUrl,
            ["updatedAt"] = updatedAt ?? DateTimeOffset.UtcNow,
        };
        return Build<Repository>(overrides);
    }

    internal static User User(string login = "octocat")
    {
        return Build<User>(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["login"] = login,
        });
    }

    internal static PullRequest PullRequest(
        int number = 1,
        string title = "PR",
        string? body = null,
        ItemState state = ItemState.Open,
        string author = "octocat",
        string baseRef = "main",
        string headRef = "feat",
        bool draft = false,
        int comments = 0,
        int additions = 0,
        int deletions = 0,
        int changedFiles = 0,
        bool? mergeable = null,
        string htmlUrl = "https://github.com/octocat/Hello-World/pull/1")
    {
        return Build<PullRequest>(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["number"] = number,
            ["title"] = title,
            ["body"] = body,
            ["state"] = state,
            ["user"] = User(author),
            ["base"] = Build<GitReference>(new() { ["ref"] = baseRef }),
            ["head"] = Build<GitReference>(new() { ["ref"] = headRef }),
            ["draft"] = draft,
            ["comments"] = comments,
            ["additions"] = additions,
            ["deletions"] = deletions,
            ["changedFiles"] = changedFiles,
            ["mergeable"] = mergeable,
            ["mergeableState"] = MergeableState.Clean,
            ["htmlUrl"] = htmlUrl,
            ["updatedAt"] = DateTimeOffset.UtcNow,
        });
    }

    internal static PullRequestFile PullRequestFile(
        string fileName = "src/foo.cs",
        string status = "modified",
        int additions = 1,
        int deletions = 0)
    {
        return Build<PullRequestFile>(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["fileName"] = fileName,
            ["status"] = status,
            ["additions"] = additions,
            ["deletions"] = deletions,
        });
    }

    internal static Issue Issue(
        int number = 1,
        string title = "Issue",
        string? body = null,
        bool isPullRequest = false,
        IEnumerable<string>? labels = null,
        IEnumerable<string>? assignees = null,
        string author = "octocat",
        string htmlUrl = "https://github.com/octocat/Hello-World/issues/1")
    {
        var labelObjs = (labels ?? Array.Empty<string>())
            .Select(n => Build<Label>(new() { ["name"] = n }))
            .ToArray();
        var assigneeObjs = (assignees ?? Array.Empty<string>())
            .Select(login => User(login))
            .ToArray();

        return Build<Issue>(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["number"] = number,
            ["title"] = title,
            ["body"] = body,
            ["state"] = ItemState.Open,
            ["user"] = User(author),
            ["labels"] = (IReadOnlyList<Label>)labelObjs,
            ["assignees"] = (IReadOnlyList<User>)assigneeObjs,
            ["comments"] = 0,
            ["createdAt"] = DateTimeOffset.UtcNow,
            ["updatedAt"] = DateTimeOffset.UtcNow,
            ["pullRequest"] = isPullRequest ? PullRequest() : null,
            ["htmlUrl"] = htmlUrl,
        });
    }

    private static T Build<T>(Dictionary<string, object?> overrides)
    {
        var ctor = typeof(T)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault() ?? throw new InvalidOperationException(
                $"No public ctor for {typeof(T).FullName}");

        var args = ctor.GetParameters().Select(p =>
        {
            if (overrides.TryGetValue(p.Name ?? string.Empty, out var value))
            {
                return value;
            }
            return Default(p.ParameterType);
        }).ToArray();

        return (T)ctor.Invoke(args);
    }

    private static object? Default(Type t)
    {
        if (!t.IsValueType)
        {
            // Empty arrays for IReadOnlyList<T>/IList<T>/T[].
            if (t.IsArray)
            {
                return Array.CreateInstance(t.GetElementType()!, 0);
            }
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>))
                {
                    var elem = t.GetGenericArguments()[0];
                    return Array.CreateInstance(elem, 0);
                }
            }
            return null;
        }

        // Nullable<T> default = null.
        if (Nullable.GetUnderlyingType(t) is not null)
        {
            return null;
        }

        return Activator.CreateInstance(t);
    }
}
