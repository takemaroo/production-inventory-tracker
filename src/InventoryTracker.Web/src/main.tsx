// The front end's entry point: the one script index.html loads. It finds the page's #root element and asks React to
// draw the App component inside it. Everything else is reached from App.
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';

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
