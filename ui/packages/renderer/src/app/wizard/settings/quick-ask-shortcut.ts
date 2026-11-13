/** What one key press gives while a shortcut is recorded: nothing yet, a refusal in words, or the accelerator to save. */
export type Captured = null | { readonly refusal: string } | { readonly accelerator: string };

const MODIFIER_CODES: ReadonlySet<string> = new Set([
  'ShiftLeft',
  'ShiftRight',
  'ControlLeft',
  'ControlRight',
  'AltLeft',
  'AltRight',
  'MetaLeft',
  'MetaRight',
  'OSLeft',
  'OSRight',
]);

const NAMED_CODES: Readonly<Record<string, string>> = {
  Space: 'Space',
  Enter: 'Enter',
  NumpadEnter: 'Enter',
  Backspace: 'Backspace',
  Delete: 'Delete',
  Insert: 'Insert',
  Home: 'Home',
  End: 'End',
  PageUp: 'PageUp',
  PageDown: 'PageDown',
  ArrowUp: 'Up',
  ArrowDown: 'Down',
  ArrowLeft: 'Left',
  ArrowRight: 'Right',
  PrintScreen: 'PrintScreen',
  Minus: '-',
  Equal: '=',
  BracketLeft: '[',
  BracketRight: ']',
  Backslash: '\\',
  Semicolon: ';',
  Quote: "'",
  Comma: ',',
  Period: '.',
  Slash: '/',
  Backquote: '`',
  NumpadAdd: 'numadd',
  NumpadSubtract: 'numsub',
  NumpadMultiply: 'nummult',
  NumpadDivide: 'numdiv',
  NumpadDecimal: 'numdec',
};

const KEYCAPS: Readonly<Record<string, string>> = {
  Plus: '+',
  numadd: 'Num +',
  numsub: 'Num -',
  nummult: 'Num *',
  numdiv: 'Num /',
  numdec: 'Num .',
};

function keyName(code: string, key: string, mac: boolean): string | null {
  const letter = /^Key([A-Z])$/.exec(code);
  if (letter !== null) return !mac && /^[a-z]$/i.test(key) ? key.toUpperCase() : letter[1]!;
  const digit = /^Digit([0-9])$/.exec(code);
  if (digit !== null) return digit[1]!;
  const fn = /^F([0-9]{1,2})$/.exec(code);
  if (fn !== null && Number(fn[1]) >= 1 && Number(fn[1]) <= 24) return code;
  const pad = /^Numpad([0-9])$/.exec(code);
  if (pad !== null) return `num${pad[1]}`;
  return NAMED_CODES[code] ?? null;
}

/**
 * The accelerator one key press makes, in the names main stores: `CommandOrControl` for Ctrl, or for Cmd on a Mac,
 * then `Control` (a Mac's Ctrl), `Super`, `Alt` and `Shift`, then the key. A letter is named by the character the
 * layout gives it on Linux and Windows and by its position on a Mac, as each system registers a global shortcut;
 * every other key by its position, so Shift, AltGr and Option never change it. A key with no modifier, or with
 * Shift alone, is refused with main's own reason, since main refuses it on Save.
 */
export function captureShortcut(event: KeyboardEvent, mac: boolean): Captured {
  if (MODIFIER_CODES.has(event.code)) return null;
  const key = keyName(event.code, event.key, mac);
  if (key === null) return { refusal: 'Scout cannot use that key in a shortcut; choose another.' };
  const modifiers = [
    (mac ? event.metaKey : event.ctrlKey) ? 'CommandOrControl' : null,
    mac && event.ctrlKey ? 'Control' : null,
    !mac && event.metaKey ? 'Super' : null,
    event.altKey ? 'Alt' : null,
    event.shiftKey ? 'Shift' : null,
  ].filter((name): name is string => name !== null);
  const accelerator = [...modifiers, key].join('+');
  if (modifiers.length === 0) {
    return {
      refusal: `"${accelerator}" has no modifier, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.`,
    };
  }
  if (modifiers.every((name) => name === 'Shift')) {
    return {
      refusal: `"${accelerator}" has no modifier but Shift, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.`,
    };
  }
  return { accelerator };
}

/** The keycaps of a stored accelerator, in the names the platform prints on its keys. */
export function keycapsOf(accelerator: string, mac: boolean): string[] {
  const names: Readonly<Record<string, string>> = {
    CommandOrControl: mac ? 'Cmd' : 'Ctrl',
    CmdOrCtrl: mac ? 'Cmd' : 'Ctrl',
    Control: 'Ctrl',
    Ctrl: 'Ctrl',
    Command: 'Cmd',
    Cmd: 'Cmd',
    Super: mac ? 'Cmd' : 'Super',
    Meta: mac ? 'Cmd' : 'Super',
    Alt: mac ? 'Option' : 'Alt',
    Option: 'Option',
    ...KEYCAPS,
  };
  return accelerator
    .split('+')
    .filter((part) => part !== '')
    .map((part) => names[part] ?? part.replace(/^num([0-9])$/, 'Num $1'));
}
