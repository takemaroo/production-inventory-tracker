import { describe, expect, it } from 'vitest';
import { customerLabel, idText, itemLabel } from './names';

describe('customerLabel and itemLabel (F-10, AC-37.4)', () => {
  // F-10, design 12.8: a customer is its name and id, with " (inactive)" after it when deactivated.
  it('names a customer with its id, and marks an inactive one', () => {
    expect(customerLabel({ id: 4, name: 'Deli Norte', isActive: true })).toBe('Deli Norte (#4)');
    expect(customerLabel({ id: 7, name: 'Cafe Sur', isActive: false })).toBe('Cafe Sur (#7) (inactive)');
  });

  // F-10: an item with its id, "Cheese (#3)".
  it('names an item with its id', () => {
    expect(itemLabel('Cheese', 3)).toBe('Cheese (#3)');
    expect(idText(12)).toBe('(#12)');
  });

  // AC-37.4: "Deli Norte" (#4) and the same name with a Cyrillic "o" (U+043E, built from its number) in place of the
  // Latin one (#9) look alike; their ids tell them apart.
  it('tells two look-alike customers apart by their ids', () => {
    const lookAlike = `Deli N${String.fromCodePoint(0x043e)}rte`;

    const labels = [customerLabel({ id: 4, name: 'Deli Norte', isActive: true }), customerLabel({ id: 9, name: lookAlike, isActive: true })];

    expect(labels).toEqual(['Deli Norte (#4)', `${lookAlike} (#9)`]);
    expect(new Set(labels).size).toBe(2);
  });

  // AC-37.3: a plain-text label (for the document title) shows direction characters as markers, never obeys them.
  it('shows direction characters in a name as markers', () => {
    expect(itemLabel(`Milk${String.fromCodePoint(0x202e)}gnp.exe`, 11)).toBe('Milk[U+202E]gnp.exe (#11)');
  });
});
