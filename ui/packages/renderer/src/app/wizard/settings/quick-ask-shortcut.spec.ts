import { describe, expect, it } from 'vitest';

import { captureShortcut, keycapsOf } from './quick-ask-shortcut';

type Press = Partial<Pick<KeyboardEvent, 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey'>> & {
  code: string;
  key: string;
};

function press(p: Press): KeyboardEvent {
  return new KeyboardEvent('keydown', {
    ctrlKey: false,
    altKey: false,
    shiftKey: false,
    metaKey: false,
    ...p,
  });
}

describe('a recorded key press becomes the accelerator main stores', () => {
  for (const [what, event, mac, accelerator] of [
    [
      'a letter',
      { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true },
      false,
      'CommandOrControl+Alt+K',
    ],
    [
      'a digit',
      { code: 'Digit7', key: '7', ctrlKey: true, shiftKey: true },
      false,
      'CommandOrControl+Shift+7',
    ],
    [
      'Space, the default',
      { code: 'Space', key: ' ', ctrlKey: true, altKey: true },
      false,
      'CommandOrControl+Alt+Space',
    ],
    ['a function key', { code: 'F12', key: 'F12', altKey: true }, false, 'Alt+F12'],
    ['F24', { code: 'F24', key: 'F24', ctrlKey: true }, false, 'CommandOrControl+F24'],
    [
      'an arrow',
      { code: 'ArrowUp', key: 'ArrowUp', ctrlKey: true, shiftKey: true },
      false,
      'CommandOrControl+Shift+Up',
    ],
    ['the comma', { code: 'Comma', key: ',', ctrlKey: true }, false, 'CommandOrControl+,'],
    [
      'the period, typed as ">" with Shift',
      { code: 'Period', key: '>', ctrlKey: true, shiftKey: true },
      false,
      'CommandOrControl+Shift+.',
    ],
    [
      'the equals key, typed as "+" with Shift',
      { code: 'Equal', key: '+', altKey: true, shiftKey: true },
      false,
      'Alt+Shift+=',
    ],
    [
      'the keypad plus',
      { code: 'NumpadAdd', key: '+', ctrlKey: true },
      false,
      'CommandOrControl+numadd',
    ],
    [
      'a keypad digit',
      { code: 'Numpad3', key: '3', ctrlKey: true },
      false,
      'CommandOrControl+num3',
    ],
    ['the Super key with a letter', { code: 'KeyS', key: 's', metaKey: true }, false, 'Super+S'],
    [
      'every modifier at once',
      { code: 'KeyA', key: 'a', ctrlKey: true, metaKey: true, altKey: true, shiftKey: true },
      false,
      'CommandOrControl+Super+Alt+Shift+A',
    ],
    [
      'the AZERTY key that types "a", at the QWERTY "Q" position, by its character',
      { code: 'KeyQ', key: 'a', ctrlKey: true, altKey: true },
      false,
      'CommandOrControl+Alt+A',
    ],
    [
      'the AZERTY key that types "a" on a Mac, by its position',
      { code: 'KeyQ', key: 'a', metaKey: true },
      true,
      'CommandOrControl+Q',
    ],
    [
      'a letter typed with Shift is the same letter',
      { code: 'KeyK', key: 'K', ctrlKey: true, shiftKey: true },
      false,
      'CommandOrControl+Shift+K',
    ],
    [
      'a letter AltGr turns into another character is named by its position',
      { code: 'KeyE', key: '€', ctrlKey: true, altKey: true },
      false,
      'CommandOrControl+Alt+E',
    ],
    [
      'a letter Option turns into "å" on a Mac',
      { code: 'KeyA', key: 'å', metaKey: true, altKey: true },
      true,
      'CommandOrControl+Alt+A',
    ],
    ["a Mac's Cmd", { code: 'KeyK', key: 'k', metaKey: true }, true, 'CommandOrControl+K'],
    [
      "a Mac's Ctrl",
      { code: 'KeyK', key: 'k', ctrlKey: true, altKey: true },
      true,
      'Control+Alt+K',
    ],
  ] as const) {
    it(`${what} ${mac ? 'on a Mac ' : ''}is saved as ${accelerator}`, () => {
      expect(captureShortcut(press(event), mac)).toEqual({ accelerator });
    });
  }

  for (const code of ['ShiftLeft', 'ControlRight', 'AltLeft', 'MetaLeft'] as const) {
    it(`${code} alone keeps the recording waiting for the key`, () => {
      expect(
        captureShortcut(press({ code, key: code, ctrlKey: true, altKey: true }), false),
      ).toBeNull();
    });
  }

  for (const [what, event, refusal] of [
    [
      'a letter with no modifier',
      { code: 'KeyK', key: 'k' },
      '"K" has no modifier, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.',
    ],
    [
      'a function key with no modifier',
      { code: 'F9', key: 'F9' },
      '"F9" has no modifier, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.',
    ],
    [
      'a letter with Shift only',
      { code: 'KeyK', key: 'K', shiftKey: true },
      '"Shift+K" has no modifier but Shift, so it would fire whenever you type it; add Ctrl, Alt, Super or Cmd.',
    ],
    [
      'a key Electron has no name for',
      { code: 'IntlBackslash', key: '<', ctrlKey: true },
      'Scout cannot use that key in a shortcut; choose another.',
    ],
  ] as const) {
    it(`${what} is refused with the reason main gives`, () => {
      expect(captureShortcut(press(event), false)).toEqual({ refusal });
    });
  }
});

describe('a stored accelerator is drawn as keycaps', () => {
  for (const [accelerator, mac, keys] of [
    ['CommandOrControl+Alt+Space', false, ['Ctrl', 'Alt', 'Space']],
    ['CommandOrControl+Alt+Space', true, ['Cmd', 'Option', 'Space']],
    ['CmdOrCtrl+Shift+K', false, ['Ctrl', 'Shift', 'K']],
    ['Control+F12', false, ['Ctrl', 'F12']],
    ['Super+K', false, ['Super', 'K']],
    ['CommandOrControl+Plus', false, ['Ctrl', '+']],
    ['CommandOrControl+,', false, ['Ctrl', ',']],
    ['Alt+numadd', false, ['Alt', 'Num +']],
    ['Alt+num3', false, ['Alt', 'Num 3']],
    ['Alt+Shift+Up', false, ['Alt', 'Shift', 'Up']],
  ] as const) {
    it(`${accelerator}${mac ? ' on a Mac' : ''} reads ${keys.join(', ')}`, () => {
      expect(keycapsOf(accelerator, mac)).toEqual(keys);
    });
  }
});
