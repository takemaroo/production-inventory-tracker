import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiRequest } from '../api/http';
import {
  endSession,
  getSessionState,
  signIn,
  signOut,
  startSession,
  subscribeToSession,
  type PageLoader,
} from './session';

// These tests use the real request function (src/api/http.ts) with a stubbed fetch, so they prove the session module
// and http.ts work together exactly as the app connects them. A stand-in page loader records each address the session
// would load, because the simulated page (jsdom) cannot load a new document.
const fetchStub = vi.fn<typeof fetch>();
const loadPage = vi.fn<PageLoader>();
let stop: (() => void) | undefined;
// Listeners a test added; removed after each test even when an assertion failed, so none leaks into the next test.
const unsubscribers: (() => void)[] = [];

beforeEach(() => {
  fetchStub.mockReset();
  loadPage.mockReset();
  vi.stubGlobal('fetch', fetchStub);
  window.history.replaceState(null, '', '/');
  stop = startSession({ loadPage });
});

afterEach(() => {
  unsubscribers.splice(0).forEach((unsubscribe) => unsubscribe());
  stop?.();
  stop = undefined;
  vi.unstubAllGlobals();
});

/** A JSON answer as the API writes it. */
function answer(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** The design 5.1 sign-in response. */
function signInAnswer(roles: string[] = ['Manager'], token = 'token-123'): Response {
  return answer(200, {
    accessToken: token,
    tokenType: 'Bearer',
    expiresAt: '2026-11-28T22:05:00Z',
    email: 'maria.lopez@example.com',
    roles,
  });
}

/** Design 7.1: the 401 that ends a session. */
function notAuthenticated(): Response {
  return answer(401, {
    status: 401,
    code: 'not-authenticated',
    detail: 'Your sign-in is missing or has expired. Sign in again.',
  });
}

/** The Authorization header of the stub's call number `index`, or null. */
function authorizationOf(index: number): string | null {
  const init = fetchStub.mock.calls[index]?.[1] ?? {};
  return new Headers(init.headers).get('Authorization');
}

async function signInAsManager(): Promise<void> {
  fetchStub.mockResolvedValueOnce(signInAnswer());
  const result = await signIn('maria.lopez@example.com', 'Sample-pass1');
  expect(result.ok).toBe(true);
}

describe('signIn (US-34, design 16.1)', () => {
  // AC-34.2, design 5.1: POST /auth/sign-in with the email and password, and no token (there is no session yet).
  it('sends the email and password to /api/v1/auth/sign-in without a token', async () => {
    await signInAsManager();

    const [input, init] = fetchStub.mock.calls[0] ?? [];
    expect(String(input)).toBe('/api/v1/auth/sign-in');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe('{"email":"maria.lopez@example.com","password":"Sample-pass1"}');
    expect(authorizationOf(0)).toBeNull();
  });

  // AC-34.2: the session holds the email and roles in the API's order; the token is not part of what screens see.
  it('starts the session with the email and roles, never exposing the token', async () => {
    fetchStub.mockResolvedValueOnce(signInAnswer(['Warehouse', 'Production']));

    await signIn('jorge.ibarra@example.com', 'Sample-pass1');

    const state = getSessionState();
    expect(state).toEqual({ kind: 'signed-in', user: { email: 'maria.lopez@example.com', roles: ['Warehouse', 'Production'] } });
    expect(JSON.stringify(state)).not.toContain('token-123');
  });

  // SEC-10: from then on every request carries the token (http.ts asks the session module for it).
  it('makes later requests carry the token', async () => {
    await signInAsManager();
    fetchStub.mockResolvedValueOnce(answer(200, {}));

    await apiRequest('GET', '/stock');

    expect(authorizationOf(1)).toBe('Bearer token-123');
  });

  // AC-34.3: a refused sign-in is handed back as the API sent it, and starts no session.
  it('hands back a refused sign-in and starts no session', async () => {
    fetchStub.mockResolvedValueOnce(answer(401, { code: 'invalid-credentials', detail: 'Invalid email or password.' }));

    const result = await signIn('maria.lopez@example.com', 'wrong');

    expect(result).toEqual({
      ok: false,
      failure: { kind: 'refused', status: 401, code: 'invalid-credentials', detail: 'Invalid email or password.', errors: undefined },
    });
    expect(getSessionState()).toEqual({ kind: 'signed-out' });
    expect(loadPage).not.toHaveBeenCalled();
  });

  // Decision #258 d: a 200 without a usable token has no message, so it is "no message" with its status.
  it('treats a 200 without a token as no message', async () => {
    fetchStub.mockResolvedValueOnce(answer(200, { email: 'maria.lopez@example.com', roles: ['Manager'] }));

    const result = await signIn('maria.lopez@example.com', 'Sample-pass1');

    expect(result).toEqual({ ok: false, failure: { kind: 'no-message', status: 200 } });
    expect(getSessionState()).toEqual({ kind: 'signed-out' });
  });

  // T-041 review, SEC-10: a token that an Authorization header cannot carry (RFC 6750 characters only) is never kept,
  // so no later request can throw while building its header, or show the token in an error. The control character
  // and the space are built from their code points.
  it.each([
    ['a line break', `tok-SECRET${String.fromCodePoint(0x0a)}X`],
    ['a space', `tok${String.fromCodePoint(0x20)}SECRET`],
    ['an empty token', ''],
    ['"=" before the end', 'tok=SECRET'],
  ])('refuses a token with %s as no message, and later requests still answer', async (_case, token) => {
    fetchStub.mockResolvedValueOnce(signInAnswer(['Manager'], token));

    const result = await signIn('maria.lopez@example.com', 'Sample-pass1');

    expect(result).toEqual({ ok: false, failure: { kind: 'no-message', status: 200 } });
    expect(getSessionState()).toEqual({ kind: 'signed-out' });
    fetchStub.mockResolvedValueOnce(answer(200, {}));
    await expect(apiRequest('GET', '/stock')).resolves.toMatchObject({ ok: true });
    expect(authorizationOf(1)).toBeNull();
  });

  // RFC 6750: every character a bearer token may hold is accepted, padding included (a JWT has "-", "_" and ".").
  it('accepts a token made of the RFC 6750 characters', async () => {
    fetchStub.mockResolvedValueOnce(signInAnswer(['Manager'], 'aZ09-._~+/abc=='));

    await signIn('maria.lopez@example.com', 'Sample-pass1');
    fetchStub.mockResolvedValueOnce(answer(200, {}));
    await apiRequest('GET', '/stock');

    expect(authorizationOf(1)).toBe('Bearer aZ09-._~+/abc==');
  });

  // AC-34.2: a role this front end does not know is left out, so it can open nothing.
  it('keeps only the four known roles, in order', async () => {
    fetchStub.mockResolvedValueOnce(signInAnswer(['Seller', 'Auditor', 'Manager']));

    await signIn('maria.lopez@example.com', 'Sample-pass1');

    expect(getSessionState()).toMatchObject({ user: { roles: ['Seller', 'Manager'] } });
  });
});

describe('every end of a session (architecture 15)', () => {
  // AC-34.4: sign-out drops the token, blanks the page, then loads /sign-in; in that order.
  it('signs out: token dropped and page blanked before /sign-in loads', async () => {
    await signInAsManager();
    const seenWhenLoading: string[] = [];
    loadPage.mockImplementation(() => {
      seenWhenLoading.push(getSessionState().kind);
    });

    signOut();

    expect(loadPage).toHaveBeenCalledExactlyOnceWith('/sign-in');
    expect(seenWhenLoading).toEqual(['ended']);
    fetchStub.mockResolvedValueOnce(answer(200, {}));
    await apiRequest('GET', '/stock');
    expect(authorizationOf(1)).toBeNull();
  });

  // AC-34.4: the real page loader replaces the history entry (location.replace), so Back cannot return to the screen.
  it('loads the next document with location.replace by default', async () => {
    stop?.();
    stop = startSession();
    const replace = vi.fn();
    vi.stubGlobal('location', { replace, pathname: '/stock', search: '' });
    await signInAsManager();

    signOut();

    expect(replace).toHaveBeenCalledExactlyOnceWith('/sign-in');
  });

  // Every end tells the screens (SessionContext blanks the page on `ended`), before the next document is asked for.
  it('tells the listeners before loading the next document', async () => {
    await signInAsManager();
    const order: string[] = [];
    unsubscribers.push(subscribeToSession(() => order.push(`told: ${getSessionState().kind}`)));
    loadPage.mockImplementation((address) => {
      order.push(`load: ${address}`);
    });

    endSession('idle');

    expect(order).toEqual(['told: ended', 'load: /sign-in?ended=idle']);
  });

  // AC-34.7 (T-046) and AC-35.1 (T-045) end the session through the same function, with their own addresses.
  it('ends with password-changed', async () => {
    await signInAsManager();

    endSession('password-changed');

    expect(loadPage).toHaveBeenCalledExactlyOnceWith('/sign-in?ended=password-changed');
  });
});

describe('401 not-authenticated from any request (AC-34.6, T-039 review hand-over)', () => {
  // AC-34.6: the session ends at /sign-in?ended=expired&return=<current path and query>.
  it('ends the session and comes back to the current screen', async () => {
    window.history.replaceState(null, '', '/production-orders?status=Created&page=2');
    await signInAsManager();
    fetchStub.mockResolvedValueOnce(notAuthenticated());

    const result = await apiRequest('GET', '/production-orders');

    expect(result).toEqual({ ok: false, failure: { kind: 'session-ended' } });
    expect(loadPage).toHaveBeenCalledExactlyOnceWith(
      '/sign-in?ended=expired&return=/production-orders%3Fstatus%3DCreated%26page%3D2',
    );
    expect(getSessionState()).toEqual({ kind: 'ended' });
  });

  // Hand-over 2: several running requests can each get the 401; the session ends once, and nothing throws.
  it('is safe when several running requests each get the 401', async () => {
    window.history.replaceState(null, '', '/stock');
    await signInAsManager();
    fetchStub.mockImplementation(() => Promise.resolve(notAuthenticated()));

    const results = await Promise.all([apiRequest('GET', '/stock'), apiRequest('GET', '/units'), apiRequest('GET', '/items')]);

    expect(results.map((result) => (result.ok ? 'ok' : result.failure.kind))).toEqual([
      'session-ended',
      'session-ended',
      'session-ended',
    ]);
    expect(loadPage).toHaveBeenCalledExactlyOnceWith('/sign-in?ended=expired&return=/stock');
  });

  // Hand-over 2: the handler never throws, so no exception can escape apiRequest, even when loading the page fails.
  it('never throws, even when loading the page fails', async () => {
    await signInAsManager();
    loadPage.mockImplementation(() => {
      throw new Error('navigation refused');
    });
    fetchStub.mockResolvedValueOnce(notAuthenticated());

    await expect(apiRequest('GET', '/stock')).resolves.toEqual({ ok: false, failure: { kind: 'session-ended' } });
    // SEC-10: the token is dropped all the same.
    fetchStub.mockResolvedValueOnce(answer(200, {}));
    await apiRequest('GET', '/stock');
    expect(authorizationOf(2)).toBeNull();
  });

  // Hand-over 2: a listener that fails while the page blanks does not stop the end, and nothing throws.
  it('never throws, even when a listener fails', async () => {
    await signInAsManager();
    unsubscribers.push(
      subscribeToSession(() => {
        throw new Error('draw failed');
      }),
    );
    fetchStub.mockResolvedValueOnce(notAuthenticated());

    await expect(apiRequest('GET', '/stock')).resolves.toEqual({ ok: false, failure: { kind: 'session-ended' } });
    expect(loadPage).toHaveBeenCalledOnce();
  });

  // T-041 review: a failing listener does not keep a later one (in the app, React's) from being told, so the page
  // still blanks before the next document is asked for.
  it('tells every listener even when an earlier one fails', async () => {
    await signInAsManager();
    const told: string[] = [];
    unsubscribers.push(
      subscribeToSession(() => {
        throw new Error('draw failed');
      }),
    );
    unsubscribers.push(subscribeToSession(() => told.push(getSessionState().kind)));
    loadPage.mockImplementation(() => {
      told.push('load');
    });

    signOut();

    expect(told).toEqual(['ended', 'load']);
  });

  // Architecture 15, AC-34.3, AC-35.3 (T-041 review): other 401 codes are refusals shown in their form. A signed-in
  // session survives them: the state stays signed-in, nothing is loaded, and the next request still carries the token.
  it('keeps the session through 401 invalid-credentials and account-locked', async () => {
    await signInAsManager();
    fetchStub.mockResolvedValueOnce(answer(401, { code: 'invalid-credentials', detail: 'Invalid email or password.' }));
    fetchStub.mockResolvedValueOnce(answer(401, { code: 'account-locked', detail: 'Account temporarily locked. Try again later.' }));

    const first = await apiRequest('POST', '/auth/change-password', { body: {} });
    const second = await apiRequest('POST', '/auth/change-password', { body: {} });

    expect([first, second].map((result) => (result.ok ? 'ok' : result.failure.kind))).toEqual(['refused', 'refused']);
    expect(getSessionState()).toEqual({ kind: 'signed-in', user: { email: 'maria.lopez@example.com', roles: ['Manager'] } });
    expect(loadPage).not.toHaveBeenCalled();
    fetchStub.mockResolvedValueOnce(answer(200, {}));
    await apiRequest('GET', '/stock');
    expect(authorizationOf(3)).toBe('Bearer token-123');
  });

  // With no session there is nothing to end (only the sign-in screen sends without one); nothing is loaded.
  it('does nothing when no one is signed in', async () => {
    fetchStub.mockResolvedValueOnce(notAuthenticated());

    await apiRequest('GET', '/stock');

    expect(loadPage).not.toHaveBeenCalled();
    expect(getSessionState()).toEqual({ kind: 'signed-out' });
  });
});

describe('back-forward cache guard (AC-34.4, risk 23.2)', () => {
  // T-041 criterion: a restored page (pageshow with persisted true) is replaced by /sign-in, and its session dropped.
  it('replaces a page restored from the back-forward cache', async () => {
    await signInAsManager();

    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));

    expect(loadPage).toHaveBeenCalledExactlyOnceWith('/sign-in');
    expect(getSessionState()).toEqual({ kind: 'ended' });
  });

  // Risk 23.2: the copy the cache keeps after a sign-out is the blank one; restoring it still loads /sign-in.
  it('replaces a restored page that had already been left', async () => {
    await signInAsManager();
    signOut();
    loadPage.mockClear();

    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));

    expect(loadPage).toHaveBeenCalledExactlyOnceWith('/sign-in');
  });

  // A normal load also fires pageshow (persisted false); that must leave the page alone.
  it('leaves a normally loaded page alone', async () => {
    await signInAsManager();

    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: false }));

    expect(loadPage).not.toHaveBeenCalled();
    expect(getSessionState().kind).toBe('signed-in');
  });
});

describe('startSession (T-039 review hand-over 1)', () => {
  // Its stop function disconnects from http.ts and removes the guard, so nothing outlives it.
  it('stops cleanly: no token, no 401 handling, no guard', async () => {
    await signInAsManager();
    stop?.();
    stop = undefined;
    fetchStub.mockResolvedValueOnce(notAuthenticated());

    await apiRequest('GET', '/stock');
    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));

    expect(authorizationOf(1)).toBeNull();
    expect(loadPage).not.toHaveBeenCalled();
    expect(getSessionState()).toEqual({ kind: 'signed-out' });
  });

  // Starting again replaces the earlier start, so the guard and the connection are never doubled.
  it('can be started again without doubling its listeners', async () => {
    stop = startSession({ loadPage });
    await signInAsManager();

    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));

    expect(loadPage).toHaveBeenCalledOnce();
  });
});
