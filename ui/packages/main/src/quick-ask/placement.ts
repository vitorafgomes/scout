import type { LocalBubbleSettings } from '@scout/shared';

interface Rect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

function clamp(value: number, low: number, high: number): number {
  return Math.max(low, Math.min(value, high));
}

/**
 * The top-left point that puts a window of `size` in `corner` of `workArea`, `margin` pixels from both edges; where
 * the margin does not fit, the point moves in so the window stays inside the work area.
 */
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
    x: clamp(
      corner.endsWith('left') ? left : right,
      workArea.x,
      workArea.x + workArea.width - size.width,
    ),
    y: clamp(
      corner.startsWith('top') ? top : bottom,
      workArea.y,
      workArea.y + workArea.height - size.height,
    ),
  };
}

/** `bounds` shrunk to no more than `workArea` and moved the least distance that puts them inside it. */
export function fitInWorkArea(bounds: Rect, workArea: Rect): Rect {
  const width = Math.min(bounds.width, workArea.width);
  const height = Math.min(bounds.height, workArea.height);
  return {
    x: clamp(bounds.x, workArea.x, workArea.x + workArea.width - width),
    y: clamp(bounds.y, workArea.y, workArea.y + workArea.height - height),
    width,
    height,
  };
}
