/**
 * The one way the front end talks to the API (docs/03-architecture.md, part v1.2, section 16; decision #236).
 *
 * Every screen sends its requests through {@link apiRequest}, so the rules below hold everywhere and live in one place:
 *
 * - The address is relative, "/api/v1" plus the path, so the same build works at any host or port (A-1, decision #244).
 * - The headers: JSON is asked for, the sign-in token is added only when a session exists, and a body is marked JSON.
 * - F-2: a request with no answer after 30 seconds is abandoned. Nothing is ever sent again by itself.
 * - The answer comes back as a result object, never as a thrown error, so a screen cannot forget a failure: TypeScript
 *   makes it look at `ok` before it can read `data`. A failure is one of four kinds, each with the design's text
 *   (design 12.4, 18.1).
 * - AC-34.6: a `401` with code `not-authenticated` tells the session module, which ends the session (architecture 15).
 */
import { parseApiJson } from './json';

/** The API's base path. Relative on purpose: the API serves the front end from its own address, so there is no CORS. */
export const apiBasePath = '/api/v1';

/** F-2: how long a request may wait for its answer, in milliseconds (30 seconds). */
export const requestTimeoutMs = 30_000;

/** F-2, design 18.1: shown when the API does not answer or the 30 seconds pass. */
export const noAnswerText = 'Cannot reach the API. Check that it is running, then try again.';

/** Design 12.4, 18.1: the bold first line above a refused change's message. Not shown for a refused read. */
export const nothingChangedText = 'Nothing was changed.';

/** Design 12.4, 18.1: shown when the API refuses without a ProblemDetails message. */
export function noMessageText(status: number): string {
  return `The API answered with status ${status} and no message. Try again.`;
}

/** The HTTP methods the API uses (it has no PATCH endpoint). */
export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE';

/** One query value. `undefined`, `null` and the empty string mean "not set" and are left out of the address. */
export type QueryValue = string | number | boolean | null | undefined;

/** Query parameters under the API's own names, for example `{ itemType: 'RawMaterial', page: 2 }`. */
export type Query = Readonly<Record<string, QueryValue>>;

/** A refusal with the API's ProblemDetails body (design 6). Every field is kept exactly as the API sent it. */
export interface RefusedFailure {
  readonly kind: 'refused';
  /** The HTTP status, for example 400, 403, 404 or 409. */
  readonly status: number;
  /** The stable code, for example "validation" or "send-out-of-range"; undefined if the body had none. */
  readonly code: string | undefined;
  /** F-1: the complete message, shown word for word. Never rewritten or rebuilt. */
  readonly detail: string;
  /** F-1: messages per field, keyed by the JSON path in the body sent ("lines[0].quantity"); undefined if none. */
  readonly errors: Readonly<Record<string, readonly string[]>> | undefined;
}

/** Status 400 or more without a ProblemDetails message. The screen shows {@link noMessageText}. */
export interface NoMessageFailure {
  readonly kind: 'no-message';
  readonly status: number;
}

/** F-2: a network failure or the 30-second timeout. The screen shows {@link noAnswerText}. */
export interface NoAnswerFailure {
  readonly kind: 'no-answer';
}

/** AC-34.6: a `401` `not-authenticated`. The screen shows nothing; the session module has been told and ends it. */
export interface SessionEndedFailure {
  readonly kind: 'session-ended';
}

/**
 * Why a request did not succeed. This is a "discriminated union": every member has a `kind` with its own fixed text,
 * so after `if (failure.kind === 'refused')` TypeScript knows `detail` exists, and a `switch` over `kind` that misses a
 * case does not compile.
 */
export type ApiFailure = RefusedFailure | NoMessageFailure | NoAnswerFailure | SessionEndedFailure;

/**
 * What {@link apiRequest} gives back. `T` is a "generic" type parameter: the caller says which shape the answer has
 * (for example `ApiResult<StockRow>`), so `data` has that type. The shape is the interface contract's
 * (docs/02-design.md section 5); it is trusted, not checked at run time.
 */
export type ApiResult<T> = { readonly ok: true; readonly status: number; readonly data: T } | { readonly ok: false; readonly failure: ApiFailure };

/** Options for {@link apiRequest}. */
export interface RequestOptions {
  /** Query parameters; empty values are left out. */
  readonly query?: Query;
  /**
   * The JSON body. Quantities must be `ExactDecimal` values from `parseTypedDecimal`, so the typed digits are sent as
   * they are (architecture 16, "Exact decimals"); never put a JavaScript number made from a quantity here.
   */
  readonly body?: unknown;
}

