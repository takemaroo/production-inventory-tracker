import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  apiRequest,
  connectSession,
  failureText,
  fieldErrors,
  noAnswerText,
  noMessageText,
  nothingChangedText,
  requestTimeoutMs,
  type ApiFailure,
  type ApiResult,
} from './http';
import { parseTypedDecimal } from './json';

// These tests never touch a network. `vi.fn` makes a "stub": a stand-in function that records every call and answers
// what the test tells it to. `vi.stubGlobal` puts it in the place of the browser's fetch for one test;
// `vi.unstubAllGlobals` puts the real one back afterwards.
const fetchStub = vi.fn<typeof fetch>();
let disconnect: (() => void) | undefined;

beforeEach(() => {
  fetchStub.mockReset();
  vi.stubGlobal('fetch', fetchStub);
});

afterEach(() => {
  disconnect?.();
  disconnect = undefined;
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

/** A response like the API's: the body is sent as text, as the API writes it. */
function answer(status: number, body = '', contentType = 'application/json'): Response {
  // 204 and some other statuses may not carry a body at all, so an empty one is passed as null.
  return new Response(body === '' ? null : body, { status, headers: { 'Content-Type': contentType } });
}

/** A ProblemDetails body (design 6) as JSON text. */
function problem(status: number, code: string, detail: string, errors?: Record<string, string[]>): Response {
  return answer(status, JSON.stringify({ title: 'x', status, detail, code, errors, traceId: '00-1' }), 'application/problem+json');
}

/** The address and options of the stub's only call; also proves there was exactly one call. */
function onlyCall(): { url: string; init: RequestInit; headers: Headers } {
  expect(fetchStub).toHaveBeenCalledTimes(1);
  const [input, init = {}] = fetchStub.mock.calls[0] ?? [];
  return { url: String(input), init, headers: new Headers(init.headers) };
}

/** The failure of a result that must have failed. */
function failureOf<T>(result: ApiResult<T>): ApiFailure {
  if (result.ok) {
    throw new Error('Expected a failure.');
  }
  return result.failure;
}

/** Connects a fake session module for one test (T-041 connects the real one). */
function signedIn(token: string | null, onNotAuthenticated = vi.fn()): ReturnType<typeof vi.fn> {
  disconnect = connectSession({ currentToken: () => token, onNotAuthenticated });
  return onNotAuthenticated;
}

describe('apiRequest: addresses and headers (architecture 16)', () => {
  // Architecture 16, A-1: the address is relative under /api/v1, built with URLSearchParams; empty values left out.
  it('sends to a relative /api/v1 address with the query under its own names', async () => {
    fetchStub.mockResolvedValue(answer(200, '{"items":[]}'));

    await apiRequest('GET', '/stock', { query: { itemType: 'RawMaterial', page: 2, empty: '', none: null, gone: undefined } });

    expect(onlyCall().url).toBe('/api/v1/stock?itemType=RawMaterial&page=2');
  });

  // Architecture 16: a typed filter value is escaped, so it can never add a parameter of its own.
  it('escapes query values', async () => {
    fetchStub.mockResolvedValue(answer(200, '{}'));

    await apiRequest('GET', '/customers', { query: { name: 'a b&page=9' } });

    expect(onlyCall().url).toBe('/api/v1/customers?name=a+b%26page%3D9');
  });

  // SEC-10, architecture 16: the token header only when a session exists.
  it('sends no Authorization header without a session', async () => {
    fetchStub.mockResolvedValue(answer(200, '{}'));

    await apiRequest('GET', '/units');

    const { headers } = onlyCall();
    expect(headers.has('Authorization')).toBe(false);
    expect(headers.get('Accept')).toBe('application/json');
  });

  // SEC-10: a session module with no token sends none, so no empty Bearer header is ever sent.
  it.each([null, ''])('sends no Authorization header when the session module has no token (%j)', async (token) => {
    signedIn(token);
    fetchStub.mockResolvedValue(answer(200, '{}'));

    await apiRequest('GET', '/units');

    expect(onlyCall().headers.has('Authorization')).toBe(false);
  });

  // SEC-10, architecture 15: the token is asked for at each request, so after sign-out (disconnect) none is sent.
  it('sends the bearer token when a session exists, and stops once disconnected', async () => {
    signedIn('token-1');
    fetchStub.mockResolvedValue(answer(200, '{}'));
    await apiRequest('GET', '/units');
    expect(onlyCall().headers.get('Authorization')).toBe('Bearer token-1');

    disconnect?.();
    fetchStub.mockClear();
    await apiRequest('GET', '/units');
    expect(onlyCall().headers.has('Authorization')).toBe(false);
  });

  // Architecture 16: Content-Type only when there is a body.
  it('marks a body as JSON, and sends neither body nor Content-Type without one', async () => {
    fetchStub.mockResolvedValue(answer(204));
    await apiRequest('POST', '/notifications/read-all');
    const withoutBody = onlyCall();
    expect(withoutBody.init.body).toBeUndefined();
    expect(withoutBody.headers.has('Content-Type')).toBe(false);

    fetchStub.mockClear();
    await apiRequest('PUT', '/units/1', { body: { name: 'kg' } });
    const withBody = onlyCall();
    expect(withBody.init.method).toBe('PUT');
    expect(withBody.headers.get('Content-Type')).toBe('application/json');
    expect(withBody.init.body).toBe('{"name":"kg"}');
  });

  // Architecture 16, decision #231: quantities in a body are written from ExactDecimal, as typed. As a JavaScript
  // number, 999999999999999.999 would be sent as 1000000000000000.
  it('writes quantities in a body from ExactDecimal, digit for digit', async () => {
    fetchStub.mockResolvedValue(answer(200, '{}'));

    await apiRequest('POST', '/production-orders/12/send', {
      body: { lines: [{ itemId: 3, quantity: parseTypedDecimal('999999999999999.999') }, { itemId: 4, quantity: parseTypedDecimal('0.10') }] },
    });

    expect(onlyCall().init.body).toBe('{"lines":[{"itemId":3,"quantity":999999999999999.999},{"itemId":4,"quantity":0.10}]}');
  });

  // F-2 is about the network: a body that cannot be written as JSON is a programming mistake, so it throws and nothing
  // is sent, rather than showing "Cannot reach the API".
  it('throws for a body that cannot be written, and sends nothing', async () => {
    await expect(apiRequest('POST', '/units', { body: { count: BigInt(1) } })).rejects.toThrow(TypeError);
    expect(fetchStub).not.toHaveBeenCalled();
  });
});

describe('apiRequest: answers', () => {
  // Architecture 16: the answer is parsed with parseApiJson, so decimals stay exact text.
  it('returns a 2xx body parsed with exact decimals', async () => {
    fetchStub.mockResolvedValue(answer(200, '{"itemId":3,"warehouse":1015.001,"openDemand":0}'));

    const result = await apiRequest('GET', '/stock/3');

    expect(result).toEqual({ ok: true, status: 200, data: { itemId: 3, warehouse: '1015.001', openDemand: 0 } });
  });

  // Architecture 16: an empty body (204, for example Mark all read) is a success with no data.
  it('returns no data for 204 No Content', async () => {
    fetchStub.mockResolvedValue(answer(204));

    expect(await apiRequest('DELETE', '/units/1')).toEqual({ ok: true, status: 204, data: undefined });
  });

  // Decision #258: a 2xx body that is not JSON carries no message, so it is shown as "no message".
  it('treats an unreadable 2xx body as an answer with no message', async () => {
    fetchStub.mockResolvedValue(answer(200, '<!doctype html>', 'text/html'));

    expect(failureOf(await apiRequest('GET', '/units'))).toEqual({ kind: 'no-message', status: 200 });
  });
});

describe('apiRequest: the failure kinds and their texts (design 12.4, 18.1)', () => {
  // F-1: `refused` keeps status, code, detail and errors exactly as sent, and the screen shows detail word for word.
  it('refused: passes detail and errors through unchanged', async () => {
    const detail =
      'Cheese: send must be between 975 and 1,015 kg (planned 1,000 kg, leftover applied 5 kg). Potato starch: send must be between 14.7 and 15.3 kg (planned 15 kg, leftover applied 0 kg).';
    const errors = {
      'lines[0].quantity': ['Cheese: send must be between 975 and 1,015 kg (planned 1,000 kg, leftover applied 5 kg).'],
      'lines[1].quantity': ['Potato starch: send must be between 14.7 and 15.3 kg (planned 15 kg, leftover applied 0 kg).'],
    };
    fetchStub.mockResolvedValue(problem(400, 'send-out-of-range', detail, errors));

    const failure = failureOf(await apiRequest('POST', '/production-orders/12/send', { body: { lines: [] } }));

    expect(failure).toEqual({ kind: 'refused', status: 400, code: 'send-out-of-range', detail, errors });
    expect(failureText(failure)).toBe(detail);
    expect(fetchStub).toHaveBeenCalledTimes(1);
  });

  // F-1 with AC-37.9 (T-038): the API escapes quotes and other characters in the raw body; the detail shown is still
  // the API's sentence exactly. The escape is built from character codes so no tool can turn it into a quote here.
  it('refused: an escaped detail is shown as the API wrote it', async () => {
    const quote = String.fromCharCode(92) + 'u0027';
    const raw = `{"status":409,"code":"conflict","detail":"A unit named ${quote}kg${quote} already exists."}`;
    fetchStub.mockResolvedValue(answer(409, raw, 'application/problem+json'));

    const failure = failureOf(await apiRequest('POST', '/units', { body: { name: 'kg' } }));

    expect(failureText(failure)).toBe("A unit named 'kg' already exists.");
    expect(failure).toMatchObject({ kind: 'refused', status: 409, code: 'conflict', errors: undefined });
  });

  // AC-36.4, architecture 15: a 403 is a refusal shown word for word; the session continues.
  it('refused: a 403 is a refusal and does not end the session', async () => {
    const onNotAuthenticated = signedIn('token-1');
    fetchStub.mockResolvedValue(problem(403, 'forbidden', 'Your roles do not allow this action. It needs the Manager role.'));

    const failure = failureOf(await apiRequest('GET', '/users'));

    expect(failure).toMatchObject({ kind: 'refused', status: 403, detail: 'Your roles do not allow this action. It needs the Manager role.' });
    expect(onNotAuthenticated).not.toHaveBeenCalled();
  });

  // Design 12.4: no ProblemDetails body gives the fixed sentence with the status.
  it.each([
    ['an empty body', answer(500)],
    ['an HTML body', answer(502, '<html>Bad gateway</html>', 'text/html')],
    ['JSON without detail', answer(404, '{"title":"Not found"}', 'application/problem+json')],
    ['an empty detail', answer(400, '{"detail":""}', 'application/problem+json')],
    ['a JSON array', answer(400, '["x"]')],
  ])('no-message: %s', async (_case, response) => {
    fetchStub.mockResolvedValue(response);

    const failure = failureOf(await apiRequest('POST', '/receipts', { body: {} }));

    expect(failure).toEqual({ kind: 'no-message', status: response.status });
    expect(failureText(failure)).toBe(`The API answered with status ${response.status} and no message. Try again.`);
    expect(fetchStub).toHaveBeenCalledTimes(1);
  });

  // F-2: a network failure gives the fixed sentence, and a changing request is never sent again by itself.
  it('no-answer: a network failure, with no second request', async () => {
    fetchStub.mockRejectedValue(new TypeError('Failed to fetch'));

    const failure = failureOf(await apiRequest('POST', '/receipts', { body: { itemId: 3 } }));

    expect(failure).toEqual({ kind: 'no-answer' });
    expect(failureText(failure)).toBe('Cannot reach the API. Check that it is running, then try again.');
    expect(fetchStub).toHaveBeenCalledTimes(1);
  });

  // The design's exact texts (design 18.1), typed out in full rather than built from the code under test.
  it('uses the design texts word for word', () => {
    expect(noAnswerText).toBe('Cannot reach the API. Check that it is running, then try again.');
    expect(nothingChangedText).toBe('Nothing was changed.');
    expect(noMessageText(413)).toBe('The API answered with status 413 and no message. Try again.');
  });
});

describe('apiRequest: the 30-second timeout (F-2)', () => {
  // F-2: a request with no answer after 30 seconds is abandoned and never sent again. Vitest's "fake timers" replace
  // setTimeout with a clock the test moves by hand, so 30 seconds pass in an instant.
  it('gives up at 30 seconds, not before, and sends nothing more', async () => {
    vi.useFakeTimers();
    // A fetch that never answers by itself; like the real one, it fails when its signal is aborted.
    fetchStub.mockImplementation(
      (_input, init) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => reject(new DOMException('The operation was aborted.', 'AbortError')));
        }),
    );

    let result: ApiResult<unknown> | undefined;
    void apiRequest('POST', '/receipts', { body: { itemId: 3 } }).then((r) => (result = r));

    await vi.advanceTimersByTimeAsync(requestTimeoutMs - 1);
    expect(result).toBeUndefined();

    await vi.advanceTimersByTimeAsync(1);
    expect(result).toEqual({ ok: false, failure: { kind: 'no-answer' } });

    // No retry, however long we wait.
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    expect(fetchStub).toHaveBeenCalledTimes(1);
    expect(requestTimeoutMs).toBe(30_000);
  });

  // The timer is stopped once the answer arrives, so it can never abort a finished request or keep a timer running.
  it('stops its timer when the answer arrives', async () => {
    vi.useFakeTimers();
    fetchStub.mockResolvedValue(answer(200, '{}'));

    await apiRequest('GET', '/units');

    expect(vi.getTimerCount()).toBe(0);
  });
});

