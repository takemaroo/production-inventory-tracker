import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { directionCodePoints, codePointName } from '../format/direction';
import { NameWithId, Quantity, QuantityRange, SafeText } from './SafeText';

// Testing Library draws a component into the simulated page (jsdom); cleanup removes it after each test.
afterEach(cleanup);

// Invisible characters are built from their numbers, never typed (see src/guards.test.ts).
const character = (codePoint: number) => String.fromCodePoint(codePoint);

/** Draws a SafeText and returns its `<bdi>` element. */
function renderSafeText(text: string, keepLineBreaks?: boolean): HTMLElement {
  const { container } = render(<SafeText text={text} keepLineBreaks={keepLineBreaks} />);
  const bdi = container.querySelector('bdi');
  if (bdi === null) {
    throw new Error('SafeText drew no <bdi> element.');
  }
  return bdi;
}

describe('SafeText: text binding only (AC-37.1)', () => {
  // AC-37.1, the PRD's test data: an item name, a customer name, customer notes and a completion reason.
  const attacks = [
    '<img src=x onerror=alert(1)>',
    '</script><script>alert(1)</script>',
    "<b>bold</b> & 'quote'",
    '<a href="https://example.com">click</a>',
  ];

  // AC-37.1: the characters show exactly as typed; no element is made from them, so no image loads, no script runs,
  // nothing is bold and nothing is a link.
  it.each(attacks)('shows %j as text, with no element', (text) => {
    const { container } = render(<SafeText text={text} />);
    const bdi = container.querySelector('bdi');

    expect(bdi?.textContent).toBe(text);
    expect(bdi?.children).toHaveLength(0);
    expect(container.querySelector('img, script, a, b, strong')).toBeNull();
    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.queryByRole('img')).toBeNull();
  });

  // AC-37.1 with notes: the line-break form makes no element from the text either, only its own <br>.
  it('keeps the AC-37.1 strings as text when line breaks are kept', () => {
    const bdi = renderSafeText(`${attacks[2]}\n${attacks[3]}`, true);

    expect(bdi.textContent).toBe(`${attacks[2]}${attacks[3]}`);
    expect([...bdi.children].map((child) => child.tagName)).toEqual(['BR']);
  });
});

describe('SafeText: direction characters shown, not obeyed (AC-37.3)', () => {
  // AC-37.3: "Milk" + the character + "gnp.exe" shows as "Milk[U+202E]gnp.exe", with the marker as its own element
  // (design 17.5), for each listed code point. The character itself is not in the page, so it reorders nothing.
  it.each(directionCodePoints.map((codePoint) => [codePointName(codePoint), codePoint] as const))(
    'shows %s as a marker element',
    (name, codePoint) => {
      const bdi = renderSafeText(`Milk${character(codePoint)}gnp.exe`);

      expect(bdi.textContent).toBe(`Milk[${name}]gnp.exe`);
      const markers = bdi.querySelectorAll('span.bidi');
      expect(markers).toHaveLength(1);
      expect(markers[0]?.textContent).toBe(`[${name}]`);
      expect(bdi.textContent).not.toContain(character(codePoint));
    },
  );

  // AC-37.3: several markers, side by side and next to an emoji, each drawn on its own; the emoji is kept whole.
  it('draws each of several direction characters as its own marker', () => {
    const cheese = character(0x1f9c0);
    const bdi = renderSafeText(`${cheese}${character(0x202e)}${character(0x2066)}x`);

    expect(bdi.textContent).toBe(`${cheese}[U+202E][U+2066]x`);
    expect(bdi.querySelectorAll('span.bidi')).toHaveLength(2);
  });

  // PRD row 73: the zero-width joiner and non-joiner are left as they are; they get no marker.
  it('gives the zero-width joiner and non-joiner no marker', () => {
    const text = `a${character(0x200d)}b${character(0x200c)}c`;
    const bdi = renderSafeText(text);

    expect(bdi.textContent).toBe(text);
    expect(bdi.querySelectorAll('span.bidi')).toHaveLength(0);
  });
});

describe('SafeText: isolation and line breaks (design 12.8)', () => {
  // Design 12.8: every piece of API text sits in its own direction-isolated element, with the class that lets long
  // words wrap.
  it('puts the text in its own <bdi> element', () => {
    const bdi = renderSafeText('Deli Norte');

    expect(bdi.tagName).toBe('BDI');
    expect(bdi.className).toBe('t');
    expect(bdi.textContent).toBe('Deli Norte');
  });

  // Design 12.8: notes and reasons keep their line breaks; Windows, old Mac and Unix breaks each count once.
  it('draws each line break as one when asked', () => {
    const bdi = renderSafeText('Line 1\nLine 2\r\nLine 3\rLine 4', true);

    expect(bdi.querySelectorAll('br')).toHaveLength(3);
    expect(bdi.textContent).toBe('Line 1Line 2Line 3Line 4');
  });

  // Design 12.8: elsewhere the text is printed as it is, and the browser shows a line break as a space.
  it('adds no line break element unless asked', () => {
    const bdi = renderSafeText('Line 1\nLine 2');

    expect(bdi.querySelectorAll('br')).toHaveLength(0);
    expect(bdi.textContent).toBe('Line 1\nLine 2');
  });

  // Design 12.8 and AC-37.3 together: a marker on the second line of a note.
  it('shows markers on every line', () => {
    const bdi = renderSafeText(`ok\nMilk${character(0x202e)}gnp.exe`, true);

    expect(bdi.textContent).toBe('okMilk[U+202E]gnp.exe');
    expect(bdi.querySelectorAll('span.bidi')).toHaveLength(1);
  });
});

