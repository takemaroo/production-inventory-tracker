import { StrictMode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../../app/App';
import { noAnswerText } from '../../api/http';
import { startSession, type PageLoader } from '../../session/session';

// The sign-in screen of design 16.1, drawn through the whole App so the router, the session module and the request
// function are the real ones. Only fetch is stubbed (never a real network), and the page loader is a stand-in.
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

/** Design 7.1: a sign-in refusal, `400` for `validation` and `401` otherwise, with the same status in its body. */
function refusal(code: string, detail: string, errors?: Record<string, string[]>): Response {
  const status = code === 'validation' ? 400 : 401;
  return answer(status, { status, code, detail, errors });
}

/** Opens the sign-in screen at an address and waits for it. */
async function openSignIn(address = '/sign-in') {
  window.history.replaceState(null, '', address);
  const view = render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
  await screen.findByRole('heading', { level: 1, name: 'Sign in' });
  return view;
}

function emailField(): HTMLInputElement {
  return screen.getByLabelText('Email');
}

function passwordField(): HTMLInputElement {
  return screen.getByLabelText('Password');
}

function signInButton(): HTMLElement {
  return screen.getByRole('button', { name: 'Sign in' });
}

/** The error message area (design 12.4), which has no role of its own: found by its class, as the mockup draws it. */
function errorArea(view: { container: HTMLElement }): HTMLElement | null {
  return view.container.querySelector('.msg--error');
}

async function typeAndSend(email: string, password: string): Promise<void> {
  const user = userEvent.setup();
  if (email !== '') {
    await user.type(emailField(), email);
  }
  if (password !== '') {
    await user.type(passwordField(), password);
  }
  await user.click(signInButton());
}

function currentAddress(): string {
  return `${window.location.pathname}${window.location.search}`;
}

describe('sign-in screen layout (design 16.1)', () => {
  // Design 16.1, AC-34.1: the fields with their types and autocomplete, the button, the product name, the footer.
  it('shows the card the design specifies', async () => {
    const view = await openSignIn();

    expect(emailField().type).toBe('email');
    expect(emailField().getAttribute('autocomplete')).toBe('username');
    expect(emailField().getAttribute('spellcheck')).toBe('false');
    expect(passwordField().type).toBe('password');
    expect(passwordField().getAttribute('autocomplete')).toBe('current-password');
    expect(signInButton().getAttribute('type')).toBe('submit');
    expect(screen.getByText('Production & Inventory Tracker')).toBeTruthy();
    // F-8, design 12.2: the footer line with the computer's offset, on the sign-in screen too.
    expect(view.container.querySelector('.footer')?.textContent).toMatch(/^Times are local \(UTC[+-]\d{2}:\d{2}\)\.$/);
    // Design 16.1: no header or menu.
    expect(screen.queryByRole('banner')).toBeNull();
    expect(screen.queryByRole('navigation')).toBeNull();
  });

  // Design 12.2: the document title, "{page title}", an en dash (U+2013), then "Production & Inventory Tracker".
  it('has its own document title', async () => {
    await openSignIn();

    await waitFor(() => expect(document.title).toBe(`Sign in ${String.fromCodePoint(0x2013)} Production & Inventory Tracker`));
  });

  // Design 16.1: on first open, focus is in Email.
  it('starts with focus in Email', async () => {
    await openSignIn();

    await waitFor(() => expect(document.activeElement).toBe(emailField()));
  });

  // Design 16.1, row 83: when the browser's password manager has already filled Email, focus starts in Password.
  // A password manager fills the page without telling React, so the test does the same: the page reports a value for
  // the Email field from the first moment, while React's own state stays empty. (T-044 can see real autofill in Edge.)
  it('starts with focus in Password when Email is already filled', async () => {
    const valueOnThePage = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    vi.spyOn(HTMLInputElement.prototype, 'value', 'get').mockImplementation(function (this: HTMLInputElement) {
      return this.id === 'email' ? 'maria.lopez@example.com' : (valueOnThePage?.get?.call(this) as string);
    });

    await openSignIn();

    await waitFor(() => expect(document.activeElement).toBe(passwordField()));
  });

  // Design 16.1, plain /sign-in (AC-34.4, after a sign-out): no arrival message.
  it('shows no message on the plain address', async () => {
    const view = await openSignIn();

    expect(screen.queryByRole('status')).toBeNull();
    expect(errorArea(view)).toBeNull();
  });
});

describe('arrival messages (design 16.1, 18.1)', () => {
  // AC-34.7 (the sign-out itself is T-046), AC-34.6, AC-35.1: each end's fixed sentence as a status message.
  it.each([
    ['idle', 'You were signed out after 15 minutes without activity.'],
    ['expired', 'Your sign-in is missing or has expired. Sign in again.'],
    ['password-changed', 'Password changed. Sign in with your new password.'],
  ])('shows the message for ended=%s', async (ended, text) => {
    await openSignIn(`/sign-in?ended=${ended}`);

    expect(screen.getByRole('status').textContent).toBe(`i${text}`);
    expect(screen.getByText(text)).toBeTruthy();
  });

  // The address cannot put words on the screen: an unknown value shows nothing.
  it('shows nothing for an unknown ended value', async () => {
    await openSignIn('/sign-in?ended=You%20have%20won');

    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.queryByText(/You have won/)).toBeNull();
  });
});

