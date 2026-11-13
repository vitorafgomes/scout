import path from 'node:path';
import type { FakeMeeting } from './app.js';

/** The Scout data folder of the loaded states: one feature with writes granted, two actions waiting, three writes, a mandate and two projects. */
export const SCOUT_DATA = path.join(import.meta.dirname, 'scout-data');

/** The three meetings the owner kept. */
export const KEPT_MEETINGS: readonly FakeMeeting[] = [
  {
    id: 'kept-meeting-weekly-01',
    startedAt: '2026-11-03T10:00:00+00:00',
    endedAt: '2026-11-03T10:45:00+00:00',
    title: 'Weekly planning',
    lineCount: 120,
    turnCount: 3,
  },
  {
    id: 'kept-meeting-supply-02',
    startedAt: '2026-11-05T15:00:00+00:00',
    endedAt: '2026-11-05T15:20:00+00:00',
    title: 'Supplier call',
    lineCount: 48,
    turnCount: 1,
  },
  {
    id: 'kept-meeting-sync-0003',
    startedAt: '2026-11-06T09:30:00+00:00',
    endedAt: '2026-11-06T09:30:40+00:00',
    title: 'Quick sync',
    lineCount: 1,
    turnCount: 0,
  },
];
