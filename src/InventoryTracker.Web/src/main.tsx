// The front end's entry point: the one script index.html loads. It starts the session module, then finds the page's
// #root element and asks React to draw the App component inside it. Everything else is reached from App.
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { startSession } from './session/session';

// Architecture 15, T-039 review hand-over 1: the session module is connected to the request function (src/api/http.ts)
// before anything is drawn, so before any request can go out. From here on every request asks it for the token, and a
// `401` `not-authenticated` from any request ends the session (AC-34.6). It also starts the back-forward cache guard
// (AC-34.4). The app runs until the page goes away, so the stop function it returns is not needed here.
startSession();

const container = document.getElementById('root');
if (container === null) {
  // Only possible if index.html is changed by mistake; failing loudly is better than a blank page.
  throw new Error('index.html has no element with id "root".');
}

// createRoot hands the element to React, which from then on owns everything inside it. StrictMode adds extra checks
// while developing (it runs some code twice to expose side effects); it changes nothing in the built front end.
createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
