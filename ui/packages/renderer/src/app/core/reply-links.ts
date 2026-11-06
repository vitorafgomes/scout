/**
 * A click inside a rendered reply: when it lands on a web link, the click is consumed and the link
 * returned for the window to open in the browser; anything else, including a relative or a
 * non-web href, is left alone and returns null.
 */
export function replyLinkTarget(event: MouseEvent): string | null {
  const anchor = (event.target as HTMLElement | null)?.closest('a');
  if (anchor === null || anchor === undefined) return null;
  event.preventDefault();
  const href = anchor.getAttribute('href') ?? '';
  return /^https?:\/\//.test(href) ? href : null;
}
