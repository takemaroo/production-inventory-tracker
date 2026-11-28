import { describe, expect, it } from 'vitest';
import { formatLocalTime, formatUtcOffset, todayLocalDate } from './times';

// The time zone is passed in ("injected") rather than taken from the machine, so these tests prove two zones on any
// computer, Windows and CI included, without setting TZ (task T-040). Mexico City is UTC-06:00 all year; London
// changes between UTC+00:00 and UTC+01:00; Kolkata shows a half-hour offset.
const mexicoCity = 'America/Mexico_City';
const london = 'Europe/London';
const kolkata = 'Asia/Kolkata';

/** Two digits, as the design's times show them. */
function two(value: number): string {
  return String(value).padStart(2, '0');
}

describe('formatLocalTime (F-8, design 12.7)', () => {
  // F-8, design 12.7: "2026-11-26 08:05", 24-hour, no seconds, in the zone's local time.
  it('shows the same moment in two time zones', () => {
    expect(formatLocalTime('2026-11-26T14:05:00Z', mexicoCity)).toBe('2026-11-26 08:05');
    expect(formatLocalTime('2026-11-26T14:05:00Z', london)).toBe('2026-11-26 14:05');
    expect(formatLocalTime('2026-11-26T14:05:00Z', kolkata)).toBe('2026-11-26 19:35');
  });

  // Design 12.7: each time is converted with the zone's rules for its own date (London is UTC+01:00 in summer).
  it("uses the zone rules of each time's own date", () => {
    expect(formatLocalTime('2026-07-01T07:05:00Z', london)).toBe('2026-07-01 08:05');
    expect(formatLocalTime('2026-12-01T07:05:00Z', london)).toBe('2026-12-01 07:05');
    // The minute London's clocks go forward, 29 March 2026 at 01:00 UTC.
    expect(formatLocalTime('2026-03-29T00:59:00Z', london)).toBe('2026-03-29 00:59');
    expect(formatLocalTime('2026-03-29T01:00:00Z', london)).toBe('2026-03-29 02:00');
  });

  // F-8: 24-hour time, so midnight is "00:05"; a time can fall on the previous local day.
  it('shows midnight as 00 and moves the date with the zone', () => {
    expect(formatLocalTime('2026-11-27T06:05:00Z', mexicoCity)).toBe('2026-11-27 00:05');
    expect(formatLocalTime('2026-11-27T05:59:59Z', mexicoCity)).toBe('2026-11-26 23:59');
  });

  // F-8: without a zone, the computer's own zone is used. The expectation is built from Date's local getters, which
  // use that same zone, so the test holds on any machine.
  it("uses the computer's zone by default", () => {
    const iso = '2026-11-26T14:05:00Z';
    const at = new Date(iso);
    const expected = `${at.getFullYear()}-${two(at.getMonth() + 1)}-${two(at.getDate())} ${two(at.getHours())}:${two(at.getMinutes())}`;

    expect(formatLocalTime(iso)).toBe(expected);
  });

  // Design 11, rule 1: nothing is invented; a text that is not a time is shown as it came.
  it('shows a text that is not a time as it came', () => {
    expect(formatLocalTime('not a time', mexicoCity)).toBe('not a time');
  });

  // AC-37.3, decision #260 d (T-040 review): such text never carries a direction character to the page.
  it('marks direction characters in a text that is not a time', () => {
    expect(formatLocalTime(`soon${String.fromCodePoint(0x202e)}`, mexicoCity)).toBe('soon[U+202E]');
  });
});

describe('formatUtcOffset (F-8, design 12.2 and 18.1)', () => {
  // F-8, design 12.7: the footer's offset, "UTC-06:00", and "UTC+00:00" at zero.
  it('gives the offset in force at the moment, in two zones', () => {
    const november = new Date('2026-11-26T14:05:00Z');

    expect(formatUtcOffset(november, mexicoCity)).toBe('UTC-06:00');
    expect(formatUtcOffset(november, london)).toBe('UTC+00:00');
    expect(formatUtcOffset(november, 'UTC')).toBe('UTC+00:00');
    expect(formatUtcOffset(november, kolkata)).toBe('UTC+05:30');
  });

  // Design 12.7: the offset in force now, which for London changes with summer time.
  it('follows summer time', () => {
    expect(formatUtcOffset(new Date('2026-07-01T12:00:34.567Z'), london)).toBe('UTC+01:00');
  });

  // F-8: without a zone, the computer's offset. Date's getTimezoneOffset gives the same, as minutes behind UTC.
  it("uses the computer's zone by default", () => {
    const at = new Date('2026-11-26T14:05:00Z');
    const ahead = -at.getTimezoneOffset();
    const expected = `UTC${ahead < 0 ? '-' : '+'}${two(Math.floor(Math.abs(ahead) / 60))}:${two(Math.abs(ahead) % 60)}`;

    expect(formatUtcOffset(at)).toBe(expected);
  });
});

describe('todayLocalDate (AC-39.1, design 12.7)', () => {
  // AC-39.1: the receipt date starts at today's local date, which may differ from the UTC date.
  it("gives today's date in the zone, in the date field's form", () => {
    const earlyUtc = new Date('2026-11-28T03:00:00Z');

    expect(todayLocalDate(earlyUtc, mexicoCity)).toBe('2026-11-27');
    expect(todayLocalDate(earlyUtc, 'UTC')).toBe('2026-11-28');
  });

  // AC-39.1: by default, the computer's own date.
  it("uses the computer's zone by default", () => {
    const now = new Date();

    expect(todayLocalDate(now)).toBe(`${now.getFullYear()}-${two(now.getMonth() + 1)}-${two(now.getDate())}`);
  });
});
