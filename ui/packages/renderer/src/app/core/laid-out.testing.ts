import type { InteractivityChecker } from '@angular/cdk/a11y';

function focusable(el: HTMLElement): boolean {
  if (el.hasAttribute('disabled')) return false;
  const tag = el.tagName.toLowerCase();
  if (['button', 'input', 'select', 'textarea'].includes(tag)) return true;
  if (tag === 'a' && el.hasAttribute('href')) return true;
  const tabindex = el.getAttribute('tabindex');
  return tabindex !== null && Number(tabindex) >= 0;
}

/** jsdom lays nothing out, so the CDK focus trap would find no visible button; this checker answers as a browser would for an enabled one. */
export const LAID_OUT: Pick<
  InteractivityChecker,
  'isDisabled' | 'isVisible' | 'isTabbable' | 'isFocusable'
> = {
  isDisabled: (el: HTMLElement) => el.hasAttribute('disabled'),
  isVisible: () => true,
  isTabbable: (el: HTMLElement) => focusable(el),
  isFocusable: (el: HTMLElement) => focusable(el),
};
