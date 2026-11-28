import { describe, expect, it } from 'vitest';
// "?raw" on an import gives the file's text. A glob never includes the file that calls it, so this file reads itself
// this way, for the direction-character check at the end.
import thisFile from './guards.test.ts?raw';

// A guard over the front end's own source code, run with every test run. It fails any change that starts using a
// browser feature the security rules forbid, so a reviewer does not have to catch it by eye:
//   - AC-37.2 (SEC-10, SEC-27): no raw-HTML insertion and no string-to-code feature anywhere;
//   - AC-34.5 (SEC-10): nothing stored in the browser (the token and API data live in memory only), and nothing
//     written to the console, where a token or API data could be read.
// Decision #241: this guard is used instead of ESLint, so each rule below must really work (see the self-test).
// Tests (*.test.ts, *.test.tsx) are not scanned for these features: they may name them, as this file does.
//
// import.meta.glob is a Vite feature: it finds every matching file when the tests are built; "?raw" gives each file's
// text instead of running it.
const sources = import.meta.glob<string>(['./**/*.{ts,tsx}', '!./**/*.test.{ts,tsx}'], {
  query: '?raw',
  import: 'default',
  eager: true,
});

// Every front-end file we write, for the direction-character check: the code, styles and pages under src (tests
// included, this one through its own import), the end-to-end tests in ../e2e (from T-044), and the files at the
// project's top, index.html and the tool settings (vite.config.ts, and playwright.config.ts from T-044). Paths outside
// src are written from this file's folder, so "../index.html" is src/InventoryTracker.Web/index.html.
// Not read: package.json and package-lock.json, which npm writes, and node_modules, which is other people's code.
// One limit (checked on 2026-11-28 with a temporary config): Vitest gives every CSS file as "" unless its own setting
// `test: { css: { include: [/\.css/] } }` (in vite.config.ts) covers it, even with "?raw". There is no CSS under src
// yet; the "every file read has text" check below fails as soon as one appears without that setting, so the gap
// cannot pass in silence.
const everyFile: Readonly<Record<string, string>> = {
  ...import.meta.glob<string>(['./**/*.{ts,tsx,css,html}', '../e2e/**/*.ts', '../*.{ts,html}'], {
    query: '?raw',
    import: 'default',
    eager: true,
  }),
  './guards.test.ts': thisFile,
};

/**
 * One forbidden feature: the pattern that finds it, the rule it breaks, and a short sample of code it must find.
 * The sample is what the self-test below checks, so a mistyped pattern cannot silently protect nothing.
 */
interface ForbiddenUse {
  readonly pattern: RegExp;
  readonly rule: string;
  readonly sample: string;
}

