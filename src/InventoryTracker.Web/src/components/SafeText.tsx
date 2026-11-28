/**
 * The one way every string from the API is put on the page (US-37: AC-37.1, AC-37.3, AC-37.4; design 12.8;
 * architecture 18 "Safe text"; SEC-10, SEC-27).
 *
 * Names, descriptions, notes, reasons, references, notification messages, emails and error messages are typed by
 * people, so any of them could hold "<script>" or a disguise. Three rules keep that harmless:
 *
 * 1. Text binding only (AC-37.1, AC-37.2). React puts a string given in JSX braces, `{text}`, on the page as text:
 *    the browser shows "<b>" as the three characters, never as bold. No raw-HTML insertion is used anywhere (the
 *    source guard, src/guards.test.ts, fails the tests if one appears).
 * 2. Direction characters are shown, not obeyed (AC-37.3). Each is replaced by a visible marker, "[U+202E]", drawn
 *    as a small chip (the stylesheet's `bidi` class, design 17.5) so it cannot be mistaken for typed brackets.
 * 3. Isolation (design 12.8). The text sits in a `<bdi>` element ("bidirectional isolate"). The browser works out the
 *    text's direction inside it on its own and keeps it from mixing with the text around it, so a right-to-left
 *    name can never reorder the id, unit or words beside it. Its class `t` also lets long words wrap anywhere, so
 *    nothing runs off a 360 px screen.
 *
 * Besides SafeText, this file has the components that place API text beside the front end's own: `NameWithId` (a
 * name and its id) and `Quantity` and `QuantityRange` (figures and their unit).
 */
import { Fragment } from 'react';
import { directionMarker, splitDirectionCharacters } from '../format/direction';
import { idText, inactiveText } from '../format/names';
import { amountPerUnitDecimals, formatDecimal, formatSignedDecimal, quantityDecimals } from '../format/numbers';
import type { ApiNumber } from '../api/json';

/** The props of {@link SafeText}. */
export interface SafeTextProps {
  /** The API's text, exactly as it arrived. It is never changed; only how it is drawn. */
  readonly text: string;
  /**
   * Design 12.8: notes and reasons keep their line breaks. When true, each line break in the text is drawn as one
   * (a `<br>` element); otherwise a line break shows as a space, as everywhere else in HTML.
   */
  readonly keepLineBreaks?: boolean;
}

// "\r\n" (Windows), "\r" and "\n" each count as one line break, so a note typed on any computer breaks the same way.
const lineBreakPattern = /\r\n|\r|\n/;

/**
 * Prints one string from the API safely: as text, in its own `<bdi>`, with each direction character drawn as a
 * marker, and line breaks kept when asked (see the rules at the top of this file).
 */
export function SafeText({ text, keepLineBreaks = false }: SafeTextProps) {
  const lines = keepLineBreaks ? text.split(lineBreakPattern) : [text];
  return (
    <bdi className="t">
      {/*
        A list in JSX is drawn from an array; React asks for a "key" on each item so it can tell the items apart when
        it redraws. The text is fixed for as long as this element shows, so the position is a good enough key.
      */}
      {lines.map((line, lineIndex) => (
        <Fragment key={lineIndex}>
          {lineIndex > 0 ? <br /> : null}
          {splitDirectionCharacters(line).map((segment, segmentIndex) =>
            segment.kind === 'text' ? (
              <Fragment key={segmentIndex}>{segment.text}</Fragment>
            ) : (
              <span key={segmentIndex} className="bidi">
                {directionMarker(segment.codePoint)}
              </span>
            ),
          )}
        </Fragment>
      ))}
    </bdi>
  );
}

/** The props of {@link NameWithId}. */
export interface NameWithIdProps {
  /** The record's name, from the API. */
  readonly name: string;
  /** The record's id, shown after the name as "(#4)". */
  readonly id: number;
  /** True for a deactivated customer, which shows " (inactive)" after the id (F-10). */
  readonly inactive?: boolean;
}

/**
 * F-10, AC-37.4, design 12.8: a customer or item on the page, "Deli Norte (#4)" or "Cafe Sur (#7) (inactive)". The
 * name is a {@link SafeText} of its own, so it is isolated and the id after it can never be reordered by it. The id
 * is in the stylesheet's `num` class, which keeps "(#4)" on one line, so it is never cut or wrapped apart (SEC-25,
 * decision #220); only the name wraps. For plain-text places (the document title), use `customerLabel` or `itemLabel`
 * from src/format/names.ts.
 */
export function NameWithId({ name, id, inactive = false }: NameWithIdProps) {
  return (
    <>
      <SafeText text={name} /> <span className="num">{idText(id)}</span>
      {inactive ? inactiveText : null}
    </>
  );
}

/**
 * Which F-7 form a {@link Quantity} takes (design 12.7): `quantity` "1,015 kg" (up to 3 decimals), `amount-per-unit`
 * "0.000125 kg" (up to 6, decision #203), `signed` "+15 kg", "-25 kg", "0 kg".
 */
export type QuantityForm = 'quantity' | 'amount-per-unit' | 'signed';

/** The props of {@link Quantity}. */
export interface QuantityProps {
  /** The figure as the API sent it (architecture 16: exact text unless a safe whole number). */
  readonly value: ApiNumber;
  /** The unit's name, from the API. */
  readonly unit: string;
  /** The form; `quantity` unless given. */
  readonly form?: QuantityForm;
}

/** The digits of a {@link Quantity}, without the unit. */
function quantityDigits(value: ApiNumber, form: QuantityForm): string {
  switch (form) {
    case 'quantity':
      return formatDecimal(value, quantityDecimals);
    case 'amount-per-unit':
      return formatDecimal(value, amountPerUnitDecimals);
    case 'signed':
      return formatSignedDecimal(value, quantityDecimals);
  }
}

/**
 * F-7, design 12.7 and 12.8: a quantity on the page, "1,015 kg". The digits are the front end's own text; the unit is
 * API text, so it is a {@link SafeText}: isolated, with any direction character drawn as the marker chip. The whole
 * is in the stylesheet's `num` class (tabular figures, one line), so a quantity never wraps away from its unit. The
 * string forms in src/format/numbers.ts are for plain-text places only.
 */
export function Quantity({ value, unit, form = 'quantity' }: QuantityProps) {
  return (
    <span className="num">
      {quantityDigits(value, form)} <SafeText text={unit} />
    </span>
  );
}

/** The props of {@link QuantityRange}. */
export interface QuantityRangeProps {
  readonly low: ApiNumber;
  readonly high: ApiNumber;
  /** The unit's name, from the API; shown once, after the second figure. */
  readonly unit: string;
}

/**
 * AC-40.3, design 12.7: an accepted range on the page, "975 to 1,015 kg", with the unit as a {@link SafeText} as in
 * {@link Quantity}. Both ends come from the API; nothing is calculated (PRD row 70).
 */
export function QuantityRange({ low, high, unit }: QuantityRangeProps) {
  return (
    <span className="num">
      {formatDecimal(low, quantityDecimals)} to {formatDecimal(high, quantityDecimals)} <SafeText text={unit} />
    </span>
  );
}
