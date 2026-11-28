import { describe, expect, it } from 'vitest';
import {
  codePointName,
  directionCodePoints,
  directionMarker,
  hiddenDirectionCharacters,
  showDirectionCharacters,
  splitDirectionCharacters,
} from './direction';

// Every invisible character in these tests is built from its number with String.fromCodePoint, never typed, so the
// test file itself holds no character a reviewer cannot see (see src/guards.test.ts).
const character = (codePoint: number) => String.fromCodePoint(codePoint);

/** The code points from `first` to `last`, both included. */
function range(first: number, last: number): number[] {
  return Array.from({ length: last - first + 1 }, (_, index) => first + index);
}

describe('directionCodePoints (AC-37.3)', () => {
  // AC-37.3 lists U+061C, U+200E, U+200F, U+202A to U+202E and U+2066 to U+2069: exactly these twelve, in order.
  it('holds exactly the code points AC-37.3 lists', () => {
    expect(directionCodePoints).toEqual([0x061c, ...range(0x200e, 0x200f), ...range(0x202a, 0x202e), ...range(0x2066, 0x2069)]);
    expect(directionCodePoints).toHaveLength(12);
  });

  // Design 12.8: the marker names the code point, "[U+202E]", with four upper-case hex digits.
  it('names a code point in Unicode notation', () => {
    expect(codePointName(0x202e)).toBe('U+202E');
    expect(codePointName(0x061c)).toBe('U+061C');
    expect(directionMarker(0x2066)).toBe('[U+2066]');
  });
});

describe('showDirectionCharacters (AC-37.3, plain text)', () => {
  // AC-37.3: "Milk" + U+202E + "gnp.exe" is shown as "Milk[U+202E]gnp.exe", for each of the listed code points.
  it.each(directionCodePoints.map((codePoint) => [codePointName(codePoint), codePoint] as const))(
    'shows %s as its marker',
    (name, codePoint) => {
      expect(showDirectionCharacters(`Milk${character(codePoint)}gnp.exe`)).toBe(`Milk[${name}]gnp.exe`);
    },
  );

  // PRD row 73: the zero-width joiner and non-joiner are needed by emoji and some scripts, so they stay as they are.
  it('leaves the zero-width joiner and non-joiner and other text untouched', () => {
    const text = `a${character(0x200d)}b${character(0x200c)}c ${character(0x1f9c0)} Cr${character(0x00e8)}me`;

    expect(showDirectionCharacters(text)).toBe(text);
  });
});

describe('splitDirectionCharacters (AC-37.3)', () => {
  // AC-37.3: the stored text is not changed; the pieces hold every character, in order, so nothing is lost.
  it('splits text into runs and markers, in order', () => {
    const text = `${character(0x2066)}Milk${character(0x202e)}${character(0x202e)}gnp${character(0x1f9c0)}`;

    expect(splitDirectionCharacters(text)).toEqual([
      { kind: 'marker', codePoint: 0x2066 },
      { kind: 'text', text: 'Milk' },
      { kind: 'marker', codePoint: 0x202e },
      { kind: 'marker', codePoint: 0x202e },
      { kind: 'text', text: `gnp${character(0x1f9c0)}` },
    ]);
    expect(splitDirectionCharacters('')).toEqual([]);
  });
});

describe('hiddenDirectionCharacters (AC-37.3, design 12.8 and 18.1)', () => {
  // AC-37.3, design 18.1: the edit-field line, word for word.
  it('gives the line for one direction character', () => {
    expect(hiddenDirectionCharacters(`Milk${character(0x202e)}gnp.exe`)).toBe(
      'This text contains hidden direction characters: U+202E.',
    );
  });

  // Design 12.8: each code point found is listed once, in the order it first appears, joined by ", ".
  it('lists each code point once, in order', () => {
    const text = `${character(0x2066)}a${character(0x202e)}b${character(0x2066)}c${character(0x200f)}${character(0x202e)}`;

    expect(hiddenDirectionCharacters(text)).toBe('This text contains hidden direction characters: U+2066, U+202E, U+200F.');
  });

  // Design 12.8: the line shows only when the stored text holds such a character.
  it('gives no line for text without direction characters', () => {
    expect(hiddenDirectionCharacters('Deli Norte')).toBeNull();
    expect(hiddenDirectionCharacters(`a${character(0x200d)}b`)).toBeNull();
    expect(hiddenDirectionCharacters('')).toBeNull();
  });
});
