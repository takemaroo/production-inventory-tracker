/**
 * The session: who is signed in, the token that proves it, and every way a session ends (US-34; architecture 15;
 * design 12.3; decision #236).
 *
 * **Why the token lives only in this file's memory (SEC-10, AC-34.5).** The sign-in response (token, `expiresAt`,
 * email and roles) is kept in one module-level variable below and nowhere else. A module-level variable lives as long
 * as the page's JavaScript does: a reload, a second tab or a browser restart starts with it empty, so the sign-in
 * screen opens (Q106). Nothing is written to the browser's storage, cookies or console (the source guard,
 * src/guards.test.ts, fails the tests if anything is). Storage would outlive the tab and could be read by anyone who
 * uses the shared computer next. Screens never see the token: they get the email and roles through a React context
 * (SessionContext.tsx), and src/api/http.ts asks this file for the token at each request.
 *
 * **Every end of a session is a full page load** (architecture 15). Signing out, the idle sign-out (T-046), a `401`
 * `not-authenticated` from any request, and a changed password all run {@link endSession}, which:
 *
 * 1. drops the token and the user (so no later request can carry it);
 * 2. blanks the page: React removes every screen, and with it all API data the screens held, at once;
 * 3. loads the sign-in screen as a new document with `window.location.replace`. A new document is the one sure way to
 *    leave no API data in memory, and `replace` swaps the current history entry, so Back does not return to the
 *    screen that was open (AC-34.4).
 *
 * **The back-forward cache (risk 23.2).** When the user goes Back or Forward, Edge and Chrome may show an earlier page
 * again from their "back-forward cache": a frozen copy of the whole page, its memory included, instead of loading it
 * anew. Two guards keep a signed-out page from coming back that way: the page is blanked and the session dropped
 * before leaving (so a frozen copy holds nothing), and the browser's `pageshow` event, which fires every time a page
 * is shown and says `persisted: true` when the page came out of that cache, replaces any such page with `/sign-in`.
 *
 * **Ready for tenants later** (decision #244): the session is one object built from the sign-in response, so a tenant
 * would be one more field here, not a new storage place.
 */
import { flushSync } from 'react-dom';
import { apiRequest, connectSession, type ApiResult } from '../api/http';
import { signInAddress, signInPath, type EndedReason } from './signInAddress';

/** The four roles, in the API's role order (design 7.1: Warehouse, Production, Manager, Seller). */
export type Role = 'Warehouse' | 'Production' | 'Manager' | 'Seller';

const knownRoles: readonly Role[] = ['Warehouse', 'Production', 'Manager', 'Seller'];

/** What the frame may know about the person signed in (AC-34.2): the email and roles from the sign-in response. */
export interface SessionUser {
  readonly email: string;
  /** In the order the API sent them, which is the role order (AC-34.2). */
  readonly roles: readonly Role[];
}

/**
 * The session as the screens see it. This is a "discriminated union", as in src/api/http.ts: `kind` says which case
 * it is, and only the signed-in case has a user.
 *
 * - `signed-out`: no one is signed in; every screen but sign-in goes to the sign-in screen (AC-34.1).
 * - `signed-in`: the user of the sign-in response.
 * - `ended`: the session has just ended and the page is leaving; nothing is drawn (the page is blank).
 */
export type SessionState =
  | { readonly kind: 'signed-out' }
  | { readonly kind: 'signed-in'; readonly user: SessionUser }
  | { readonly kind: 'ended' };

/** How a session ends (architecture 15): signing out, or one of the reasons the sign-in address names. */
export type EndReason = 'signed-out' | EndedReason;

/** The `200` body of `POST /auth/sign-in` (design 5.1). */
interface SignInResponse {
  readonly accessToken: string;
  readonly tokenType: string;
  readonly expiresAt: string;
  readonly email: string;
  readonly roles: readonly string[];
}

/** The whole sign-in response, kept in memory only. Never handed out: screens get {@link SessionUser}. */
interface Session {
  readonly token: string;
  /**
   * When the token stops working (8 hours after signing in). Kept as part of the session, but nothing watches it:
   * design 12.3 asks for no warning and no renewal, so the first `401` after it ends the session.
   */
  readonly expiresAt: string;
  readonly user: SessionUser;
}

/**
 * Loads a new document at a same-site address, replacing the current history entry. The real one is
 * `window.location.replace`; tests give a stand-in, because a simulated test page cannot load another document.
 */
export type PageLoader = (address: string) => void;

/** Options for {@link startSession}. */
export interface StartSessionOptions {
  /** How to load the sign-in screen when a session ends; `window.location.replace` unless given (tests only). */
  readonly loadPage?: PageLoader;
}