describe('NameWithId (F-10, AC-37.4, design 12.8)', () => {
  // F-10: "Deli Norte (#4)", and " (inactive)" after a deactivated customer: "Cafe Sur (#7) (inactive)".
  it('shows the name, then the id, then inactive when so', () => {
    const active = render(<NameWithId name="Deli Norte" id={4} />);
    expect(active.container.textContent).toBe('Deli Norte (#4)');
    cleanup();

    const inactive = render(<NameWithId name="Cafe Sur" id={7} inactive />);
    expect(inactive.container.textContent).toBe('Cafe Sur (#7) (inactive)');
  });

  // Design 12.8, SEC-25: the name is isolated on its own and the id is outside it, on one line (`num`), so a
  // right-to-left name or a direction character can never reorder or hide the id.
  it('keeps the id outside the isolated name', () => {
    const name = `Milk${character(0x202e)}gnp.exe`;
    const { container } = render(<NameWithId name={name} id={11} />);

    const bdi = container.querySelector('bdi');
    const id = container.querySelector('span.num');
    expect(bdi?.textContent).toBe('Milk[U+202E]gnp.exe');
    expect(id?.textContent).toBe('(#11)');
    expect(bdi?.contains(id ?? null)).toBe(false);
  });

  // AC-37.4: "Deli Norte" (#4) and the look-alike with a Cyrillic "o" (U+043E) in place of the Latin one (#9) each
  // show their own id.
  it('shows the id of each of two look-alike customers', () => {
    const lookAlike = `Deli N${character(0x043e)}rte`;
    const { container } = render(
      <ul>
        <li>
          <NameWithId name="Deli Norte" id={4} />
        </li>
        <li>
          <NameWithId name={lookAlike} id={9} />
        </li>
      </ul>,
    );

    expect([...container.querySelectorAll('li')].map((item) => item.textContent)).toEqual(['Deli Norte (#4)', `${lookAlike} (#9)`]);
  });
});

describe('Quantity and QuantityRange (F-7, design 12.7 and 12.8)', () => {
  // F-7, design 12.7: each form prints as the string formatters do: "1,015 kg", "0.000125 kg", "+15 kg", "-25 kg".
  it.each([
    ['quantity', '1015.000', '1,015 kg'],
    ['quantity', '0.6', '0.6 kg'],
    ['amount-per-unit', '0.000125', '0.000125 kg'],
    ['signed', 15, '+15 kg'],
    ['signed', -25, '-25 kg'],
    ['signed', 0, '0 kg'],
  ] as const)('form %s prints %j as %j', (form, value, expected) => {
    const { container } = render(<Quantity value={value} unit="kg" form={form} />);

    expect(container.textContent).toBe(expected);
  });

  // F-7: the plain quantity form is the default, with its 3 decimals.
  it('uses the quantity form unless told otherwise', () => {
    const { container } = render(<Quantity value="1.2345" unit="Bag" />);

    expect(container.textContent).toBe('1.235 Bag');
  });

  // Design 12.7: "a quantity never wraps away from its unit": figure and unit share one `num` span, and the unit
  // (API text) is a SafeText of its own inside it (design 12.8).
  it('keeps the figure and its unit in one num span, with the unit isolated', () => {
    const { container } = render(<Quantity value={5} unit="kg" />);

    const num = container.querySelector('span.num');
    expect(num?.textContent).toBe('5 kg');
    expect(num?.querySelector('bdi')?.textContent).toBe('kg');
  });

  // AC-37.3, design 12.8 (T-040 review): a direction character in the unit is drawn as the marker chip inside the
  // unit's own <bdi>; the character itself is not on the page.
  it('draws a direction character in the unit as the marker element', () => {
    const { container } = render(<Quantity value={-25} unit={`k${character(0x202e)}g`} form="signed" />);

    const unit = container.querySelector('span.num bdi');
    expect(container.textContent).toBe('-25 k[U+202E]g');
    expect(unit?.textContent).toBe('k[U+202E]g');
    expect(unit?.querySelector('span.bidi')?.textContent).toBe('[U+202E]');
    expect(container.textContent).not.toContain(character(0x202e));
  });

  // AC-40.3, design 12.7 and 12.8: "975 to 1,015 kg", the unit once, as a SafeText with its marker chip.
  it('prints a range with its unit once, isolated', () => {
    const plain = render(<QuantityRange low="975.000" high="1015.000" unit="kg" />);
    expect(plain.container.textContent).toBe('975 to 1,015 kg');
    cleanup();

    const marked = render(<QuantityRange low={980} high="1020.5" unit={`B${character(0x2066)}ag`} />);
    expect(marked.container.textContent).toBe('980 to 1,020.5 B[U+2066]ag');
    expect(marked.container.querySelectorAll('span.num bdi span.bidi')).toHaveLength(1);
  });
});
