/**
 * Numbers on screen (F-7, design 12.7 and 6.1; architecture 16 "Exact decimals", decision #231).
 *
 * The API's quantities are exact decimals. `parseApiJson` (src/api/json.ts) keeps every one that is not a safe whole
 * number as its exact source text, for example "1015.500". The functions here print that text the way the API writes
 * numbers in its own messages: comma thousands, dot decimals, trailing zeros removed, the unit after a space.
 *
 * They work on the digits as text and never turn a quantity into a JavaScript number. A JavaScript number holds only
 * about 15 to 17 significant digits, so "123456789012345678.5" would come out wrong, and 1.005 is stored as
 * 1.00499999..., so it would round the wrong way. The front end also calculates no business figure (PRD row 70): it
 * only prints what the API sent.
 */
import type { ApiNumber } from '../api/json';
import { showDirectionCharacters } from './direction';

/** F-7: quantities show at most 3 decimals. */
export const quantityDecimals = 3;

/** Design 12.7, decision #203: amounts per unit on recipes and order lines show up to 6, as the API stores 6. */
export const amountPerUnitDecimals = 6;

/** F-7, design 6.1: percentages always show exactly 2 decimals. */
export const percentDecimals = 2;

/**
 * A decimal split into its sign and digits, for example "-1015.5" as negative, "1015" and "5". Only plain decimals are
 * read: an optional minus, digits, and optionally a dot and more digits. That is every form the API writes, because a
 * C# decimal is never written with an exponent.
 */
interface DecimalDigits {
  readonly negative: boolean;
  readonly whole: string;
  readonly fraction: string;
}

const plainDecimalPattern = /^(-)?(\d+)(?:\.(\d+))?$/;

/** Reads a number from the API into its digits, or null when it is not a plain decimal (see {@link DecimalDigits}). */
function readDigits(value: ApiNumber): DecimalDigits | null {
  // A safe whole number arrives as a JavaScript number (an id, a count, or a quantity such as 1015); String gives its
  // exact digits. Any other number is not something parseApiJson produces, but String still gives its shortest exact
  // form, and the pattern refuses the exponent forms ("1e+21") and the non-finite values.
  const text = typeof value === 'number' ? String(value) : value;
  const match = plainDecimalPattern.exec(text);
  if (match === null) {
    return null;
  }
  // JSON allows no leading zeros, but stripping them costs nothing and keeps "007" from printing as "007".
  const whole = (match[2] ?? '0').replace(/^0+(?=\d)/, '');
  return { negative: match[1] === '-', whole, fraction: match[3] ?? '' };
}

/**
 * Architecture 16: rounds to `places` decimals, half away from zero, on the digits themselves, as the API rounds
 * (design 2.4). "0.0005" to 3 places is "0.001"; "-0.0005" is "-0.001"; "999.9995" is "1000.000".
 */
function roundDigits(number: DecimalDigits, places: number): DecimalDigits {
  if (number.fraction.length <= places) {
    return number;
  }
  // Half away from zero on the size of the number: the sign is kept aside, so a 5 or more in the first dropped place
  // always rounds the size up, for negative numbers too.
  const kept = number.whole + number.fraction.slice(0, places);
  const firstDropped = number.fraction.charAt(places);
  const rounded = firstDropped >= '5' ? addOne(kept) : kept;
  // Adding one can make the number one digit longer ("999999" becomes "1000000"); the extra digit belongs to the
  // whole part, so the fraction is always the last `places` digits.
  const whole = rounded.slice(0, rounded.length - places) || '0';
  return { negative: number.negative, whole, fraction: rounded.slice(rounded.length - places) };
}

/** Adds one to a string of digits, carrying as on paper: "129" becomes "130", "999" becomes "1000". */
function addOne(digits: string): string {
  const result = digits.split('');
  for (let index = result.length - 1; index >= 0; index -= 1) {
    if (result[index] === '9') {
      result[index] = '0';
    } else {
      result[index] = String(Number(result[index]) + 1);
      return result.join('');
    }
  }
  return `1${result.join('')}`;
}

/** True when every digit is zero, so "0.000" and "-0" count as zero, which has no sign. */
function isZero(number: DecimalDigits): boolean {
  return /^0+$/.test(number.whole + number.fraction || '0');
}

/** F-7: "1015" becomes "1,015". A loop over the digits, so the result is exact however many there are. */
function groupThousands(whole: string): string {
  let grouped = '';
  for (let index = 0; index < whole.length; index += 1) {
    const left = whole.length - index;
    grouped += whole.charAt(index);
    if (left > 1 && (left - 1) % 3 === 0) {
      grouped += ',';
    }
  }
  return grouped;
}

