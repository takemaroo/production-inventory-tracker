/**
 * Exact decimal numbers between the front end and the API (docs/03-architecture.md, part v1.2, "Exact decimals").
 *
 * Why this exists: JavaScript has one number type, a binary floating-point number with about 15 to 17 significant
 * digits. The API's quantities are exact decimals (C# decimal, up to 999,999,999.999 on input and more for sums), so
 * turning them into JavaScript numbers could round them. Two rules prevent it:
 *
 * - Sending: a quantity the user typed goes into the request body as the digits they typed (F-7, design 12.7), never
 *   through a JavaScript number. `ExactDecimal` holds those digits and writes them as a JSON number with JSON.rawJSON.
 * - Receiving: `parseApiJson` keeps every JSON number that is not a safe whole number as its exact source text, so
 *   "1015.001" stays "1015.001". Whole numbers such as ids and page counts stay ordinary numbers. The formatting code
 *   prints a quantity from that text and computes nothing: the API calculates every business figure (PRD row 70).
 */

/** A JSON number as the front end holds it: a safe whole number as a number, anything else as its exact digits. */
export type ApiNumber = number | string;

/** A decimal typed by the user, checked and ready to send. Create one with {@link parseTypedDecimal}. */
export class ExactDecimal {
  /** The digits to send, for example "995.5": no sign, no thousands separator, no exponent. */
  readonly digits: string;

  private constructor(digits: string) {
    this.digits = digits;
  }

  /** Used only by parseTypedDecimal, which has already checked the digits. */
  static fromCheckedDigits(digits: string): ExactDecimal {
    return new ExactDecimal(digits);
  }

  /** JSON.stringify calls toJSON on any object that has it; rawJSON makes it write the digits as a bare JSON number. */
  toJSON(): RawJSON {
    return JSON.rawJSON(this.digits);
  }
}

// F-7: one or more digits with at most one dot. ".5" and "5." have digits and one dot, so they are accepted and
// written in JSON's own form below ("0.5", "5").
const typedDecimalPattern = /^(\d+\.?\d*|\.\d+)$/;

/**
 * Checks what the user typed in a quantity or amount field (F-7, design 12.7) and returns it ready to send, or null when
 * it is not a plain decimal ("1,5", "1 000", "-5", "1e3", or empty). Spaces before and after are removed. Every other
 * rule (decimal places, zero, maximums, ranges) is the API's to judge and is shown by F-1.
 */
export function parseTypedDecimal(typed: string): ExactDecimal | null {
  const text = typed.trim();
  if (!typedDecimalPattern.test(text)) {
    return null;
  }

  // JSON numbers cannot start with a dot, end with a dot, or have extra leading zeros, so "007.50" becomes "7.50"
  // and ".5" becomes "0.5". The value is unchanged; trailing zeros after the dot are kept, as the API ignores them.
  const [whole = '', fraction = ''] = text.split('.');
  const wholeDigits = whole.replace(/^0+(?=\d)/, '') || '0';
  return ExactDecimal.fromCheckedDigits(fraction === '' ? wholeDigits : `${wholeDigits}.${fraction}`);
}

/**
 * Parses a JSON response body. Every number with a dot or an exponent, or too large to be exact in JavaScript, is
 * returned as its source text (a string); every other number is returned as a number.
 */
export function parseApiJson(text: string): unknown {
  return JSON.parse(text, (_key, value, context) => {
    if (typeof value !== 'number' || context.source === undefined) {
      return value;
    }
    const isSafeWholeNumber = /^-?\d+$/.test(context.source) && Number.isSafeInteger(value);
    return isSafeWholeNumber ? value : context.source;
  });
}