/**
 * What the session module (src/session/, T-041) gives this file. It is a plain object of two functions, registered
 * once with {@link connectSession}; this file never holds the token itself (SEC-10, AC-34.5).
 */
export interface SessionConnection {
  /** The token of the current session, or null (or "") when no one is signed in. Asked for at each request. */
  readonly currentToken: () => string | null;
  /**
   * AC-34.6: called when any request is answered `401` `not-authenticated`. It may be called more than once if several
   * requests were running, so ending the session must be safe to repeat.
   */
  readonly onNotAuthenticated: () => void;
}

// The connected session module, or null before T-041 connects it (and in tests that do not need one).
let session: SessionConnection | null = null;

/**
 * Lets the session module (T-041) supply the token and be told of a `401` `not-authenticated` (AC-34.6). This is the
 * "hook" of T-039 in the plain sense of a callback; it is not a React hook. Returns a function that disconnects it again.
 */
export function connectSession(connection: SessionConnection): () => void {
  session = connection;
  return () => {
    if (session === connection) {
      session = null;
    }
  };
}

/**
 * Sends one request to the API and returns its answer or the reason it failed. Never retries. It does not throw for
 * anything the API or the network does; only a body that cannot be written as JSON (a programming mistake) throws.
 *
 * `async` marks a function that returns a "promise": a value that arrives later. Inside it, `await` pauses this
 * function (not the page) until a promise settles, so the code reads top to bottom although the network is slow.
 */
export async function apiRequest<T>(method: HttpMethod, path: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
  const hasBody = options.body !== undefined;
  const headers = new Headers({ Accept: 'application/json' });
  // SEC-10: the token is asked for at each request and only sent when a session exists.
  const token = session?.currentToken() ?? null;
  if (token !== null && token !== '') {
    headers.set('Authorization', `Bearer ${token}`);
  }
  if (hasBody) {
    headers.set('Content-Type', 'application/json');
  }
  // Written before the request is started: a body that cannot be written (a programming mistake, never the user's
  // input) then throws here, and is not reported as "Cannot reach the API" when nothing was sent.
  const bodyText = hasBody ? JSON.stringify(options.body) : undefined;

  // F-2: an AbortController is the browser's way to cancel a fetch: calling abort() makes the fetch (and the reading
  // of its body) fail at once. A timer calls it after 30 seconds. This is AbortSignal.timeout(30000) written out, so
  // that tests can move the timer with fake timers (decision #258).
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), requestTimeoutMs);

  let response: Response;
  let text: string;
  try {
    // fetch is the browser's built-in request function. It resolves as soon as the status and headers arrive, even
    // for 4xx and 5xx answers; it rejects only when no answer came (network down, API stopped, aborted).
    // `globalThis.fetch` is looked up at each call, so tests can put a stub in its place.
    response = await globalThis.fetch(buildAddress(path, options.query), {
      method,
      headers,
      body: bodyText,
      signal: controller.signal,
    });
    text = await response.text();
  } catch {
    // F-2: no answer, or the 30 seconds passed. Never sent again by itself: a change might already have been made.
    return { ok: false, failure: { kind: 'no-answer' } };
  } finally {
    clearTimeout(timer);
  }

  if (response.ok) {
    // A 2xx answer. 204 No Content and other empty bodies give `undefined`.
    const parsed = parseBody(text);
    if (parsed.readable) {
      return { ok: true, status: response.status, data: parsed.value as T };
    }
    // The API always answers JSON under /api; an unreadable 2xx body has no message to show (decision #258).
    return { ok: false, failure: { kind: 'no-message', status: response.status } };
  }

  const failure = refusalFrom(response.status, text);
  // AC-34.6, architecture 15: the rule goes by the code, not by the endpoint. Other 401 codes (invalid-credentials,
  // account-locked) are refusals shown in their form (AC-34.3, AC-35.3).
  if (failure.kind === 'refused' && failure.status === 401 && failure.code === 'not-authenticated') {
    session?.onNotAuthenticated();
    return { ok: false, failure: { kind: 'session-ended' } };
  }
  return { ok: false, failure };
}

/**
 * The text a message area shows for a failure (design 12.4, 18.1): the API's `detail` word for word for a refusal (F-1),
 * the fixed sentences for the other kinds, and null when the session ended (the sign-in screen opens instead).
 * "Nothing was changed." ({@link nothingChangedText}) is a separate line the screen adds above a refused change.
 */
