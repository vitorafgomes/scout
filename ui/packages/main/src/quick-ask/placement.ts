import type { LocalBubbleSettings } from '@scout/shared';

interface Rect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

/** The top-left point that puts a window of `size` in `corner` of `workArea`, `margin` pixels from both edges. */
export function placeInCorner(
  size: { readonly width: number; readonly height: number },
  workArea: Rect,
  corner: LocalBubbleSettings['corner'],
  margin = 16,
): { x: number; y: number } {
  const left = workArea.x + margin;
  const right = workArea.x + workArea.width - size.width - margin;
  const top = workArea.y + margin;
  const bottom = workArea.y + workArea.height - size.height - margin;
  return {
    x: corner.endsWith('left') ? left : right,
    y: corner.startsWith('top') ? top : bottom,
  };
}