// The state the screens see. A fixed object per case where there is nothing else to hold: React compares snapshots
// by identity, so returning the same object means "nothing changed" (see subscribeToSession).
const signedOutState: SessionState = { kind: 'signed-out' };
const endedState: SessionState = { kind: 'ended' };

// SEC-10, AC-34.5: the one place the token is held.
let current: Session | null = null;
let state: SessionState = signedOutState;
const listeners = new Set<() => void>();

/** AC-34.4: the real page loader. `replace` (not `assign`) swaps the current history entry for the new document. */
const replaceDocument: PageLoader = (address) => {
  window.location.replace(address);
};
let loadPage: PageLoader = replaceDocument;

// What startSession set up, so that calling it again, or its stop function, undoes exactly that.
let stopCurrent: (() => void) | null = null;

/**
 * Starts the session module when the app starts (src/main.tsx calls it before the first screen is drawn, so before
 * any request can go out; T-039 review hand-over 1). It:
 *
 * - connects this module to src/api/http.ts with `connectSession`, so every request asks here for the token, and a
 *   `401` `not-authenticated` from any request ends the session (AC-34.6);
 * - listens for `pageshow` to replace a page restored from the back-forward cache (AC-34.4, risk 23.2).
 *
 * Returns a function that undoes both and forgets the session; tests call it after each test. Calling startSession
 * again first stops the previous start, so the listeners are never doubled.
 */
export function startSession(options: StartSessionOptions = {}): () => void {
  stopCurrent?.();

  loadPage = options.loadPage ?? replaceDocument;
  const disconnect = connectSession({
    currentToken: () => current?.token ?? null,
    onNotAuthenticated: handleNotAuthenticated,
  });

  // addEventListener asks the browser to call a function each time an event happens on the window. `pageshow` fires
  // whenever this page is shown, after a normal load (persisted false) and after a restore from the back-forward
  // cache (persisted true).
  const onPageShow = (event: PageTransitionEvent) => {
    if (event.persisted) {
      replaceRestoredPage();
    }
  };
  window.addEventListener('pageshow', onPageShow);

  const stop = () => {
    window.removeEventListener('pageshow', onPageShow);
    disconnect();
    current = null;
    state = signedOutState;
    loadPage = replaceDocument;
    if (stopCurrent === stop) {
      stopCurrent = null;
    }
  };
  stopCurrent = stop;
  return stop;
}

/** The session as the screens see it now. Never holds the token. */
export function getSessionState(): SessionState {
  return state;
}

/**
 * Asks to be told whenever the session changes, and returns a function that stops it. This is the shape React's
 * `useSyncExternalStore` hook expects (SessionContext.tsx): React calls `subscribe`, then reads
 * {@link getSessionState} again each time it is told, and draws the screens again only when the state object differs.
 */
