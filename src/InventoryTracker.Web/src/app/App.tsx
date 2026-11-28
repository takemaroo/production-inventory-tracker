import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router';
import { SafeText } from '../components/SafeText';
import { SignInScreen } from '../screens/auth/SignInScreen';
import { signOut } from '../session/session';
import { SessionProvider, useSession } from '../session/SessionContext';
import { homePath, signInPath } from '../session/signInAddress';
import { productName } from './documentTitle';
import { RequireSession } from './RequireSession';

/**
 * The root component of the front end.
 *
 * A React component is a function that returns what to show, written in JSX (HTML-like tags inside TypeScript).
 * React turns the JSX into page elements and updates them when the data changes. Text placed between tags is always
 * inserted as text, never as HTML, which is the "text binding only" rule of AC-37.2.
 *
 * T-041: a minimal router. `/sign-in` is the sign-in screen (design 16.1); `/` opens Stock (design 12.3); every other
 * address needs a session (RequireSession, AC-34.1) and shows a temporary signed-in page. T-042 replaces that page
 * with the whole route table (src/app/routes.tsx), the page frame and the screens' placeholders.
 *
 * The session module itself is started in src/main.tsx, before this component is first drawn.
 */
export function App() {
  // SessionProvider is outermost, so every screen can read the session and the whole page blanks when it ends.
  // BrowserRouter reads the address bar and lets the components below react to it without reloading the page.
  // Routes picks the Route whose path matches the address best; "*" matches any address.
  return (
    <SessionProvider>
      <BrowserRouter>
        <Routes>
          <Route path={signInPath} element={<SignInScreen />} />
          <Route path="/" element={<Navigate to={homePath} replace />} />
          <Route
            path="*"
            element={
              <RequireSession>
                <SignedInPlaceholder />
              </RequireSession>
            }
          />
        </Routes>
      </BrowserRouter>
    </SessionProvider>
  );
}

/**
 * Temporary (T-041 only; T-042 replaces it with the frame and the real screens): what every address shows once signed
 * in. It names the person signed in as the header will, "maria.lopez@example.com (Manager)" (AC-34.2, design 18.1),
 * offers Sign out (AC-34.4), and shows the address that was opened, so AC-34.1 and AC-34.6 can be seen working.
 */
function SignedInPlaceholder() {
  const user = useSession();
  const location = useLocation();
  // RequireSession draws this only with a session, so this is never null here; the check keeps TypeScript sure.
  if (user === null) {
    return null;
  }

  return (
    <main id="main">
      <h1>{productName}</h1>
      <p>
        {/* The email and role names come from the API, so they are SafeText (AC-37.1). */}
        Signed in as <SafeText text={user.email} /> (<SafeText text={user.roles.join(', ')} />)
      </p>
      <p>
        Address opened: <code>{`${location.pathname}${location.search}`}</code>
      </p>
      <p>The screens arrive with the next tasks.</p>
      <button className="btn btn--secondary" type="button" onClick={signOut}>
        Sign out
      </button>
    </main>
  );
}