export function failureText(failure: ApiFailure): string | null {
  switch (failure.kind) {
    case 'refused':
      return failure.detail;
    case 'no-message':
      return noMessageText(failure.status);
    case 'no-answer':
      return noAnswerText;
    case 'session-ended':
      return null;
  }
}

/**
 * F-1, design 12.4: the refusal's field messages, keyed by the form's own field names. Keys outside `lines` are kept as
 * the API wrote them (`quantity`, `customerId`). A key `lines[i]...` names position `i` in the body that was sent; it is
 * moved to the form's row `rowForPosition[i]`. Without a map, positions are rows, as on every form but one.
 *
 * Request extra material (design 16.10) sends only its filled rows, so it passes the map from body position to row.
 * A key whose position is not in the map belongs to no field on the form and is left out; the message area already
 * shows it, because `detail` holds every message. The messages themselves are never changed.
 */
export function fieldErrors(
  failure: ApiFailure,
  rowForPosition?: readonly number[],
): Readonly<Record<string, readonly string[]>> {
  if (failure.kind !== 'refused' || failure.errors === undefined) {
    return {};
  }

  const result = emptyRecord();
  for (const [key, messages] of Object.entries(failure.errors)) {
    const line = linePositionPattern.exec(key);
    if (line === null || rowForPosition === undefined) {
      result[key] = messages;
      continue;
    }
    const row = rowForPosition[Number(line[1])];
    if (row !== undefined) {
      result[`lines[${row}]${line[2] ?? ''}`] = messages;
    }
  }
  return result;
}

// "lines[12].quantity" -> position "12" and the rest ".quantity"; "lines[3]" alone -> "3" and "".
const linePositionPattern = /^lines\[(\d+)\](.*)$/;

/** Builds "/api/v1/stock?itemType=RawMaterial&page=1" from the path and the query, leaving out empty values. */
function buildAddress(path: string, query: Query | undefined): string {
  // URLSearchParams writes each name and value with the escaping an address needs ("a b" becomes "a+b", "&" becomes
  // "%26"), so a typed filter value can never add a parameter of its own.
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(query ?? {})) {
    if (value !== undefined && value !== null && value !== '') {
      params.append(name, String(value));
    }
  }
  const search = params.toString();
  return `${apiBasePath}${path}${search === '' ? '' : `?${search}`}`;
}

/** Parses a body with exact decimals (architecture 16). An empty body is readable and gives `undefined`. */
function parseBody(text: string): { readable: true; value: unknown } | { readable: false } {
  if (text === '') {
    return { readable: true, value: undefined };
  }
  try {
    return { readable: true, value: parseApiJson(text) };
  } catch {
    return { readable: false };
  }
}

/**
 * Status 400 or more: a refusal when the body is a ProblemDetails object with a message (design 6), otherwise
 * "no message". Whether the body counts is decided by its content, not its Content-Type header, so the rule does not
 * depend on how a header is spelt; a body with an empty `detail` has nothing to show and counts as no message.
 */
function refusalFrom(status: number, text: string): RefusedFailure | NoMessageFailure {
  const parsed = parseBody(text);
  if (!parsed.readable || !isObject(parsed.value)) {
    return { kind: 'no-message', status };
  }
  const { detail, code, errors } = parsed.value;
  if (typeof detail !== 'string' || detail === '') {
    return { kind: 'no-message', status };
  }
  return {
    kind: 'refused',
    status,
    code: typeof code === 'string' ? code : undefined,
    detail,
    errors: fieldMessages(errors),
  };
}

/** Design 6: `errors` maps each JSON path to a list of messages. Anything of another shape is not a field message. */
function fieldMessages(errors: unknown): Readonly<Record<string, readonly string[]>> | undefined {
  if (!isObject(errors)) {
    return undefined;
  }
  const result = emptyRecord();
  for (const [key, messages] of Object.entries(errors)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[key] = messages;
    }
  }
  return result;
}

/**
 * An object with no prototype, to hold keys that came from the API (T-039 review). A plain `{}` inherits from
 * `Object.prototype`, where `__proto__` is a special setter: `result['__proto__'] = list` would not add a key named
 * "__proto__" but replace the object's prototype, so the message would be lost and the object would gain the list's
 * methods. With no prototype there is no such setter, every key the server sends is stored as an ordinary key, and
 * looking up a field name such as "constructor" finds nothing inherited.
 */
function emptyRecord(): Record<string, readonly string[]> {
  return Object.create(null) as Record<string, readonly string[]>;
}

/** True for a plain JSON object (not null, not an array). */
function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
