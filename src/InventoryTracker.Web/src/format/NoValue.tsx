/**
 * A value that is not known yet (design 12.7; design 2.3: such a value is `null` in the API's answer).
 *
 * This file holds React components. A component is a function whose name starts with a capital letter and that
 * returns what to show, written in JSX: HTML-like tags inside TypeScript, which React turns into page elements. Its
 * input is one object, its "props" (properties), written like HTML attributes where it is used:
 * `<ValueOrNone value={row.freeLeftover} format={...} />`.
 */
import type { ReactNode } from 'react';

/** Design 12.7: what a value not yet known shows, a hyphen. */
export const noValueText = '-';

/** Design 12.7: what a screen reader says instead of the hyphen. */
export const noValueSpokenText = 'none';

/**
 * Design 12.7: "-" on screen, read as "none" by a screen reader. The hyphen is hidden from screen readers with
 * `aria-hidden`, and "none" is hidden from the eye with the stylesheet's visually-hidden class `vh` (design 17), so
 * each audience gets one word, never both.
 */
export function NoValue() {
  // "<>...</>" is a "fragment": it groups two elements without adding an element of its own around them.
  return (
    <>
      <span aria-hidden="true">{noValueText}</span>
      <span className="vh">{noValueSpokenText}</span>
    </>
  );
}

/** The props of {@link ValueOrNone}. `T` is the type of the value when it is known, for example `ApiNumber`. */
export interface ValueOrNoneProps<T> {
  /** The API's value; `null` means not known yet. */
  readonly value: T | null;
  /**
   * Turns a known value into what to show. A `ReactNode` is anything React can draw: an element, a string, a number.
   * For a quantity, give the `Quantity` component, so its unit (API text) is a SafeText (design 12.8):
   * `(v) => <Quantity value={v} unit={row.unit} />`. A plain string is right only for text the front end owns, such
   * as `formatSignedPercent(v)` or `formatLocalTime(v)`.
   */
  readonly format: (value: T) => ReactNode;
}

/** Design 12.7: the formatted value, or {@link NoValue} when it is `null`. */
export function ValueOrNone<T>({ value, format }: ValueOrNoneProps<T>) {
  // A string inside JSX braces, like {format(value)}, is always put on the page as text, never read as HTML
  // (AC-37.2).
  return value === null ? <NoValue /> : <>{format(value)}</>;
}
