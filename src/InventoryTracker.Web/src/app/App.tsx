import { BrowserRouter, Route, Routes, useLocation } from 'react-router';

/**
 * The root component of the front end.
 *
 * A React component is a function that returns what to show, written in JSX (HTML-like tags inside TypeScript).
 * React turns the JSX into page elements and updates them when the data changes. Text placed between tags, such as
 * {location.pathname} below, is always inserted as text, never as HTML, which is the "text binding only" rule of
 * AC-37.2: a name like <img src=x onerror=alert(1)> is shown as those characters and never runs.
 *
 * Skeleton only (docs/04-tasks.md: T-041 and T-042 replace it): one placeholder page for every address, which proves that the
 * build, the API's SPA fallback (A-1: any screen address returns this page) and the client-side router work together.
 */
export function App() {
  // BrowserRouter reads the address bar and lets the components below react to it without reloading the page.
  // Routes picks the first Route whose path matches; "*" matches any address.
  return (
    <BrowserRouter>
      <Routes>
        <Route path="*" element={<PlaceholderPage />} />
      </Routes>
    </BrowserRouter>
  );
}

/** The skeleton's only page: names the product and the address that was opened. */
function PlaceholderPage() {
  // useLocation is a "hook": a function a component calls to read something React keeps for it, here the current
  // address. When the address changes, React draws the component again with the new value.
  const location = useLocation();

  return (
    <main>
      <h1>Production &amp; Inventory Tracker</h1>
      <p>The web front end (version 1.2) is being built. This placeholder proves the build and the serving work.</p>
      <p>
        Address opened: <code>{location.pathname}</code>
      </p>
    </main>
  );
}