const forbidden: readonly ForbiddenUse[] = [
  { pattern: /\binnerHTML\b/, rule: 'AC-37.2: raw HTML (innerHTML)', sample: 'element.innerHTML = text;' },
  { pattern: /\bouterHTML\b/, rule: 'AC-37.2: raw HTML (outerHTML)', sample: 'element.outerHTML = text;' },
  {
    pattern: /\binsertAdjacentHTML\b/,
    rule: 'AC-37.2: raw HTML (insertAdjacentHTML)',
    sample: "element.insertAdjacentHTML('beforeend', text);",
  },
  {
    pattern: /\bdangerouslySetInnerHTML\b/,
    rule: 'AC-37.2: raw HTML (dangerouslySetInnerHTML)',
    sample: '<div dangerouslySetInnerHTML={{ __html: text }} />',
  },
  { pattern: /\bdocument\.write(ln)?\b/, rule: 'AC-37.2: raw HTML (document.write)', sample: 'document.writeln(text);' },
  {
    pattern: /\bcreateContextualFragment\b/,
    rule: 'AC-37.2: raw HTML (createContextualFragment)',
    sample: 'range.createContextualFragment(text);',
  },
  {
    pattern: /\bparseFromString\b/,
    rule: 'AC-37.2: raw HTML (DOMParser.parseFromString)',
    sample: "new DOMParser().parseFromString(text, 'text/html');",
  },
  // The newer HTML-parsing doors (phase 3 review): Element.setHTMLUnsafe, ShadowRoot.setHTMLUnsafe and
  // Document.parseHTMLUnsafe parse a string as HTML, scripts and event attributes included.
  { pattern: /\bsetHTMLUnsafe\b/, rule: 'AC-37.2: raw HTML (setHTMLUnsafe)', sample: 'element.setHTMLUnsafe(text);' },
  { pattern: /\bparseHTMLUnsafe\b/, rule: 'AC-37.2: raw HTML (parseHTMLUnsafe)', sample: 'Document.parseHTMLUnsafe(text);' },
  { pattern: /\bsrcdoc\b/, rule: 'AC-37.2: raw HTML (srcdoc)', sample: '<iframe srcdoc={text} />' },
  { pattern: /\beval\s*\(/, rule: 'AC-37.5: code from text (eval)', sample: 'eval(text);' },
  { pattern: /\bnew\s+Function\s*\(/, rule: 'AC-37.5: code from text (new Function)', sample: "new Function('return 1');" },
  // Called without `new`, Function builds code from text just the same (phase 3 review). The word boundary before
  // "Function" means names that only end in it, such as `isFunction(`, do not count; `globalThis.Function(` does.
  { pattern: /\bFunction\s*\(/, rule: 'AC-37.5: code from text (Function called without new)', sample: "Function('return 1')();" },
  // A timer given a string runs it as code (phase 3 review). Only a string written in place can be seen in the
  // source, so this finds a quote or backtick as the first argument; a timer given a function, as in src/api/http.ts,
  // is allowed. A string held in a variable cannot be seen by any text scan; Trusted Types (architecture 14) blocks
  // it in the browser.
  {
    pattern: /\bset(Timeout|Interval)\s*\(\s*['"`]/,
    rule: 'AC-37.5: code from text (setTimeout or setInterval given a string)',
    sample: "setInterval('tick()', 1000);",
  },
  { pattern: /\blocalStorage\b/, rule: 'AC-34.5: browser storage (localStorage)', sample: "localStorage.setItem('token', token);" },
  {
    pattern: /\bsessionStorage\b/,
    rule: 'AC-34.5: browser storage (sessionStorage)',
    sample: "sessionStorage.setItem('token', token);",
  },
  { pattern: /\bindexedDB\b/, rule: 'AC-34.5: browser storage (indexedDB)', sample: "indexedDB.open('db');" },
  { pattern: /\bdocument\.cookie\b/, rule: 'AC-34.5: browser storage (cookies)', sample: 'document.cookie = text;' },
  { pattern: /\bcaches\./, rule: 'AC-34.5: browser storage (cache storage)', sample: "caches.open('api');" },
  { pattern: /\bconsole\./, rule: 'AC-34.5: nothing is written to the browser console', sample: 'console.log(token);' },
  // Design 17: every style comes from the stylesheet, so the pages need no 'unsafe-inline' in style-src (AC-37.5).
  { pattern: /\bstyle=\{/, rule: 'AC-37.5: no inline style (style={...})', sample: "<p style={{ color: 'red' }} />" },
];

// Code that is allowed and must never trip the guard: each is how the front end really uses a nearby feature.
const allowed: readonly string[] = [
  // src/api/http.ts: the F-2 timeout, a timer given a function.
  'const timer = setTimeout(() => controller.abort(), requestTimeoutMs);',
  'setInterval(checkIdle, 1000);',
  'window.setTimeout(function () { return 1; }, 5);',
  "if (typeof value === 'function' || isFunction(value)) {}",
  'export function formatLocalTime(iso: string): string {}',
  'const styles = sheet.cssRules;',
];

describe('source guard (AC-37.2, AC-34.5)', () => {
  it('finds the source files', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(0);
    // The scan must include the shared files that matter most; a wrong glob that matched nothing useful would pass.
    expect(Object.keys(sources)).toEqual(expect.arrayContaining(['./api/http.ts', './components/SafeText.tsx', './session/session.ts']));
  });

  it.each(forbidden)('no source file uses $rule', ({ pattern }) => {
    const offenders = Object.entries(sources)
      .filter(([, text]) => pattern.test(text))
      .map(([file]) => file);

    expect(offenders).toEqual([]);
  });

  // Decision #241, phase 3 review: the guard replaces ESLint, so every pattern is proved to find its own sample.
  // A mistyped pattern (a wrong escape, a misspelt name) fails here instead of silently protecting nothing.
  it.each(forbidden)('the pattern for $rule finds its sample', ({ pattern, sample }) => {
    expect(pattern.test(sample)).toBe(true);
  });

  // The other half of the self-test: the patterns do not trip on code the front end is meant to use.
  it.each(allowed)('no pattern trips on the allowed code %j', (code) => {
    const tripped = forbidden.filter(({ pattern }) => pattern.test(code)).map(({ rule }) => rule);

    expect(tripped).toEqual([]);
  });
});

// AC-37.3 applied to the source itself ("Trojan Source"). A direction character typed into code or a comment can make
// the code look different from what runs, and nobody reviewing it would see it. Every such character in the front end
// is built from its number (String.fromCodePoint), so no file, test files included, may hold one raw. The list is the
// AC-37.3 list, written as numbers here so this file holds none either.
// It is copied from src/format/direction.ts on purpose: the guard must not depend on the code it guards, so a mistake
// there cannot also switch this check off.
const directionCodePoints = [0x061c, 0x200e, 0x200f, 0x202a, 0x202b, 0x202c, 0x202d, 0x202e, 0x2066, 0x2067, 0x2068, 0x2069];

/** The direction characters a text holds raw, named "U+202E". */
function rawDirectionCharacters(text: string): string[] {
  return directionCodePoints
    .filter((codePoint) => text.includes(String.fromCodePoint(codePoint)))
    .map((codePoint) => `U+${codePoint.toString(16).toUpperCase().padStart(4, '0')}`);
}

describe('source guard: no raw direction characters (AC-37.3)', () => {
  it('finds every file, tests included', () => {
    expect(Object.keys(everyFile)).toEqual(
      expect.arrayContaining([
        './guards.test.ts',
        './components/SafeText.test.tsx',
        './format/direction.ts',
        '../index.html',
        '../vite.config.ts',
      ]),
    );
    // Each must have been read, not only named: an empty self-read or page would pass the check below with nothing.
    expect(thisFile).toContain('source guard: no raw direction characters');
    expect(everyFile['../index.html']).toContain('id="root"');
    expect(everyFile['../vite.config.ts']).toContain('defineConfig');
  });

  // A file the scan names but cannot read (Vitest blanks CSS, see above) would pass the check with nothing in it.
  it('every file read has text', () => {
    const emptyFiles = Object.entries(everyFile)
      .filter(([, text]) => text.trim() === '')
      .map(([file]) => file);

    expect(emptyFiles).toEqual([]);
  });

  it('no front-end file holds a raw direction character', () => {
    const offenders = Object.entries(everyFile).flatMap(([file, text]) =>
      rawDirectionCharacters(text).map((name) => `${file}: ${name}`),
    );

    expect(offenders).toEqual([]);
  });

  // The self-test for this check: a sample holding two of them, built from their numbers, is caught for both.
  it('finds raw direction characters in a sample', () => {
    const sample = `const name = 'Milk${String.fromCodePoint(0x202e)}gnp.exe${String.fromCodePoint(0x2066)}';`;

    expect(rawDirectionCharacters(sample)).toEqual(['U+202E', 'U+2066']);
    expect(rawDirectionCharacters('const name = "Milk";')).toEqual([]);
  });
});
