/**
 * Hands the session to the screens through React (architecture 15: "a React context gives the frame the email and
 * roles"). The token never passes through here; only {@link SessionUser} does.
 */
import { createContext, useContext, useSyncExternalStore, type ReactNode } from 'react';
import { getSessionState, subscribeToSession, type SessionState, type SessionUser } from './session';

/** What the context carries: the session while it can be shown (never `ended`, which draws nothing). */
type ShownSession = Exclude<SessionState, { kind: 'ended' }>;

/**
 * A React "context" lets a component far down the page read a value without every component in between passing it
 * along. A provider puts the value in (SessionProvider below); any component inside it reads it with the
 * `useContext` hook (useSession below). The starting value here is what a component would read with no provider
 * around it: no one signed in.
 */
const SessionContext = createContext<ShownSession>({ kind: 'signed-out' });

/** The props of {@link SessionProvider}: the whole app goes inside it. */
export interface SessionProviderProps {
  readonly children: ReactNode;
}

/**
 * Puts the session within reach of every screen, and blanks the page when a session ends (architecture 15).
 *
 * `useSyncExternalStore` is the React hook for reading a value that lives outside React, here the session module's
 * state. React subscribes to the module, reads the state, and draws this component again whenever the module says it
 * changed, so a sign-in or sign-out shows at once everywhere.
 *
 * While the state is `ended` this returns nothing: React then removes every screen from the page, and with them all
 * the API data they held, before the sign-in screen loads as a new document (design 12.3, AC-34.4).
 */
export function SessionProvider({ children }: SessionProviderProps) {
  const state = useSyncExternalStore(subscribeToSession, getSessionState);
  if (state.kind === 'ended') {
    return null;
  }
  // In React 19 a context object is itself its provider: <SessionContext value={...}>.
  return <SessionContext value={state}>{children}</SessionContext>;
}

/**
 * The person signed in (email and roles, AC-34.2), or null when no one is. A "hook" is a function whose name starts
 * with "use" and which only components (or other hooks) call, always in the same order on every draw; React relies on
 * that order to know which stored value belongs to which call. `useContext` reads the nearest provider's value and
 * draws the component again when it changes.
 */
export function useSession(): SessionUser | null {
  const session = useContext(SessionContext);
  return session.kind === 'signed-in' ? session.user : null;
}