describe('AC-34.3: refusals and blank fields', () => {
  // AC-34.3: wrong password, unknown email or deactivated user; email kept, password emptied, focus on the message.
  it('shows "Invalid email or password.", keeps the email and empties the password', async () => {
    const view = await openSignIn('/sign-in?ended=idle');
    fetchStub.mockResolvedValueOnce(refusal('invalid-credentials', 'Invalid email or password.'));

    await typeAndSend('maria.lopez@example.com', 'wrong-password');

    await waitFor(() => expect(errorArea(view)?.textContent).toBe('!Invalid email or password.'));
    expect(emailField().value).toBe('maria.lopez@example.com');
    expect(passwordField().value).toBe('');
    expect(document.activeElement).toBe(errorArea(view));
    // Design 16.1: the arrival message is replaced.
    expect(screen.queryByRole('status')).toBeNull();
    // Signing in is not a change, so the "Nothing was changed." line is not shown.
    expect(screen.queryByText('Nothing was changed.')).toBeNull();
    expect(loadPage).not.toHaveBeenCalled();
  });

  // AC-34.3, AC-01.4: a locked account shows the API's sentence.
  it('shows "Account temporarily locked. Try again later."', async () => {
    const view = await openSignIn();
    fetchStub.mockResolvedValueOnce(refusal('account-locked', 'Account temporarily locked. Try again later.'));

    await typeAndSend('maria.lopez@example.com', 'Sample-pass1');

    await waitFor(() => expect(errorArea(view)?.textContent).toBe('!Account temporarily locked. Try again later.'));
    expect(passwordField().value).toBe('');
  });

  // AC-34.3, design 12.4: both blank gives both sentences, in the message area and under each field; nothing sent.
  it('checks blank fields without a request', async () => {
    const view = await openSignIn();

    await userEvent.setup().click(signInButton());

    expect(fetchStub).not.toHaveBeenCalled();
    const area = errorArea(view);
    expect([...(area?.querySelectorAll('p') ?? [])].map((p) => p.textContent)).toEqual([
      'Email is required.',
      'Password is required.',
    ]);
    expect(emailField().getAttribute('aria-invalid')).toBe('true');
    expect(document.getElementById(emailField().getAttribute('aria-describedby') ?? '')?.textContent).toBe('Email is required.');
    expect(passwordField().getAttribute('aria-invalid')).toBe('true');
    expect(document.getElementById(passwordField().getAttribute('aria-describedby') ?? '')?.textContent).toBe(
      'Password is required.',
    );
    await waitFor(() => expect(document.activeElement).toBe(area));
  });

  // AC-34.3: an email of spaces is blank; the typed password is kept, since nothing was sent (decision #262).
  it('treats an email of spaces as blank and keeps the password', async () => {
    const view = await openSignIn();

    await typeAndSend('   ', 'Sample-pass1');

    expect(fetchStub).not.toHaveBeenCalled();
    expect(errorArea(view)?.textContent).toBe('!Email is required.');
    expect(passwordField().getAttribute('aria-invalid')).toBeNull();
    expect(passwordField().value).toBe('Sample-pass1');
  });

  // F-1, design 12.4: a field message from the API also sits under its field.
  it('puts an API field message under its field', async () => {
    await openSignIn();
    fetchStub.mockResolvedValueOnce(refusal('validation', 'Email is required.', { email: ['Email is required.'] }));

    await typeAndSend('x', 'Sample-pass1');

    await waitFor(() => expect(emailField().getAttribute('aria-invalid')).toBe('true'));
    expect(document.getElementById('email-error')?.textContent).toBe('Email is required.');
  });

  // Design 12.4: an answer with no ProblemDetails body shows the status sentence.
  it('shows the no-message sentence for an answer without a message', async () => {
    const view = await openSignIn();
    fetchStub.mockResolvedValueOnce(new Response(null, { status: 500 }));

    await typeAndSend('maria.lopez@example.com', 'Sample-pass1');

    await waitFor(() =>
      expect(errorArea(view)?.textContent).toBe('!The API answered with status 500 and no message. Try again.'),
    );
  });
});

