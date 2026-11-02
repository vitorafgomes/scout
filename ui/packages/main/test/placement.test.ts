import { describe, expect, it } from 'bun:test';
import { placeInCorner } from '../src/quick-ask/placement.js';

describe('placeInCorner', () => {
  const workArea = { x: 0, y: 32, width: 1280, height: 768 };
  const size = { width: 64, height: 64 };

  it.each([
    ['bottom-right', { x: 1200, y: 720 }],
    ['bottom-left', { x: 16, y: 720 }],
    ['top-right', { x: 1200, y: 48 }],
    ['top-left', { x: 16, y: 48 }],
  ] as const)('puts a 64px window %s of the work area, 16px from its edges', (corner, expected) => {
    expect(placeInCorner(size, workArea, corner)).toEqual(expected);
  });

  it('offsets by the work area origin of a display that is not the first one', () => {
    expect(
      placeInCorner(
        { width: 640, height: 420 },
        { x: 1280, y: 0, width: 1920, height: 1040 },
        'bottom-right',
      ),
    ).toEqual({ x: 2544, y: 604 });
  });
});
