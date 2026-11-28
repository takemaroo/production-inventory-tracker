import { describe, expect, it } from 'vitest';

// A guard over the front end's own source code, run with every test run. It fails any change that starts using a
// browser feature the security rules forbid, so a reviewer does not have to catch it by eye:
//   - AC-37.2 (SEC-10, SEC-27): no raw-HTML insertion and no string-to-code feature anywhere;
//   - AC-34.5 (SEC-10): nothing stored in the browser (the token and API data live in memory only), and nothing
//     written to the console, where a token or API data could be read.
// Tests (*.test.ts, *.test.tsx) are not scanned: they may name these features, as this file does.
//
// import.meta.glob is a Vite feature: it finds every matching file when the tests are built; "?raw" gives each file's
// text instead of running it.
const sources = import.meta.glob<string>(['./**/*.{ts,tsx}', '!./**/*.test.{ts,tsx}'], {
  query: '?raw',
  import: 'default',
  eager: true,
});

const forbidden: ReadonlyArray<{ pattern: RegExp; rule: string }> = [
  { pattern: /\binnerHTML\b/, rule: 'AC-37.2: raw HTML (innerHTML)' },
  { pattern: /\bouterHTML\b/, rule: 'AC-37.2: raw HTML (outerHTML)' },
  { pattern: /\binsertAdjacentHTML\b/, rule: 'AC-37.2: raw HTML (insertAdjacentHTML)' },
  { pattern: /\bdangerouslySetInnerHTML\b/, rule: 'AC-37.2: raw HTML (dangerouslySetInnerHTML)' },
  { pattern: /\bdocument\.write(ln)?\b/, rule: 'AC-37.2: raw HTML (document.write)' },
  { pattern: /\bcreateContextualFragment\b/, rule: 'AC-37.2: raw HTML (createContextualFragment)' },
  { pattern: /\bparseFromString\b/, rule: 'AC-37.2: raw HTML (DOMParser.parseFromString)' },
  { pattern: /\bsrcdoc\b/, rule: 'AC-37.2: raw HTML (srcdoc)' },
  { pattern: /\beval\s*\(/, rule: 'AC-37.5: code from text (eval)' },
  { pattern: /\bnew\s+Function\s*\(/, rule: 'AC-37.5: code from text (new Function)' },
  { pattern: /\blocalStorage\b/, rule: 'AC-34.5: browser storage (localStorage)' },
  { pattern: /\bsessionStorage\b/, rule: 'AC-34.5: browser storage (sessionStorage)' },
  { pattern: /\bindexedDB\b/, rule: 'AC-34.5: browser storage (indexedDB)' },
  { pattern: /\bdocument\.cookie\b/, rule: 'AC-34.5: browser storage (cookies)' },
  { pattern: /\bcaches\./, rule: 'AC-34.5: browser storage (cache storage)' },
  { pattern: /\bconsole\./, rule: 'AC-34.5: nothing is written to the browser console' },
  // Design 17: every style comes from the stylesheet, so the pages need no 'unsafe-inline' in style-src (AC-37.5).
  { pattern: /\bstyle=\{/, rule: 'AC-37.5: no inline style (style={...})' },
];

describe('source guard (AC-37.2, AC-34.5)', () => {
  it('finds the source files', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(0);
  });

  it.each(forbidden)('no source file uses $rule', ({ pattern }) => {
    const offenders = Object.entries(sources)
      .filter(([, text]) => pattern.test(text))
      .map(([file]) => file);

    expect(offenders).toEqual([]);
  });
});
