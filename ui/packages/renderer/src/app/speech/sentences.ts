const FENCE = /```[\s\S]*?(?:```|$)/g;
const MARKDOWN_LINK = /!?\[([^\]]*)\]\([^)]*\)/g;
const URL = /\b(?:https?:\/\/|www\.)\S+/gi;
const LINE_MARKER = /^\s*(?:#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)](?:\s+|$))/;
const EMPHASIS = /(\*\*|__|\*|`)/g;
const SENTENCE_END = /(?<=[.!?…])\s+/;
const SPOKEN = /[\p{L}\p{N}]/u;

/** The longest piece a voice is given at once; a longer one is cut so each piece stays small and is never refused. */
export const MAX_PIECE_CHARS = 300;

/** `sentence` in pieces of at most MAX_PIECE_CHARS, each cut after the last `,`, `;` or `:` before the cap, else at the last space, else at the cap. */
function pieces(sentence: string): string[] {
  const out: string[] = [];
  let rest = sentence;
  while (rest.length > MAX_PIECE_CHARS) {
    const head = rest.slice(0, MAX_PIECE_CHARS);
    const mark = Math.max(head.lastIndexOf(','), head.lastIndexOf(';'), head.lastIndexOf(':'));
    const space = head.lastIndexOf(' ');
    const at = mark > 0 ? mark + 1 : space > 0 ? space : MAX_PIECE_CHARS;
    out.push(rest.slice(0, at).trim());
    rest = rest.slice(at).trim();
  }
  out.push(rest);
  return out;
}

/**
 * The sentences of a Markdown reply in the words a voice should read: fenced code blocks and table rows are left out,
 * a link is read by its label and a bare address not at all, list and heading marks and emphasis are dropped, and a
 * line is cut after each `.`, `!`, `?` or `…` followed by a space, and a sentence over MAX_PIECE_CHARS into smaller pieces.
 * A piece with no letter or digit is left out.
 */
export function speakableSentences(markdown: string): string[] {
  return markdown
    .replace(FENCE, '\n')
    .split('\n')
    .filter((line) => !line.trimStart().startsWith('|'))
    .map((line) =>
      line
        .replace(MARKDOWN_LINK, '$1')
        .replace(URL, '')
        .replace(LINE_MARKER, '')
        .replace(EMPHASIS, ''),
    )
    .flatMap((line) => line.split(SENTENCE_END))
    .map((sentence) => sentence.replace(/\s+/g, ' ').trim())
    .flatMap(pieces)
    .filter((sentence) => SPOKEN.test(sentence));
}

/** The end of the last whole sentence or line in `text`: past a line break, or past `.`, `!`, `?` or `…` and a space. */
function settled(text: string): number {
  for (let i = text.length - 1; i >= 0; i--) {
    const c = text[i]!;
    if (c === '\n') return i + 1;
    if (/\s/.test(c) && i > 0 && /[.!?…]/.test(text[i - 1]!)) return i;
  }
  return 0;
}

/**
 * Splits a reply that streams in, each update the whole reply so far, into the sentences to read, each given once:
 * `push` answers the sentences that became whole since the last call, and `end` the rest once the reply is complete.
 */
export class SentenceStream {
  private given = 0;

  push(reply: string): string[] {
    return this.take(reply.slice(0, settled(reply)));
  }

  end(reply: string): string[] {
    return this.take(reply);
  }

  private take(text: string): string[] {
    const sentences = speakableSentences(text);
    const fresh = sentences.slice(this.given);
    this.given = Math.max(this.given, sentences.length);
    return fresh;
  }
}
