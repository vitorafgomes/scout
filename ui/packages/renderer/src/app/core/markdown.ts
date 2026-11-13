import { Marked, type Tokens } from 'marked';

/**
 * Renders a reply's Markdown to HTML for the chat: headings, lists, emphasis, inline code, fenced
 * code blocks, links and tables. Raw HTML in the reply is shown as text, never rendered, so a
 * generic such as `Span<T>` stays visible and nothing the model writes becomes markup; Angular's
 * sanitiser then strips anything else before it reaches the DOM. A single newline keeps its line
 * break, as the plain-text chat showed it.
 */
const engine = new Marked({
  gfm: true,
  breaks: true,
  renderer: {
    html({ text }: Tokens.HTML | Tokens.Tag): string {
      return escape(text);
    },
    br(): string {
      return '<br>\n';
    },
  },
});

export function escape(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

export function renderMarkdown(text: string): string {
  if (text.length === 0) return '';
  return engine.parse(text, { async: false }).trimEnd();
}
