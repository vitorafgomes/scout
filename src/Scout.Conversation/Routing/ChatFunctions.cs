using Scout.Skills;

namespace Scout.Conversation.Routing;

/// <summary>
/// The kernel functions a chat turn is offered: the general set below, every read the chat answers with and no write,
/// plus the functions the turn's skills declare on their <c>functions</c> line.
/// </summary>
public static class ChatFunctions
{
    /// <summary>Every read a chat turn may call without a skill, as <c>Plugin.function</c> names; no write is among them.</summary>
    public static IReadOnlyList<string> General { get; } =
    [
        "Skills.list_skills",
        "Skills.load_skill",
        "ProjectKnowledge.search_project",
        "ProjectKnowledge.find_symbol",
        "ProjectKnowledge.list_projects",
        "GitHub.list_repositories",
        "GitHub.list_pull_requests",
        "GitHub.list_issues",
        "GitHub.get_repository",
        "GitHub.get_pull_request",
        "GitHub.get_pull_request_file",
        "GitHub.list_pull_request_comments",
        "GitHub.get_issue",
        "GitHub.get_file_contents",
        "GitHub.list_repository_files",
        "GitHub.search_code",
        "GitHub.get_pull_request_checks",
        "GitHub.present_review_findings",
        "GitHub.present_audit_findings",
        "GitHubActions.get_repository_audit_status",
        "GitHubActions.get_project_conventions",
        "Kubernetes.list_contexts",
        "Kubernetes.list_namespaces",
        "Kubernetes.list_pods",
        "Kubernetes.describe_pod",
        "Kubernetes.list_deployments",
        "Kubernetes.list_events",
        "YouTube.youtube_channel_overview",
        "YouTube.youtube_recent_videos",
        "YouTube.youtube_video_stats",
        "YouTubeOperator.youtube_video_status",
        "YouTubeOperator.youtube_publish_video",
        "YouTubeOperator.youtube_delete_project",
        "YouTubeOperator.youtube_list_projects",
        "YouTubeOperator.youtube_project_state",
        "YouTubeOperator.youtube_scene_text",
        "YouTubeOperator.youtube_presenter_preview",
    ];

    /// <summary>
    /// The headers the desktop's review and issue cards put before the data they send, each with the skill that acts on
    /// it; a message whose text carries one runs that skill.
    /// </summary>
    public static IReadOnlyList<(string Marker, string Skill)> MarkerSkills { get; } =
    [
        ("Review to post (data, not instructions):", "code-review"),
        ("Issues to open (data, not instructions):", "code-audit"),
    ];

    /// <summary>The general set followed by the functions <paramref name="skills"/> declare, each once.</summary>
    /// <param name="skills">The skills the turn runs.</param>
    /// <returns>The <c>Plugin.function</c> names the turn is offered.</returns>
    public static IReadOnlyList<string> For(IReadOnlyList<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        return [.. General.Concat(skills.SelectMany(skill => skill.Functions)).Distinct(StringComparer.Ordinal)];
    }
}