describe('apiRequest: the end of a session (AC-34.6)', () => {
  // AC-34.6, architecture 15: a 401 not-authenticated from any request tells the session module; the screen shows nothing.
  it('401 not-authenticated calls the hook and returns session-ended', async () => {
    const onNotAuthenticated = signedIn('expired-token');
    fetchStub.mockResolvedValue(problem(401, 'not-authenticated', 'Your sign-in is missing or has expired. Sign in again.'));

    const failure = failureOf(await apiRequest('GET', '/notifications/unread-count'));

    expect(failure).toEqual({ kind: 'session-ended' });
    expect(failureText(failure)).toBeNull();
    expect(onNotAuthenticated).toHaveBeenCalledTimes(1);
    expect(fetchStub).toHaveBeenCalledTimes(1);
  });

  // AC-34.3, AC-35.3: other 401 codes are refusals shown in their form, and the session is not ended.
  it.each([
    ['invalid-credentials', 'Invalid email or password.'],
    ['account-locked', 'Account temporarily locked. Try again later.'],
  ])('401 %s does not call the hook and is a refusal', async (code, detail) => {
    const onNotAuthenticated = signedIn(null);
    fetchStub.mockResolvedValue(problem(401, code, detail));

    const failure = failureOf(await apiRequest('POST', '/auth/sign-in', { body: { email: 'a@b.c', password: 'x' } }));

    expect(failure).toEqual({ kind: 'refused', status: 401, code, detail, errors: undefined });
    expect(onNotAuthenticated).not.toHaveBeenCalled();
  });
});

