import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { App } from './App';

// Testing Library draws a component into the simulated page (jsdom) and finds things the way a user would: by role
// and visible text, not by CSS class. cleanup removes what a test drew, so tests never see each other's pages.
afterEach(cleanup);

describe('App (skeleton placeholder)', () => {
  // Skeleton check, and the base of AC-36.3: any screen address opened directly shows the front end's page,
  // which reads the address itself.
  it('shows the product name and the address that was opened', () => {
    window.history.pushState({}, '', '/production-orders/12');

    render(<App />);

    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Production & Inventory Tracker');
    expect(screen.getByText('/production-orders/12')).toBeTruthy();
  });
});
