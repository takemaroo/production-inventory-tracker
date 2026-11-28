import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useSession } from '../session/SessionContext';
import { signInAddress } from '../session/signInAddress';

/** The props of {@link RequireSession}: the screen to show once someone is signed in. */
export interface RequireSessionProps {
  readonly children: ReactNode;
}

/**
 * AC-34.1, design 12.3, architecture 17: a screen that needs a session. With no one signed in, the screen is not drawn
 * at all, so no screen and no API data can be seen, and no request for its data is sent. Instead the address is
 * replaced with `/sign-in?return=<path and query>`, so after signing in this same screen opens.
 *
 * `<Navigate replace />` is React Router's way to change the address from inside the page: when drawn, it moves to
 * the new address without loading a new document. `replace` swaps the current history entry instead of adding one,
 * so Back does not return to an address that would only send the user to the sign-in screen again.
 */
export function RequireSession({ children }: RequireSessionProps) {
  const session = useSession();
  // useLocation gives the address the router is showing; it is the screen to come back to.
  const location = useLocation();
  if (session === null) {
    return <Navigate to={signInAddress({ returnTo: `${location.pathname}${location.search}` })} replace />;
  }
  return children;
}