describe('AC-34.8: the API does not answer', () => {
  // AC-34.8, F-2: no answer shows the F-2 text, and the email is kept.
  it('shows the F-2 message', async () => {
    const view = await openSignIn();
    fetchStub.mockRejectedValueOnce(new TypeError('Failed to fetch'));

    await typeAndSend('maria.lopez@example.com', 'Sample-pass1');

    await waitFor(() => expect(errorArea(view)?.textContent).toBe(`!${noAnswerText}`));
    expect(noAnswerText).toBe('Cannot reach the API. Check that it is running, then try again.');
    expect(emailField().value).toBe('maria.lopez@example.com');
  });
});

describe('F-3: one request per click', () => {
  // F-3, design 12.4: while the request runs the button is aria-disabled with the spinner; more clicks send nothing.
  it('marks the button busy and ignores further submits', async () => {
    const view = await openSignIn();
    let answerSignIn: (response: Response) => void = () => {};
    fetchStub.mockReturnValueOnce(
      new Promise<Response>((resolve) => {
        answerSignIn = resolve;
      }),
    );
    const user = userEvent.setup();

    await typeAndSend('maria.lopez@example.com', 'Sample-pass1');
    await user.click(signInButton());
    await user.type(passwordField(), '{Enter}');

    expect(fetchStub).toHaveBeenCalledOnce();
    expect(signInButton().getAttribute('aria-disabled')).toBe('true');
    expect(signInButton().querySelector('.spin')).toBeTruthy();
    // The label does not change, so the button keeps its size.
    expect(signInButton().textContent).toBe('Sign in');

    answerSignIn(refusal('invalid-credentials', 'Invalid email or password.'));
    await waitFor(() => expect(errorArea(view)).toBeTruthy());
    expect(signInButton().getAttribute('aria-disabled')).toBeNull();
    expect(signInButton().querySelector('.spin')).toBeNull();
  });
});

describe('the return-path rule after signing in (design 12.3)', () => {
  // T-041 criterion: "//evil.example", "/sign-in" and "https://..." all give /stock.
  it.each([
    ['//evil.example', '/stock'],
    ['https%3A%2F%2Fevil.example%2Fstock', '/stock'],
    ['/sign-in', '/stock'],
    ['/units', '/units'],
  ])('return=%s opens %s', async (value, expected) => {
    await openSignIn(`/sign-in?return=${value}`);
    fetchStub.mockResolvedValueOnce(signInAnswer());

    await typeAndSend('maria.lopez@example.com', 'Sample-pass1');

    await waitFor(() => expect(currentAddress()).toBe(expected));
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy();
  });

  // Decision #200: after the idle sign-out, Stock opens (the next person is often someone else).
  it('opens Stock after an idle sign-out', async () => {
    await openSignIn('/sign-in?ended=idle');
    fetchStub.mockResolvedValueOnce(signInAnswer());

    await typeAndSend('maria.lopez@example.com', 'Sample-pass1');

    await waitFor(() => expect(currentAddress()).toBe('/stock'));
  });
});
