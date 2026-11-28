// Type declarations for two newer parts of JavaScript's built-in JSON object that TypeScript 7.0's own library does not
// describe yet. Both are in the current Edge and Chrome (the browsers of PRD row 74) and in Node.js 24, which runs the
// tests. src/api/json.ts uses them to keep decimal numbers exact (docs/03-architecture.md, part v1.2, "Exact decimals").

export {};

declare global {
  /** The object JSON.rawJSON returns: JSON.stringify writes its text as it is, without quotes or rounding. */
  interface RawJSON {
    readonly rawJSON: string;
  }

  /** What JSON.parse passes as the third argument of a reviver: for a number, string or literal, its source text. */
  interface JSONReviverContext {
    readonly source?: string;
  }

  interface JSON {
    /** Wraps text that is already valid JSON (here, a number such as "995.5") so JSON.stringify writes it unchanged. */
    rawJSON(text: string): RawJSON;

    /** JSON.parse with a reviver that also receives each value's source text in `context.source`. */
    parse(
      text: string,
      reviver: (this: unknown, key: string, value: unknown, context: JSONReviverContext) => unknown,
    ): unknown;
  }
}
