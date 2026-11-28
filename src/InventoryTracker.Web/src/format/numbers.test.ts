import { describe, expect, it } from 'vitest';
import {
  formatAmountPerUnit,
  formatDecimal,
  formatPercent,
  formatQuantity,
  formatRange,
  formatSignedPercent,
  formatSignedDecimal,
  formatSignedQuantity,
} from './numbers';

// Every value below is given as the front end holds it after parseApiJson (src/api/json.ts): a safe whole number as
// a number, any other number as its exact source text (architecture 16, decision #231).

describe('formatQuantity (F-7, design 12.7)', () => {
  // F-7 and design 12.7, the design's own examples: comma thousands, trailing zeros removed, the unit after a space.
  it.each([
    [1015, 'kg', '1,015 kg'],
    ['1015.000', 'kg', '1,015 kg'],
    ['0.6', 'kg', '0.6 kg'],
    ['0.600', 'kg', '0.6 kg'],
    [1001, 'Bag', '1,001 Bag'],
    [0, 'kg', '0 kg'],
    ['974.999', 'kg', '974.999 kg'],
    ['1000000', 'kg', '1,000,000 kg'],
  ])('prints %j %s as %j', (value, unit, expected) => {
    expect(formatQuantity(value, unit)).toBe(expected);
  });

  // Architecture 16, decision #231: values beyond the 15 to 17 digits a JavaScript number holds are printed exactly.
  it('prints large values beyond 15 digits exactly', () => {
    expect(formatQuantity('123456789012345678901234.567', 'kg')).toBe('123,456,789,012,345,678,901,234.567 kg');
    expect(formatQuantity('999999999999999.999', 'kg')).toBe('999,999,999,999,999.999 kg');
    expect(formatQuantity('12345678901234567890', 'Bag')).toBe('12,345,678,901,234,567,890 Bag');
  });

  // Architecture 16: more decimals than the format shows are rounded half away from zero on the digits, as the API
  // rounds (design 2.4), never through a JavaScript number.
  it.each([
    ['0.0005', '0.001 kg'],
    ['0.00049999', '0 kg'],
    ['1.2345', '1.235 kg'],
    ['1.2344', '1.234 kg'],
    ['999.9995', '1,000 kg'],
    ['99999999999999999.9995', '100,000,000,000,000,000 kg'],
    ['-0.0005', '-0.001 kg'],
    ['-1.2345', '-1.235 kg'],
  ])('rounds %j half away from zero to %j', (value, expected) => {
    expect(formatQuantity(value, 'kg')).toBe(expected);
  });

  // Design 12.7: a zero has no sign, even a tiny negative one that rounds to zero.
  it('prints a negative value that rounds to zero as 0', () => {
    expect(formatQuantity('-0.0004', 'kg')).toBe('0 kg');
  });

  // AC-37.3: the unit is API text; a direction character in it is shown as a marker, never obeyed.
  it('shows direction characters in the unit as markers', () => {
    const unit = `k${String.fromCodePoint(0x202e)}g`;

    expect(formatQuantity(5, unit)).toBe('5 k[U+202E]g');
  });

  // Design 11, rule 1: the front end invents no figure; text that is not a plain decimal is shown as it came.
  it('shows a value that is not a plain decimal as it came', () => {
    expect(formatQuantity('1E3', 'kg')).toBe('1E3 kg');
  });

  // AC-37.3, decision #260 d (T-040 review): such text, too, never carries a direction character to the page.
  it('marks direction characters in a value that is not a plain decimal', () => {
    expect(formatDecimal(`1${String.fromCodePoint(0x202e)}5`)).toBe('1[U+202E]5');
  });
});

describe('formatAmountPerUnit (design 12.7, decision #203)', () => {
  // Design 12.7: amounts per unit show up to 6 decimals, because the API stores 6.
  it.each([
    ['0.015', '0.015 kg'],
    ['0.000125', '0.000125 kg'],
    ['0.0001250', '0.000125 kg'],
    ['1.0000005', '1.000001 kg'],
    [2, '2 kg'],
  ])('prints %j as %j', (value, expected) => {
    expect(formatAmountPerUnit(value, 'kg')).toBe(expected);
  });
});

describe('formatSignedQuantity (design 12.7, 6.1)', () => {
  // Design 12.7: signed differences, "+15 kg", "-25 kg", "0 kg"; the minus is the ASCII hyphen-minus (design 6.1).
  it.each([
    [15, '+15 kg'],
    [-25, '-25 kg'],
    [0, '0 kg'],
    ['29.500', '+29.5 kg'],
    ['-0.0001', '0 kg'],
    ['1234.5', '+1,234.5 kg'],
  ])('prints %j as %j', (value, expected) => {
    expect(formatSignedQuantity(value, 'kg')).toBe(expected);
  });
});

describe('formatSignedPercent and formatPercent (F-7, design 12.7)', () => {
  // F-7, design 12.7: a sign and 2 decimals; a zero is "0.00%".
  it.each([
    ['1.5', '+1.50%'],
    ['-2.50', '-2.50%'],
    ['2.9', '+2.90%'],
    [0, '0.00%'],
    ['0.000', '0.00%'],
    ['-0.004', '0.00%'],
    [12, '+12.00%'],
  ])('signed: prints %j as %j', (value, expected) => {
    expect(formatSignedPercent(value)).toBe(expected);
  });

  // Architecture 16: half away from zero on the digits. 1.005 as a JavaScript number is 1.00499..., which would
  // round to 1.00; on the digits it is 1.01.
  it('rounds a percentage half away from zero on its digits', () => {
    expect(formatSignedPercent('1.005')).toBe('+1.01%');
    expect(formatSignedPercent('-1.005')).toBe('-1.01%');
  });

  // Design 12.7: the tolerance is not a difference and has no sign.
  it('prints the tolerance without a sign', () => {
    expect(formatPercent(2)).toBe('2.00%');
    expect(formatPercent('2.0')).toBe('2.00%');
  });
});

describe('formatRange (AC-40.3, design 12.7)', () => {
  // AC-40.3, design 12.7: "975 to 1,015 kg".
  it('prints both ends and the unit once', () => {
    expect(formatRange('975.000', '1015.000', 'kg')).toBe('975 to 1,015 kg');
    expect(formatRange(980, '1020.5', 'Bag')).toBe('980 to 1,020.5 Bag');
  });
});

describe('formatDecimal (F-7)', () => {
  // F-7: the same format without a unit, for figures that have none.
  it('prints a number without a unit', () => {
    expect(formatDecimal('1015.5')).toBe('1,015.5');
    expect(formatDecimal('0.1234567', 6)).toBe('0.123457');
  });

  // Design 12.7, 6.1: the signed digits without a unit, for the Quantity component's signed form.
  it('prints a signed number without a unit', () => {
    expect(formatSignedDecimal(15)).toBe('+15');
    expect(formatSignedDecimal('-25.0004')).toBe('-25');
    expect(formatSignedDecimal('-0.0004')).toBe('0');
  });
});
