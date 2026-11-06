import { describe, expect, it } from 'vitest';

import { renderMarkdown } from './markdown';

describe('renderMarkdown', () => {
  it('renders headings, emphasis, lists, inline code and fenced code', () => {
    const html = renderMarkdown(
      '## Review of #5\n\n**Verdict:** needs work.\n\n- `MenuOrdering.Move` has no lock\n- a second item\n\n```csharp\nvar x = 1;\n```',
    );

    expect(html).toContain('<h2>Review of #5</h2>');
    expect(html).toContain('<strong>Verdict:</strong>');
    expect(html).toContain('<li><code>MenuOrdering.Move</code> has no lock</li>');
    expect(html).toContain('<pre><code class="language-csharp">var x = 1;');
  });

  it('keeps a single newline as a line break, as the plain-text chat did', () => {
    expect(renderMarkdown('It is green.\nAll 42 jobs passed.')).toBe(
      '<p>It is green.<br>\nAll 42 jobs passed.</p>',
    );
  });

  it('shows raw HTML as text, so a generic stays visible and nothing becomes markup', () => {
    const html = renderMarkdown('The script for Span<T> is ready. <img src=x onerror="alert(1)">');

    expect(html).toContain('Span&lt;T&gt;');
    expect(html).toContain('&lt;img src=x onerror=&quot;alert(1)&quot;&gt;');
    expect(html).not.toContain('<img');
  });

  it('renders a link with its href for the chat to open externally', () => {
    expect(renderMarkdown('see [the run](https://github.com/acme/ci/actions/runs/42)')).toContain(
      '<a href="https://github.com/acme/ci/actions/runs/42">the run</a>',
    );
  });

  it('renders nothing for an empty reply', () => {
    expect(renderMarkdown('')).toBe('');
  });
});