export function subscribeToSession(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/**
 * US-34, design 16.1: `POST /auth/sign-in` with the typed email and password. On `200` the session starts and the
 * screens are told; the sign-in screen then opens the return path (AC-34.1, AC-34.2). Any failure is handed back
 * as it came (AC-34.3, AC-34.8), and no session starts.
 *
 * The request carries no token, because there is no session yet: http.ts adds the header only when one exists.
 */
export async function signIn(email: string, password: string): Promise<ApiResult<SessionUser>> {
  const result = await apiRequest<unknown>('POST', '/auth/sign-in', { body: { email, password } });
  if (!result.ok) {
    return result;
  }
  const session = sessionFrom(result.data);
  if (session === null) {
    // The contract (design 5.1) is trusted, but a token that a header cannot carry cannot be sent. With no message,
    // this is the "no message" failure with the status (decision #258 d: no fifth failure kind is invented).
    return { ok: false, failure: { kind: 'no-message', status: result.status } };
  }
  current = session;
  setState({ kind: 'signed-in', user: session.user });
  return { ok: true, status: result.status, data: session.user };
}

/** AC-34.4: the Sign out control. No dialog; the session ends and the sign-in screen opens with no message. */
export function signOut(): void {
  endSession('signed-out');
}

/**
 * Ends the session in the one way every end shares (architecture 15): drop the token, blank the page, then load the
 * sign-in address for the reason as a new document, replacing the history entry.
 *
 * | Reason | Address |
 * |--------|---------|
 * | `signed-out` (AC-34.4) | `/sign-in` |
 * | `idle` (AC-34.7, T-046) | `/sign-in?ended=idle` |
 * | `expired` (AC-34.6) | `/sign-in?ended=expired&return=<current path and query>` |
 * | `password-changed` (AC-35.1, T-045) | `/sign-in?ended=password-changed` |
 *
 * Safe to call more than once, and it never throws (T-039 review hand-over 2): several requests that were running
 * can each get a `401`, and this runs inside src/api/http.ts, where an exception would escape `apiRequest`. Only the
 * first call while signed in does anything; with no session there is nothing to end.
 */
export function endSession(reason: EndReason): void {
  if (state.kind !== 'signed-in') {
    return;
  }
  // The address is worked out first, while the page still shows the screen whose address it keeps.
  const address = addressFor(reason);
  leave(address);
}

/** AC-34.6: http.ts calls this for each `401` `not-authenticated`. */
function handleNotAuthenticated(): void {
  endSession('expired');
}

/**
 * Risk 23.2: this page came back from the back-forward cache. Whatever it held is dropped, and the sign-in screen
 * is loaded in its place. Unlike {@link endSession} this runs whatever the state, because a page that was already
 * blanked and left can still be restored: the copy kept in the cache is the blank one, and it must not stay.
 */
function replaceRestoredPage(): void {
  leave(signInPath);
}

/** The three steps every end shares. Each step is guarded, so nothing here can throw to its caller. */
function leave(address: string): void {
  // 1. SEC-10: drop the token first. This cannot fail, so even if a later step does, no request can carry it.
  current = null;
  // 2. Blank the page: SessionContext draws nothing while the state is `ended`.
  try {
    setState(endedState);
  } catch {
    // React itself failed while redrawing (each listener is already guarded). The page is leaving anyway, so the
    // next step still runs.
  }
  // 3. AC-34.4: a new document, replacing this history entry.
  try {
    loadPage(address);
  } catch {
    // The browser refused to load the address (it never should for a same-site path). Nothing else can be done
    // from here: the token is gone and the page is blank, so nothing can be seen or sent.
  }
}

/** The sign-in address for an end (see the table on {@link endSession}). */
function addressFor(reason: EndReason): string {
  switch (reason) {
    case 'signed-out':
      return signInPath;
    case 'idle':
    case 'password-changed':
      return signInAddress({ ended: reason });
    case 'expired':
      return expiredAddress();
  }
}

/**
 * AC-34.6: the address after a `401`, which brings the user back to the screen they were on. The router keeps the
 * real address bar up to date, so `window.location` holds that screen's path and query.
 */
function expiredAddress(): string {
  try {
    return signInAddress({ ended: 'expired', returnTo: `${window.location.pathname}${window.location.search}` });
  } catch {
    // Reading the address cannot really fail; if it ever did, the message still shows and Stock opens afterwards.
    return signInAddress({ ended: 'expired' });
  }
}

/**
 * Changes the state and tells every listener. `flushSync` (from React DOM) makes React draw the change before it
 * returns, instead of a moment later as it normally would. For an end of a session that is what blanks the page
 * *before* the next document is asked for, so the back-forward cache can never keep a copy with a screen on it.
 *
 * Each listener is called in its own `try` (T-041 review): one that fails must not keep the others, React's own
 * included, from being told, or the page would not blank. A failing listener has nothing useful to report here (the
 * console is not used, AC-34.5), so its error is dropped.
 */
function setState(next: SessionState): void {
  state = next;
  flushSync(() => {
    for (const listener of listeners) {
      try {
        listener();
      } catch {
        // Dropped on purpose; see above.
      }
    }
  });
}

/**
 * RFC 6750 section 2.1: the characters a bearer token may hold in an `Authorization` header (letters, digits, "-",
 * ".", "_", "~", "+", "/", then any "=" padding). A JWT always fits. Anything else, a line break or a space for example,
 * would make the browser refuse to build the header (`Headers.set` throws), so every later request would throw instead
 * of answering, and the error text could hold the token. Such a token is never kept (T-041 review).
 */
const bearerTokenPattern = /^[A-Za-z0-9\-._~+/]+=*$/;

/**
 * Builds the session from a `200` body, or null when it lacks what a session needs: a token that can be sent (see
 * {@link bearerTokenPattern}), an email and a roles list.
 */
function sessionFrom(data: unknown): Session | null {
  if (typeof data !== 'object' || data === null) {
    return null;
  }
  const body = data as Partial<Record<keyof SignInResponse, unknown>>;
  const { accessToken, expiresAt, email, roles } = body;
  if (typeof accessToken !== 'string' || !bearerTokenPattern.test(accessToken)) {
    return null;
  }
  if (typeof email !== 'string' || !Array.isArray(roles)) {
    return null;
  }
  return {
    token: accessToken,
    expiresAt: typeof expiresAt === 'string' ? expiresAt : '',
    // AC-34.2: roles in the order the API sent them. A role this front end does not know opens nothing, so it is
    // left out rather than shown or trusted.
    user: { email, roles: roles.filter(isRole) },
  };
}

function isRole(value: unknown): value is Role {
  return knownRoles.includes(value as Role);
}
