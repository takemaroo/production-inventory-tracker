import { describe, expect, it } from 'vitest';
import { ExactDecimal, parseApiJson, parseTypedDecimal } from './json';

describe('parseTypedDecimal (F-7, design 12.7)', () => {
  // Tech-lead follow-up (docs/00-status.md): the request body carries the typed digits, never a rounded JavaScript
  // number. As a JavaScript number, 999999999999999.999 would become 1000000000000000.
  it('sends the typed digits exactly', () => {
    expect(JSON.stringify({ quantity: parseTypedDecimal('995.5') })).toBe('{"quantity":995.5}');
    expect(JSON.stringify({ quantity: parseTypedDecimal('999999999999999.999') })).toBe(
      '{"quantity":999999999999999.999}',
    );
    expect(JSON.stringify({ quantity: parseTypedDecimal(' 0.10 ') })).toBe('{"quantity":0.10}');
  });

  // F-7: digits with at most one dot are accepted; they are written in JSON's own number form.
  it('writes a leading or trailing dot and extra leading zeros in JSON form', () => {
    expect(parseTypedDecimal('.5')?.digits).toBe('0.5');
    expect(parseTypedDecimal('5.')?.digits).toBe('5');
    expect(parseTypedDecimal('007.50')?.digits).toBe('7.50');
    expect(parseTypedDecimal('0')?.digits).toBe('0');
  });

  // F-7: "1,5", "1 000" and "-5" are refused on the screen and nothing is sent (design 12.7 adds "1e3").
  it.each(['1,5', '1 000', '-5', '1e3', '', '.', '1.2.3', '+5', '12a'])('refuses %j', (typed) => {
    expect(parseTypedDecimal(typed)).toBeNull();
  });
});

describe('ExactDecimal (code review, phase 3: only F-7 checked digits)', () => {
  // The review's should-fix: unchecked text such as "-5" or "1e3" must not become an ExactDecimal, so no request body
  // can carry a quantity that skipped F-7. "@ts-expect-error" makes the type check (npm run typecheck) fail if the line
  // below it ever compiles, so each one also proves the compiler refuses that route.
  it('has no public factory for unchecked text', () => {
    // @ts-expect-error: the factory is module-private, so ExactDecimal has no such member.
    const factory: unknown = ExactDecimal.fromCheckedDigits;

    expect(factory).toBeUndefined();
  });

  it.each(['-5', '1e3', '007', '5.', '.5', '1,5', ' 5', ''])('refuses %j at run time, even through a cast', (text) => {
    // A cast tells the compiler to stop checking; this is what code that ignores the types could do.
    const Unchecked = ExactDecimal as unknown as new (digits: string) => ExactDecimal;

    expect(() => new Unchecked(text)).toThrow(TypeError);
  });

  it('cannot have its digits changed after the check', () => {
    const decimal = parseTypedDecimal('995.5');
    if (decimal === null) {
      throw new Error('995.5 is an F-7 decimal.');
    }

    // ES modules run in strict mode, where writing to a property that has only a getter throws.
    expect(() => {
      (decimal as unknown as { digits: string }).digits = '-5';
    }).toThrow(TypeError);
    expect(JSON.stringify({ quantity: decimal })).toBe('{"quantity":995.5}');
  });
});

describe('parseApiJson (exact decimals from the API)', () => {
  // Tech-lead follow-up: quantities from the API are shown as the API wrote them (F-7), with no rounding.
  it('keeps decimals as their exact text and whole numbers as numbers', () => {
    const body = parseApiJson('{"id":7,"quantity":123456789012345.678,"percent":-2.50,"big":12345678901234567890}');

    expect(body).toEqual({ id: 7, quantity: '123456789012345.678', percent: '-2.50', big: '12345678901234567890' });
  });

  it('leaves strings, booleans and null untouched', () => {
    expect(parseApiJson('{"name":"1.5","active":true,"note":null}')).toEqual({ name: '1.5', active: true, note: null });
  });

  // AC-37.9 (T-038, decision #230): the API writes <, >, &, ', " and + inside strings as six-character escapes. The
  // reviver uses the source text for numbers only, so a string is read back as the characters it stands for, never
  // as its escapes. The escapes are built from character codes so that no tool can turn them into the characters in
  // this file.
  it('reads escaped strings from the API as their characters', () => {
    const escape = (hex: string) => String.fromCharCode(92) + 'u' + hex;
    const raw = `{"name":"${escape('003C')}b${escape('003E')} ${escape('0026')} ${escape('0027')}q${escape('0022')} 1${escape('002B')}1","quantity":1.50}`;

    expect(raw).not.toContain('<');
    expect(raw).not.toContain('+');
    expect(parseApiJson(raw)).toEqual({ name: `<b> & 'q" 1+1`, quantity: '1.50' });
  });
});
