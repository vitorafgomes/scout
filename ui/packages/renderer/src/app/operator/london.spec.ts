import { describe, expect, it } from 'vitest';

import { formatLondon, londonToUtcIso } from './london';

describe('Europe/London times', () => {
  it('a date-time typed in London in winter is the same UTC time, and in summer one hour earlier', () => {
    expect(londonToUtcIso('2026-12-01T10:00')).toBe('2026-12-01T10:00:00.000Z');
    expect(londonToUtcIso('2026-07-01T10:00')).toBe('2026-07-01T09:00:00.000Z');
  });

  it('the hours either side of the clock change land on the right UTC time', () => {
    expect(londonToUtcIso('2026-03-29T00:30')).toBe('2026-03-29T00:30:00.000Z');
    expect(londonToUtcIso('2026-03-29T02:30')).toBe('2026-03-29T01:30:00.000Z');
    expect(londonToUtcIso('2026-10-25T03:00')).toBe('2026-10-25T03:00:00.000Z');
  });

  it('a value that is not a date-time is no time at all', () => {
    expect(londonToUtcIso('')).toBeNull();
    expect(londonToUtcIso('next tuesday')).toBeNull();
  });

  it('a UTC time is shown as the London wall-clock time', () => {
    expect(formatLondon('2026-07-01T09:00:00.000Z')).toBe('2026-07-01 10:00');
    expect(formatLondon('2026-12-01T10:00:00Z')).toBe('2026-12-01 10:00');
  });
});
