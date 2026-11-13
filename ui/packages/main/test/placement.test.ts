import { describe, expect, it } from 'bun:test';
import { fitInWorkArea, placeInCorner } from '../src/quick-ask/placement.js';

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

  it.each([
    ['bottom-right', { x: 0, y: 32 }],
    ['bottom-left', { x: 10, y: 32 }],
    ['top-right', { x: 0, y: 42 }],
    ['top-left', { x: 10, y: 42 }],
  ] as const)(
    'a 440 x 700 window %s of a 450 x 710 work area, too small for the 16px margin, stays inside it',
    (corner, expected) => {
      expect(
        placeInCorner(
          { width: 440, height: 700 },
          { x: 0, y: 32, width: 450, height: 710 },
          corner,
        ),
      ).toEqual(expected);
    },
  );
});

describe('fitInWorkArea', () => {
  const workArea = { x: 1280, y: 0, width: 1920, height: 1040 };

  it.each([
    [
      'inside is unchanged',
      { x: 1400, y: 100, width: 440, height: 700 },
      { x: 1400, y: 100, width: 440, height: 700 },
    ],
    [
      'past the right edge moves left',
      { x: 3000, y: 100, width: 440, height: 700 },
      { x: 2760, y: 100, width: 440, height: 700 },
    ],
    [
      'past the bottom edge moves up',
      { x: 1400, y: 900, width: 440, height: 700 },
      { x: 1400, y: 340, width: 440, height: 700 },
    ],
    [
      'left of the left edge moves right',
      { x: 1000, y: 100, width: 440, height: 700 },
      { x: 1280, y: 100, width: 440, height: 700 },
    ],
    [
      'above the top edge moves down',
      { x: 1400, y: -300, width: 440, height: 700 },
      { x: 1400, y: 0, width: 440, height: 700 },
    ],
    [
      'wider and taller than the work area shrinks to it',
      { x: 1300, y: 20, width: 2500, height: 1300 },
      { x: 1280, y: 0, width: 1920, height: 1040 },
    ],
  ] as const)('a window %s', (_case, bounds, expected) => {
    expect(fitInWorkArea(bounds, workArea)).toEqual(expected);
  });
});
