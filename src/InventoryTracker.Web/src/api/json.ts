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

// F-7: one or more digits with at most one dot. ".5" and "5." have digits and one dot, so they are accepted and
// written in JSON's own form below ("0.5", "5").
const typedDecimalPattern = /^(\d+\.?\d*|\.\d+)$/;

// The only text an ExactDecimal may hold: what parseTypedDecimal produces from an F-7 decimal. No sign, no exponent,
// no leading zeros except a single "0" before the dot, and at least one digit after a dot. "-5", "1e3", "007" and
// "5." do not match. Every text that matches is also a valid JSON number, which JSON.rawJSON requires.
const checkedDigitsPattern = /^(0|[1-9]\d*)(\.\d+)?$/;

// Module-private: the one way to create an ExactDecimal. It is assigned inside the class below (in a "static block",
// which runs once when the class is defined and may call the private constructor) and is not exported, so no code
// outside this file can reach it. Code review (phase 3): a public factory let any caller wrap unchecked text.
let createFromCheckedDigits: (digits: string) => ExactDecimal;

/**
 * A decimal typed by the user, checked against F-7 and ready to send. The only way to get one is
 * {@link parseTypedDecimal}, so a request body can never carry a quantity that skipped the F-7 check (architecture 16).
 */
export class ExactDecimal {
  // "#digits" is a JavaScript private field: unlike TypeScript's `private` keyword, which is checked only when the
  // code is compiled, the browser itself refuses any access from outside the class. So the digits cannot be changed
  // after the check, even by code that ignores the types.
  readonly #digits: string;

  // TypeScript's `private constructor` stops other files from writing `new ExactDecimal(...)`. It is a compile-time
  // rule only, so the constructor also checks the text itself and throws: unchecked text cannot become an
  // ExactDecimal at run time either.
  private constructor(digits: string) {
    if (!checkedDigitsPattern.test(digits)) {
      throw new TypeError('ExactDecimal accepts only digits checked by parseTypedDecimal (F-7).');
    }
    this.#digits = digits;
  }

  static {
    createFromCheckedDigits = (digits) => new ExactDecimal(digits);
  }

  /** The digits to send, for example "995.5": no sign, no thousands separator, no exponent. */
  get digits(): string {
    return this.#digits;
  }

  /** JSON.stringify calls toJSON on any object that has it; rawJSON makes it write the digits as a bare JSON number. */
  toJSON(): RawJSON {
    return JSON.rawJSON(this.#digits);
  }
}

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
  return createFromCheckedDigits(fraction === '' ? wholeDigits : `${wholeDigits}.${fraction}`);
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
