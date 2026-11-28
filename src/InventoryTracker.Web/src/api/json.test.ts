import { describe, expect, it } from 'vitest';
import { parseApiJson, parseTypedDecimal } from './json';

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

describe('parseApiJson (exact decimals from the API)', () => {
  // Tech-lead follow-up: quantities from the API are shown as the API wrote them (F-7), with no rounding.
  it('keeps decimals as their exact text and whole numbers as numbers', () => {
    const body = parseApiJson('{"id":7,"quantity":123456789012345.678,"percent":-2.50,"big":12345678901234567890}');

    expect(body).toEqual({ id: 7, quantity: '123456789012345.678', percent: '-2.50', big: '12345678901234567890' });
  });

  it('leaves strings, booleans and null untouched', () => {
    expect(parseApiJson('{"name":"1.5","active":true,"note":null}')).toEqual({ name: '1.5', active: true, note: null });
  });
});
