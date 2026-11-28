/**
 * Times on screen (F-8, design 12.7; architecture 16 "Times"; owner's answer to Q107: local time, offset in the footer).
 *
 * The API sends every time in UTC, in ISO 8601 with a "Z" ("2026-11-26T14:05:00Z", design 2.3). The screen shows it
 * in the computer's own time zone as "2026-11-26 08:05": 24-hour, no seconds. Each time is converted with the zone's
 * rules for its own date, so a summer time and a winter time each get their own offset.
 *
 * Times are not business figures, so the built-in `Date` and `Intl` do the work (no date library, architecture 11).
 * `Intl.DateTimeFormat` is the browser's built-in formatter: given a time zone name such as "America/Mexico_City", it
 * knows that zone's offsets and summer-time rules for every date.
 *
 * Every function takes an optional `timeZone` (an IANA zone name). Screens leave it out, which means "this computer's
 * zone". Tests pass one, so they prove two zones on any machine without changing its settings (task T-040).
 */

import { showDirectionCharacters } from './direction';

/** The parts of a time as a clock in some zone shows it, each as two digits (the year as four). */
interface WallClock {
  readonly year: string;
  readonly month: string;
  readonly day: string;
  readonly hour: string;
  readonly minute: string;
  readonly second: string;
}

// Making a formatter takes longer than using one, so a formatter for a named zone is made once and kept. The
// computer's own zone is not kept: a formatter remembers the zone it was made in, and someone may change the zone in
// Windows while the page is open. A fresh one each time follows that change at the next time shown (F-8: "the
// computer's actual offset"). A list of 50 rows with two times each makes 100, about 5 ms in all (measured with
// Node.js 24 on the owner's machine, 2026-11-28), which no one notices.
const namedZoneFormatters = new Map<string, Intl.DateTimeFormat>();

function formatterFor(timeZone: string | undefined): Intl.DateTimeFormat {
  if (timeZone === undefined) {
    return makeFormatter(undefined);
  }
  let formatter = namedZoneFormatters.get(timeZone);
  if (formatter === undefined) {
    formatter = makeFormatter(timeZone);
    namedZoneFormatters.set(timeZone, formatter);
  }
  return formatter;
}

/** A formatter giving every part of a time, two digits each, as a clock in the zone shows it. */
function makeFormatter(timeZone: string | undefined): Intl.DateTimeFormat {
  return new Intl.DateTimeFormat('en-US', {
    timeZone,
    // The Western calendar and digits, whatever the computer's language: "2026-11-26 08:05" everywhere.
    calendar: 'gregory',
    numberingSystem: 'latn',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    // F-8: 24-hour time, midnight "00:05" (not "24:05", which some settings would give).
    hourCycle: 'h23',
  });
}

/** What a clock in the zone shows at that moment. */
function wallClock(at: Date, timeZone: string | undefined): WallClock {
  // formatToParts gives each piece separately ({ type: 'hour', value: '08' }), so the order and separators of the
  // "en-US" form never matter: the pieces are put together in the design's order below.
  const parts = formatterFor(timeZone).formatToParts(at);
  const part = (type: Intl.DateTimeFormatPartTypes): string => parts.find((piece) => piece.type === type)?.value ?? '00';
  return {
    year: part('year').padStart(4, '0'),
    month: part('month'),
    day: part('day'),
    hour: part('hour'),
    minute: part('minute'),
    second: part('second'),
  };
}

/**
 * F-8, design 12.7: an API time in local time, "2026-11-26 08:05". A text that is not a time is shown as it came (the
 * API always sends a valid one), so nothing is invented; its direction characters become markers, as in all text
 * shown (AC-37.3, decision #260 d).
 */
export function formatLocalTime(iso: string, timeZone?: string): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) {
    return showDirectionCharacters(iso);
  }
  const clock = wallClock(at, timeZone);
  return `${clock.year}-${clock.month}-${clock.day} ${clock.hour}:${clock.minute}`;
}

/**
 * F-8, design 12.2 and 18.1: the offset in force at `at` (now, unless given), for the footer "Times are local
 * (UTC-06:00).": "UTC-06:00", "UTC+05:30", and "UTC+00:00" at zero.
 */
export function formatUtcOffset(at: Date = new Date(), timeZone?: string): string {
  // The offset is the difference between what the zone's clock shows and the same moment in UTC. Date.UTC reads the
  // zone's clock as if it were UTC; the moment itself is cut to whole seconds, as the clock shows no milliseconds.
  const clock = wallClock(at, timeZone);
  const clockAsUtc = Date.UTC(
    Number(clock.year),
    Number(clock.month) - 1,
    Number(clock.day),
    Number(clock.hour),
    Number(clock.minute),
    Number(clock.second),
  );
  const moment = Math.floor(at.getTime() / 1000) * 1000;
  const offsetMinutes = Math.round((clockAsUtc - moment) / 60_000);

  // Design 12.7: zero is "+00:00", never "-00:00".
  const sign = offsetMinutes < 0 ? '-' : '+';
  const size = Math.abs(offsetMinutes);
  const hours = String(Math.floor(size / 60)).padStart(2, '0');
  const minutes = String(size % 60).padStart(2, '0');
  return `UTC${sign}${hours}:${minutes}`;
}

/**
 * AC-39.1, design 12.7: today's local date as "2026-11-28", the value an `<input type="date">` takes. The receipt
 * date field starts at it, and the date is sent as entered (row 85).
 */
export function todayLocalDate(now: Date = new Date(), timeZone?: string): string {
  const clock = wallClock(now, timeZone);
  return `${clock.year}-${clock.month}-${clock.day}`;
}
