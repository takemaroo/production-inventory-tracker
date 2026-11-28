import { StrictMode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { apiRequest } from '../api/http';
import { signIn, startSession, type PageLoader } from '../session/session';
import { App } from './App';

// Testing Library draws a component into the simulated page (jsdom) and finds things the way a user would: by role
// and visible text, not by CSS class. The API is a stubbed fetch, never a real network. The session module is the
// real one, started as src/main.tsx starts it, but with a stand-in page loader: jsdom cannot load a new document, so
// the loader records the address and the test checks what the page showed at that moment.
const fetchStub = vi.fn<typeof fetch>();
const loadPage = vi.fn<PageLoader>();
let stop: (() => void) | undefined;

beforeEach(() => {
  fetchStub.mockReset();
  loadPage.mockReset();
  vi.stubGlobal('fetch', fetchStub);
  stop = startSession({ loadPage });
});

afterEach(() => {
  cleanup();
  stop?.();
  stop = undefined;
  vi.unstubAllGlobals();
});

/** A JSON answer as the API writes it. */
function answer(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Design 5.1: the sign-in response for a Manager. */
function signInAnswer(): Response {
  return answer(200, {
    accessToken: 'token-123',
    tokenType: 'Bearer',
    expiresAt: '2026-11-28T22:05:00Z',
    email: 'maria.lopez@example.com',
    roles: ['Manager'],
  });
}

/** Opens the front end at an address, as typing it into the address bar would (StrictMode as in src/main.tsx). */
function openAt(address: string) {
  window.history.replaceState(null, '', address);
  return render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

/** The address bar now: path and query. */
function currentAddress(): string {
  return `${window.location.pathname}${window.location.search}`;
}

/** Types the email and password and presses Sign in, with the API answering the sign-in. */
async function signInThroughTheScreen(): Promise<void> {
  const user = userEvent.setup();
  fetchStub.mockResolvedValueOnce(signInAnswer());
  await user.type(screen.getByLabelText('Email'), 'maria.lopez@example.com');
  await user.type(screen.getByLabelText('Password'), 'Sample-pass1');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

describe('AC-34.1: any address without a session goes to the sign-in screen', () => {
  // AC-34.1: the sign-in screen shows, the address keeps the screen, and no screen or data request is made.
  it('replaces the address with /sign-in?return=... and shows only the sign-in screen', async () => {
    openAt('/production-orders/12?status=Created');

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeTruthy();
    expect(currentAddress()).toBe('/sign-in?return=/production-orders/12%3Fstatus%3DCreated');
    expect(screen.getByLabelText('Email')).toBeTruthy();
    expect(screen.getByLabelText('Password')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeTruthy();
    expect(screen.queryByText(/Signed in as/)).toBeNull();
    expect(fetchStub).not.toHaveBeenCalled();
  });

  // AC-34.1, AC-36.3: after signing in, the address that was opened opens, its query kept.
  it('opens the address that was asked for after signing in', async () => {
    openAt('/production-orders/12?status=Created');
    await screen.findByRole('heading', { name: 'Sign in' });

    await signInThroughTheScreen();

    await waitFor(() => expect(currentAddress()).toBe('/production-orders/12?status=Created'));
    expect(screen.getByText('/production-orders/12?status=Created')).toBeTruthy();
  });

  // AC-34.1: Back after signing in does not lead to the sign-in form again (each step replaced its history entry).
  it('replaces the history entry at each step', async () => {
    const lengthBefore = window.history.length;
    openAt('/units');
    await screen.findByRole('heading', { name: 'Sign in' });

    await signInThroughTheScreen();

    await waitFor(() => expect(currentAddress()).toBe('/units'));
    expect(window.history.length).toBe(lengthBefore);
  });

  // Design 12.3: "/" opens Stock, which itself needs a session.
  it('sends / to the sign-in screen with Stock as the return path', async () => {
    openAt('/');

    await screen.findByRole('heading', { name: 'Sign in' });
    expect(currentAddress()).toBe('/sign-in?return=/stock');
  });
});

describe('AC-34.2: signing in opens Stock', () => {
  // AC-34.2: from the plain sign-in screen, a correct sign-in opens /stock and names the user and roles.
  it('opens /stock and shows who is signed in', async () => {
    openAt('/sign-in');
    await screen.findByRole('heading', { name: 'Sign in' });

    await signInThroughTheScreen();

    await waitFor(() => expect(currentAddress()).toBe('/stock'));
    expect(screen.getByText(/Signed in as/).textContent).toBe('Signed in as maria.lopez@example.com (Manager)');
  });

  // Design 12.3: someone already signed in who opens /sign-in goes to Stock.
  it('sends a signed-in user from /sign-in to /stock', async () => {
    fetchStub.mockResolvedValueOnce(signInAnswer());
    await signIn('maria.lopez@example.com', 'Sample-pass1');

    openAt('/sign-in');

    await waitFor(() => expect(currentAddress()).toBe('/stock'));
    expect(screen.queryByRole('heading', { name: 'Sign in' })).toBeNull();
  });

  // Design 12.3, architecture 17 (T-041 review, delivery manager's ruling): a signed-in arrival at /sign-in opens
  // Stock whatever its return value; only a sign-in made on the screen opens the return path.
  it('sends a signed-in user from /sign-in?return=... to /stock, not to the return path', async () => {
    fetchStub.mockResolvedValueOnce(signInAnswer());
    await signIn('maria.lopez@example.com', 'Sample-pass1');

    openAt('/sign-in?return=/units');

    await waitFor(() => expect(currentAddress()).toBe('/stock'));
    expect(screen.getByText('/stock')).toBeTruthy();
  });
});

describe('AC-34.4: sign-out', () => {
  // AC-34.4: Sign out blanks the page and drops the token before /sign-in loads, replacing the history entry.
  it('blanks the page, drops the token, then loads /sign-in', async () => {
    const view = openAt('/sign-in');
    await screen.findByRole('heading', { name: 'Sign in' });
    await signInThroughTheScreen();
    const signOutButton = await screen.findByRole('button', { name: 'Sign out' });
    let pageWhenLoading: string | undefined;
    loadPage.mockImplementation(() => {
      pageWhenLoading = view.container.textContent;
    });

    await userEvent.setup().click(signOutButton);

    expect(loadPage).toHaveBeenCalledExactlyOnceWith('/sign-in');
    expect(pageWhenLoading).toBe('');
    expect(view.container.childElementCount).toBe(0);
    // SEC-10: a request now carries no token.
    fetchStub.mockResolvedValueOnce(answer(200, {}));
    await apiRequest('GET', '/stock');
    expect(new Headers(fetchStub.mock.calls[1]?.[1]?.headers).get('Authorization')).toBeNull();
  });
});

describe('AC-34.6: a 401 from any request ends the session', () => {
  // AC-34.6: the page blanks and /sign-in?ended=expired&return=<current screen> loads.
  it('ends at /sign-in?ended=expired&return=... with the page blank', async () => {
    const view = openAt('/production-orders/12');
    await screen.findByRole('heading', { name: 'Sign in' });
    await signInThroughTheScreen();
    await waitFor(() => expect(currentAddress()).toBe('/production-orders/12'));
    let childrenWhenLoading = -1;
    loadPage.mockImplementation(() => {
      childrenWhenLoading = view.container.childElementCount;
    });
    fetchStub.mockResolvedValueOnce(
      answer(401, { code: 'not-authenticated', detail: 'Your sign-in is missing or has expired. Sign in again.' }),
    );

    // act() lets React finish drawing what a change outside a click caused before the test looks at the page.
    await act(async () => {
      await apiRequest('GET', '/production-orders/12');
    });

    expect(loadPage).toHaveBeenCalledExactlyOnceWith('/sign-in?ended=expired&return=/production-orders/12');
    expect(childrenWhenLoading).toBe(0);
  });

  // AC-34.6: the new sign-in document shows the message, and after signing in the same screen opens again.
  it('shows the message, then reopens the screen after signing in', async () => {
    openAt('/sign-in?ended=expired&return=/production-orders/12');

    expect((await screen.findByRole('status')).textContent).toContain('Your sign-in is missing or has expired. Sign in again.');
    await signInThroughTheScreen();

    await waitFor(() => expect(currentAddress()).toBe('/production-orders/12'));
  });
});

describe('back-forward cache guard (AC-34.4, risk 23.2)', () => {
  // T-041 criterion: a page restored from the back-forward cache (pageshow persisted) is blanked and replaced.
  it('replaces a restored page with /sign-in', async () => {
    const view = openAt('/sign-in');
    await screen.findByRole('heading', { name: 'Sign in' });
    await signInThroughTheScreen();
    await screen.findByRole('button', { name: 'Sign out' });

    act(() => {
      window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
    });

    expect(loadPage).toHaveBeenCalledExactlyOnceWith('/sign-in');
    expect(view.container.childElementCount).toBe(0);
  });
});