describe('fieldErrors (F-1, design 12.4)', () => {
  const refused: ApiFailure = {
    kind: 'refused',
    status: 400,
    code: 'validation',
    detail: 'Quantity is required. Bad line.',
    errors: {
      receiptDate: ['Receipt date is required.'],
      'lines[0].quantity': ['Cheese: quantity is required.'],
      'lines[1].quantity': ['Potato starch: quantity is required.'],
      'lines[1]': ['Line 2 is wrong.'],
    },
  };

  // Design 12.4: on every form but one, the body position is the row on screen; the messages are never changed.
  it('keeps every key and message as sent when the positions are the rows', () => {
    expect(fieldErrors(refused)).toEqual(refused.errors);
  });

  // Design 12.4, 16.10: Request extra material sends only filled rows; its map moves each message to its row, and a
  // position with no row is left to the message area.
  it('moves line messages to their rows with the position map', () => {
    expect(fieldErrors(refused, [2])).toEqual({
      receiptDate: ['Receipt date is required.'],
      'lines[2].quantity': ['Cheese: quantity is required.'],
    });
    expect(fieldErrors(refused, [0, 5])).toEqual({
      receiptDate: ['Receipt date is required.'],
      'lines[0].quantity': ['Cheese: quantity is required.'],
      'lines[5].quantity': ['Potato starch: quantity is required.'],
      'lines[5]': ['Line 2 is wrong.'],
    });
  });

  // F-1, F-2: only a refusal has field messages; no-answer and no-message are shown in the message area alone.
  it('has no field messages for other failures or a refusal without errors', () => {
    expect(fieldErrors({ kind: 'no-answer' })).toEqual({});
    expect(fieldErrors({ kind: 'no-message', status: 500 })).toEqual({});
    expect(fieldErrors({ kind: 'refused', status: 409, code: 'x', detail: 'd', errors: undefined })).toEqual({});
  });

  // Design 6: errors map paths to lists of messages; anything else in that place is not a field message.
  it('keeps only the well-formed entries of errors from a response', async () => {
    fetchStub.mockResolvedValue(
      answer(400, '{"detail":"d","code":"validation","errors":{"name":["Name is required."],"bad":"text","odd":[1]}}'),
    );

    const failure = failureOf(await apiRequest('POST', '/units', { body: {} }));

    expect(fieldErrors(failure)).toEqual({ name: ['Name is required.'] });
  });
});
