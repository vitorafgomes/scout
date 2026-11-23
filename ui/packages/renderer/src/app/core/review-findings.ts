import {
  ISSUES_MARKER,
  type ReviewFinding,
  type ReviewFindings,
  type SelectedFinding,
} from '@scout/shared';

/** The GitHub shortcode an issue opens its problem with, by severity; GitHub draws it, no file holds the character. */
const MARKS: Readonly<Record<ReviewFinding['severity'], string>> = {
  block: ':no_entry:',
  fix: ':wrench:',
  nit: ':bulb:',
};

/** The issue a finding of an audit becomes: the problem, the evidence and the proposed change. */
export function issueBody(review: ReviewFindings, finding: ReviewFinding): string {
  const context = finding.context ? `\n\n${finding.context}` : '';
  const rule = finding.rule ? `\n\n**Rule:** ${finding.rule}` : '';
  const at = review.commit ? ` at ${review.commit}` : '';
  return (
    `## Problem\n\n${MARKS[finding.severity]} ${finding.title}\n\n${finding.why}\n\n` +
    `## Evidence\n\n\`${finding.path}:${finding.line}\`${context}\n\n` +
    `## Proposed change\n\n${finding.fix}${rule}\n\n` +
    `---\nDrafted by Scout from an audit of ${review.repository}${at}.`
  );
}

/** The question that asks the worker to open one issue per selected finding of an audit, the issues after the marker. */
export function issuesAsk(review: ReviewFindings, selected: readonly SelectedFinding[]): string {
  const n = selected.length;
  const payload = {
    repository: review.repository,
    issues: selected.map((s) => ({ title: s.finding.title, body: s.body })),
  };
  return `Open ${n} issue${n === 1 ? '' : 's'} on ${review.repository}${ISSUES_MARKER}${JSON.stringify(payload)}`;
}