/**
 * Prints the digits with comma thousands. `fixed` true keeps exactly `places` decimals (percentages); false removes
 * trailing zeros and the dot when nothing is left (quantities). `sign` true adds "+" before a positive number.
 */
function printDigits(value: ApiNumber, places: number, fixed: boolean, sign: boolean): string {
  const digits = readDigits(value);
  if (digits === null) {
    // Not a plain decimal: never sent by the API (it writes no exponents), so it is shown as it came rather than
    // guessed at. No figure is ever invented on screen (design 11, rule 1). Like all text shown, its direction
    // characters, if it ever had any, become markers (AC-37.3, decision #260 d).
    return showDirectionCharacters(String(value));
  }
  const rounded = roundDigits(digits, places);
  const fraction = fixed ? rounded.fraction.padEnd(places, '0') : rounded.fraction.replace(/0+$/, '');
  const size = `${groupThousands(rounded.whole)}${fraction === '' ? '' : `.${fraction}`}`;
  // Design 12.7: a zero has no sign, "0 kg" and "0.00%", even when it was a tiny negative number before rounding.
  if (isZero(rounded)) {
    return size;
  }
  // Design 6.1: the minus is the ASCII hyphen-minus, as in the API's messages.
  if (rounded.negative) {
    return `-${size}`;
  }
  return sign ? `+${size}` : size;
}

/** A unit name is API text: its direction characters are shown as markers, never obeyed (AC-37.3). */
function unitText(unit: string): string {
  return showDirectionCharacters(unit);
}

// Two kinds of function follow. The digits-only ones (formatDecimal, formatSignedDecimal) give the number without
// its unit; the `Quantity` and `QuantityRange` components (src/components/SafeText.tsx) put them on the page with the
// unit in its own SafeText, so the unit is isolated and a direction character in it is drawn as the marker chip
// (design 12.8). The "...Quantity" and formatRange string forms include the unit as plain text: use them only where
// the page can hold text alone, such as the document title or an accessible name.

/**
 * F-7: a number in the API's message format without a unit, "1,015" or "0.6". At most `maxDecimals` decimals (3
 * unless given), extra ones rounded half away from zero, trailing zeros removed.
 */
export function formatDecimal(value: ApiNumber, maxDecimals: number = quantityDecimals): string {
  return printDigits(value, maxDecimals, false, false);
}

/** Design 12.7, 6.1: a signed difference without its unit, "+15", "-25", and "0" for none. */
export function formatSignedDecimal(value: ApiNumber, maxDecimals: number = quantityDecimals): string {
  return printDigits(value, maxDecimals, false, true);
}

/**
 * F-7, design 12.7: a quantity with its unit as plain text, "1,015 kg", "0.6 kg", "1,001 Bag", "0 kg". For plain-text
 * places only (a title, an accessible name); on the page use `<Quantity>`.
 */
export function formatQuantity(value: ApiNumber, unit: string): string {
  return `${formatDecimal(value, quantityDecimals)} ${unitText(unit)}`;
}

/**
 * Design 12.7, decision #203: an amount per unit on a recipe or order line, up to 6 decimals, "0.000125 kg", as plain
 * text. On the page use `<Quantity form="amount-per-unit">`.
 */
export function formatAmountPerUnit(value: ApiNumber, unit: string): string {
  return `${formatDecimal(value, amountPerUnitDecimals)} ${unitText(unit)}`;
}

/**
 * Design 12.7, 6.1: a signed difference as plain text, "+15 kg", "-25 kg", and "0 kg" for none. On the page use
 * `<Quantity form="signed">`.
 */
export function formatSignedQuantity(value: ApiNumber, unit: string): string {
  return `${formatSignedDecimal(value, quantityDecimals)} ${unitText(unit)}`;
}

/**
 * F-7, design 12.7: a percentage difference (a variance), with a sign and 2 decimals: "+1.50%", "-2.50%", and
 * "0.00%" for zero. The API sends percent values (1.5 means 1.50%, design 2.4), so nothing is multiplied. A
 * percentage holds no API text, so this string is right on the page too.
 */
export function formatSignedPercent(value: ApiNumber): string {
  return `${printDigits(value, percentDecimals, true, true)}%`;
}

/** Design 12.7: the tolerance is not a difference, so it has no sign: "2.00%". Right on the page too. */
export function formatPercent(value: ApiNumber): string {
  return `${printDigits(value, percentDecimals, true, false)}%`;
}

/**
 * AC-40.3, design 12.7: an accepted range as plain text, "975 to 1,015 kg". Both ends come from the API; nothing is
 * calculated. On the page use `<QuantityRange>`.
 */
export function formatRange(low: ApiNumber, high: ApiNumber, unit: string): string {
  return `${formatDecimal(low, quantityDecimals)} to ${formatDecimal(high, quantityDecimals)} ${unitText(unit)}`;
}
