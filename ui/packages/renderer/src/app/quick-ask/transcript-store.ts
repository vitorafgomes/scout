import { Injectable, computed, signal } from '@angular/core';

import type { LocalListenLane } from '@scout/shared';

export const MAX_LINES = 500;
export const MAX_WINDOW_CHARS = 24_000;

/** The translation of one final line, or the reason it has none. */
export interface LineTranslation {
  readonly text?: string;
  readonly language: string;
  readonly error?: string;
}

/** One final line heard in a live listening lane, with the time it arrived, the session it came from and its number in that session. */
export interface TranscriptLine {
  readonly lane: LocalListenLane;
  readonly label: 'You' | 'Others';
  readonly text: string;
  readonly at: number;
  readonly seq?: number;
  readonly session?: string;
  readonly translation?: LineTranslation;
}

/**
 * The final lines of both listening lanes, in memory only and never persisted: at most 500, the oldest
 * dropped first. A line's translation sits beside it and never replaces it; a note about a whole session
 * shows until that lane's next translation, its next listen, or Clear.
 */
@Injectable({ providedIn: 'root' })
export class TranscriptStore {
  private readonly _lines = signal<readonly TranscriptLine[]>([]);
  private readonly _notes = signal<Readonly<Record<LocalListenLane, string | null>>>({
    me: null,
    others: null,
  });
  readonly lines = this._lines.asReadonly();
  /** The session notes of both lanes, each text once. */
  readonly notes = computed(() => [
    ...new Set(Object.values(this._notes()).filter((note) => note !== null)),
  ]);

  add(lane: LocalListenLane, text: string, at = Date.now(), seq?: number, session?: string): void {
    const line: TranscriptLine = {
      lane,
      label: lane === 'me' ? 'You' : 'Others',
      text,
      at,
      ...(seq !== undefined ? { seq } : {}),
      ...(session !== undefined ? { session } : {}),
    };
    this._lines.update((lines) => [...lines, line].slice(-MAX_LINES));
  }

  /** Puts the translation beside the newest line of the lane with that session and seq, and ends the lane's note. */
  translate(
    lane: LocalListenLane,
    session: string,
    seq: number,
    translation: LineTranslation,
  ): void {
    this._notes.update((notes) => ({ ...notes, [lane]: null }));
    this._lines.update((lines) => {
      let at = lines.length - 1;
      while (
        at >= 0 &&
        (lines[at]!.lane !== lane || lines[at]!.session !== session || lines[at]!.seq !== seq)
      )
        at--;
      if (at === -1) return lines;
      const next = [...lines];
      next[at] = { ...lines[at]!, translation };
      return next;
    });
  }

  /** Shows a note about a lane's whole session, such as a pause in translation. */
  note(lane: LocalListenLane, text: string): void {
    this._notes.update((notes) => ({ ...notes, [lane]: text }));
  }

  /** Ends the lane's note as a new listen starts on it, so a note about an earlier session never stays. */
  startLane(lane: LocalListenLane): void {
    this._notes.update((notes) => ({ ...notes, [lane]: null }));
  }

  clear(): void {
    this._lines.set([]);
    this._notes.set({ me: null, others: null });
  }

  /**
   * The lines of the last `minutes` as `Label: text`, one per line, cut to the newest 24,000 characters; the
   * source lines only, never their translations.
   */
  window(minutes: number, now = Date.now()): string {
    const since = now - minutes * 60_000;
    return this._lines()
      .filter((line) => line.at >= since)
      .map((line) => `${line.label}: ${line.text}`)
      .join('\n')
      .slice(-MAX_WINDOW_CHARS);
  }
}
