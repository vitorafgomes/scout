/** The repository an issues question names at its end, "Open issues on owner/repo", as written. */
export const ISSUES_TARGET = / on ([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)$/;
const ISSUE_URL = /https:\/\/github\.com\/([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)\/issues\/\d+/g;

/** The distinct links of the issues on `target` in a reply, the repository compared without case. */
export function issueLinks(target: string | undefined, reply: string): readonly string[] {
  if (target === undefined) return [];
  const wanted = target.toLowerCase();
  const found = [...reply.matchAll(ISSUE_URL)].filter((m) => m[1]!.toLowerCase() === wanted);
  return [...new Set(found.map((m) => m[0]))];
}
