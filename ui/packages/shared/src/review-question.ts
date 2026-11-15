const PULL_REQUEST_URL =
  /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/pull\/\d+(?=$|#)/;

/** The question that asks Scout to review the pull request `url` links to, or null when it links to none. */
export function reviewQuestion(url: string | undefined): string | null {
  const pullRequest = url === undefined ? undefined : PULL_REQUEST_URL.exec(url)?.[0];
  return pullRequest === undefined ? null : `Review the pull request ${pullRequest}`;
}
