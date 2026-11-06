import { describe, expect, it } from 'vitest';

import { replyLinkTarget } from './reply-links';

function clickOn(html: string, selector: string): { event: MouseEvent; defaultPrevented: boolean } {
  const host = document.createElement('div');
  host.innerHTML = html;
  const target = host.querySelector(selector) as HTMLElement;
  const event = new MouseEvent('click', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'target', { value: target });
  return { event, defaultPrevented: () => event.defaultPrevented } as unknown as {
    event: MouseEvent;
    defaultPrevented: boolean;
  };
}

describe('replyLinkTarget', () => {
  it('returns a web link under the click and consumes the click, even from a child of the anchor', () => {
    const { event } = clickOn(
      '<p>see <a href="https://github.com/x/y/pull/5"><code>#5</code></a></p>',
      'code',
    );

    expect(replyLinkTarget(event)).toBe('https://github.com/x/y/pull/5');
    expect(event.defaultPrevented).toBe(true);
  });

  it('leaves a click on plain text alone', () => {
    const { event } = clickOn('<p>no link <strong>here</strong></p>', 'strong');

    expect(replyLinkTarget(event)).toBeNull();
    expect(event.defaultPrevented).toBe(false);
  });

  it('consumes a click on a link that is not a web address and opens nothing', () => {
    const { event } = clickOn('<p><a href="javascript:alert(1)">x</a></p>', 'a');

    expect(replyLinkTarget(event)).toBeNull();
    expect(event.defaultPrevented).toBe(true);
  });
});
