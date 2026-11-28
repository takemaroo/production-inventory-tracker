/**
 * The sign-in screen's address and the values it carries (design 12.3, 16.1; architecture 15).
 *
 * Every way into the sign-in screen is an address:
 *
 * - `/sign-in?return=/production-orders/12`: a screen was opened without a session (AC-34.1). After signing in, that
 *   screen opens again.
 * - `/sign-in`: the user signed out (AC-34.4). No message.
 * - `/sign-in?ended=idle`, `?ended=expired&return=...`, `?ended=password-changed`: a session ended by itself. The
 *   screen shows the matching arrival message from a fixed list (design 18.1).
 *
 * Both values come from the address bar, which anyone can type into or link to, so neither is trusted: `ended` only
 * picks one of the three fixed texts, and `return` is opened only when it is a path on this site (no open redirect).
 * Neither value is API data, so putting them in the address is allowed (AC-34.5).
 */

/** The sign-in screen's path. */
export const signInPath = '/sign-in';

/** Design 12.3: where a successful sign-in goes when there is no usable return path. Stock is everyone's home. */
export const homePath = '/stock';

/** The name of the query value that holds the screen to open after signing in. */
export const returnParameter = 'return';

/** The name of the query value that says how the last session ended. */
export const endedParameter = 'ended';

/**
 * How a session ended, as written in the sign-in address (architecture 15, table "Every end of a session"). Signing
 * out has no value of its own: it opens the plain `/sign-in`.
 */
export type EndedReason = 'idle' | 'expired' | 'password-changed';

/** AC-34.7, design 18.1: the arrival message after 15 minutes without activity (the timer itself is T-046). */
export const idleText = 'You were signed out after 15 minutes without activity.';

/** AC-34.6, design 18.1 (the API's own words for `not-authenticated`, design 7.1). */
export const expiredText = 'Your sign-in is missing or has expired. Sign in again.';

/** AC-35.1, design 18.1: after changing one's own password (the screen is T-045). */
export const passwordChangedText = 'Password changed. Sign in with your new password.';

/**
 * Design 16.1: the arrival message for the `ended` value in the address, or null for none. Only the three known
 * values give a message; anything else, typed or linked, shows nothing, so the address can never put words of its own
 * on the screen.
 */
export function arrivalMessage(ended: string | null): string | null {
  switch (ended) {
    case 'idle':
      return idleText;
    case 'expired':
      return expiredText;
    case 'password-changed':
      return passwordChangedText;
    default:
      return null;
  }
}

// Characters that never belong in a return path. Browsers ignore tabs and line breaks inside an address and read a
// backslash as a slash, so "/\evil.example" and "/<tab>/evil.example" would both become "//evil.example": another
// site. Rejecting them (and every other control character) closes those doors before the "//" check below.
// The pattern is built from code points so this file holds no invisible character itself.
const unsafeCharacters = new RegExp(`[${String.fromCodePoint(0x00)}-${String.fromCodePoint(0x1f)}${String.fromCodePoint(0x7f)}\\\\]`);

/**
 * Design 12.3, architecture 15 (no open redirect): the path to open after signing in. The `return` value is used only
 * when it is a path on this site: it starts with a single "/", not "//" (which a browser reads as another site,
 * "//evil.example"), holds no backslash or control character and no "." or ".." segment, and is not the sign-in screen
 * itself (which would only open the form again). Anything else, a missing value included, gives Stock.
 *
 * Examples: "/production-orders/12?status=Created" is kept; "//evil.example", "https://evil.example",
 * "/sign-in?return=/x" and "stock" all give "/stock".
 */
export function safeReturnPath(value: string | null): string {
  if (value === null || !value.startsWith('/') || value.startsWith('//') || unsafeCharacters.test(value)) {
    return homePath;
  }
  if (hasDotSegment(value) || isSignInPath(value)) {
    return homePath;
  }
  return value;
}

/**
 * True when the path (before any query) has a "." or ".." segment (T-041 review). A browser removes those segments
 * when it reads an address, so "/..//evil.example" becomes "//evil.example" (another site) and "/./sign-in" becomes
 * the sign-in screen. No screen address of design 12.1 has such a segment, so refusing them all keeps this rule safe
 * on its own, whatever the router does. "%2e" is how an address may write a dot, and browsers treat "%2e%2e" as ".."
 * too, so it counts as a dot here.
 */
function hasDotSegment(value: string): boolean {
  const pathOnly = (value.split(/[?#]/, 1)[0] ?? '').replace(/%2e/gi, '.');
  return pathOnly.split('/').some((segment) => segment === '.' || segment === '..');
}

/**
 * True when a path (with or without its query) is the sign-in screen. The router ignores letter case and a trailing
 * slash when it matches an address, so "/SIGN-IN" and "/sign-in/" are the sign-in screen too.
 */
function isSignInPath(value: string): boolean {
  const pathOnly = value.split(/[?#]/, 1)[0] ?? '';
  const trimmed = pathOnly.replace(/\/+$/, '');
  return trimmed.toLowerCase() === signInPath;
}

/** What {@link signInAddress} puts in the address. Each part is optional. */
export interface SignInAddressParts {
  /** How the session ended; leave out for a sign-out or a first visit. */
  readonly ended?: EndedReason;
  /** The screen to open after signing in (path and query). Left out unless it is a safe return path. */
  readonly returnTo?: string;
}

/**
 * Builds the sign-in address, for example `/sign-in?ended=expired&return=/production-orders/12`.
 *
 * The return path is escaped with `encodeURIComponent`, so its own "?", "&" and "=" stay inside the one value and
 * can never add a parameter. The slashes are then written back as they are: a slash is allowed inside a query value,
 * and the address stays readable, as design 12.1 writes it (`/sign-in?return=/production-orders/12`).
 */
export function signInAddress(parts: SignInAddressParts = {}): string {
  const values: string[] = [];
  if (parts.ended !== undefined) {
    values.push(`${endedParameter}=${parts.ended}`);
  }
  if (parts.returnTo !== undefined && safeReturnPath(parts.returnTo) === parts.returnTo) {
    values.push(`${returnParameter}=${encodeURIComponent(parts.returnTo).replaceAll('%2F', '/')}`);
  }
  return values.length === 0 ? signInPath : `${signInPath}?${values.join('&')}`;
}
