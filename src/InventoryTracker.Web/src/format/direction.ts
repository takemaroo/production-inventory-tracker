/**
 * Bidirectional control characters in text from the API (AC-37.3, design 12.8 and 17.5; architecture 18 "Safe text").
 *
 * What they are: Unicode has invisible characters that tell the browser to lay text out right to left or left to
 * right, for Arabic and Hebrew mixed with Latin text. They take no space and show nothing. Why they are dangerous:
 * anyone who can type a name can use them to make the name look like something else. "Milk", then U+202E (RIGHT-TO-LEFT
 * OVERRIDE), then "gnp.exe" is drawn as "Milkexe.png", because everything after the override is shown backwards. A
 * stored name can therefore disguise itself as another item or customer (SEC-27).
 *
 * The rule: these characters are shown, not obeyed. Wherever API text is displayed, each one is replaced by a visible
 * marker that names it, "[U+202E]", so "Milk[U+202E]gnp.exe" is seen for what it is. The stored text is never changed:
 * an edit field holds it exactly, with a line under it saying which characters it contains.
 *
 * Every character here is built from its number with String.fromCodePoint, never typed: an invisible character typed
 * into the source is exactly what this file defends against, and it would be just as invisible to a reviewer.
 */

/**
 * AC-37.3: the code points that are shown as markers. These are Unicode's "Bidi_Control" characters: U+061C (Arabic
 * letter mark), U+200E and U+200F (left-to-right and right-to-left marks), U+202A to U+202E (embeddings, overrides and
 * their end), and U+2066 to U+2069 (isolates and their end). Twelve in all.
 *
 * Not in the list (PRD row 73): the zero-width joiner and non-joiner (U+200D, U+200C), which emoji and several scripts
 * need between letters; marking them would break correct names.
 */
export const directionCodePoints: readonly number[] = [
  0x061c,
  0x200e,
  0x200f,
  0x202a,
  0x202b,
  0x202c,
  0x202d,
  0x202e,
  0x2066,
  0x2067,
  0x2068,
  0x2069,
];

// A Set answers "is this one of them?" in one step, however long the list. It holds the characters as strings, the
// form a loop over a string gives.
const directionCharacters: ReadonlySet<string> = new Set(directionCodePoints.map((codePoint) => String.fromCodePoint(codePoint)));

/** "U+202E": the code point in the standard Unicode notation, upper-case hex with at least four digits. */
export function codePointName(codePoint: number): string {
  return `U+${codePoint.toString(16).toUpperCase().padStart(4, '0')}`;
}

/** Design 12.8: the visible marker that replaces a direction character, "[U+202E]". */
export function directionMarker(codePoint: number): string {
  return `[${codePointName(codePoint)}]`;
}

/** A piece of displayed text: either ordinary characters, or one direction character to show as a marker. */
export type DirectionSegment = { readonly kind: 'text'; readonly text: string } | { readonly kind: 'marker'; readonly codePoint: number };

/**
 * Splits text into ordinary runs and direction characters, in order, so `SafeText` can draw each direction character
 * as a marker element (design 17.5). Joining the runs and the markers' characters gives the original text back.
 */
export function splitDirectionCharacters(text: string): DirectionSegment[] {
  const segments: DirectionSegment[] = [];
  let run = '';
  // `for...of` over a string steps through whole characters (an emoji made of two UTF-16 units counts as one), so a
  // character is never cut in half.
  for (const character of text) {
    if (directionCharacters.has(character)) {
      if (run !== '') {
        segments.push({ kind: 'text', text: run });
        run = '';
      }
      // A direction character is one UTF-16 unit, so codePointAt(0) is its whole value.
      segments.push({ kind: 'marker', codePoint: character.codePointAt(0) ?? 0 });
    } else {
      run += character;
    }
  }
  if (run !== '') {
    segments.push({ kind: 'text', text: run });
  }
  return segments;
}

/**
 * AC-37.3 for plain text: the text with each direction character replaced by its marker as ordinary characters,
 * "Milk[U+202E]gnp.exe". For places that cannot hold an element, such as the document title or a formatted quantity's
 * unit. On the page, `SafeText` draws the marker as its own element instead.
 */
export function showDirectionCharacters(text: string): string {
  return splitDirectionCharacters(text)
    .map((segment) => (segment.kind === 'text' ? segment.text : directionMarker(segment.codePoint)))
    .join('');
}

/**
 * AC-37.3, design 12.8 and 18.1: the line shown under an edit field whose stored text holds direction characters, for
 * example "This text contains hidden direction characters: U+202E.". Each code point is named once, in the order it
 * first appears in the text, joined by ", ". Returns null when the text holds none, so the field shows no line.
 */
export function hiddenDirectionCharacters(text: string): string | null {
  const found: number[] = [];
  for (const segment of splitDirectionCharacters(text)) {
    if (segment.kind === 'marker' && !found.includes(segment.codePoint)) {
      found.push(segment.codePoint);
    }
  }
  if (found.length === 0) {
    return null;
  }
  return `This text contains hidden direction characters: ${found.map(codePointName).join(', ')}.`;
}
