/**
 * The sign-in screen (US-34; design 16.1, mockup design/mockups/sign-in.html; architecture 15).
 *
 * The class names are the design's (design/mockups/app.css), so the screen takes its look from the stylesheet that
 * T-042 adds, with no inline style (the source guard forbids it, AC-37.5).
 */
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Navigate, useSearchParams } from 'react-router';
import { failureText, fieldErrors, type ApiFailure } from '../../api/http';
import { documentTitle, productName } from '../../app/documentTitle';
import { SafeText } from '../../components/SafeText';
import { formatUtcOffset } from '../../format/times';
import { signIn } from '../../session/session';
import { useSession } from '../../session/SessionContext';
import {
  arrivalMessage,
  endedParameter,
  expiredText,
  homePath,
  returnParameter,
  safeReturnPath,
} from '../../session/signInAddress';

/** AC-34.3, design 7.1 and 12.4: a blank email, in the API's own words. Nothing is sent. */
export const emailRequiredText = 'Email is required.';

/** AC-34.3, design 7.1 and 12.4: a blank password, in the API's own words. Nothing is sent. */
export const passwordRequiredText = 'Password is required.';

/** The field names a message can belong to on this form; they are also the API's `errors` keys (design 12.4). */
type FieldName = 'email' | 'password';

/** What the message area shows after a failed sign-in or a failed check, and the messages under each field. */
interface Problem {
  /** Each message, in order; the API's `detail` word for word for a refusal (F-1). */
  readonly messages: readonly string[];
  readonly fields: Readonly<Partial<Record<FieldName, readonly string[]>>>;
}

/**
 * Design 12.4, AC-34.3: the checks the front end makes itself before sending. Email blank (spaces count as blank, as
 * the API judges it) or password empty (a password of spaces is still a password, as for the API) gives the API's
 * own sentence, so the user sees the same words either way. Returns null when both are filled.
 */
function checkFields(email: string, password: string): Problem | null {
  const fields: Partial<Record<FieldName, readonly string[]>> = {};
  const messages: string[] = [];
  if (email.trim() === '') {
    fields.email = [emailRequiredText];
    messages.push(emailRequiredText);
  }
  if (password === '') {
    fields.password = [passwordRequiredText];
    messages.push(passwordRequiredText);
  }
  return messages.length === 0 ? null : { messages, fields };
}

/**
 * What a failed sign-in shows (design 16.1, 12.4). A refusal ("Invalid email or password.", "Account temporarily
 * locked. Try again later.", AC-34.3) is the API's `detail` word for word, with any `email` or `password` field
 * message also under its field. No answer is the F-2 text (AC-34.8). "Nothing was changed." is not shown: signing in
 * is not a change.
 */
function problemFrom(failure: ApiFailure): Problem {
  const errors = fieldErrors(failure);
  const fields: Partial<Record<FieldName, readonly string[]>> = {};
  if (errors.email !== undefined) {
    fields.email = errors.email;
  }
  if (errors.password !== undefined) {
    fields.password = errors.password;
  }
  // `session-ended` has no text of its own (failureText gives null). The sign-in request never carries a token, so
  // the API does not answer it `not-authenticated`; if it ever did, its message is this one (design 7.1).
  return { messages: [failureText(failure) ?? expiredText], fields };
}

/**
 * Design 16.1: the sign-in screen at `/sign-in`. A card with the product's name, the title, the message area, Email,
 * Password and "Sign in", and the footer line under it; no header or menu.
 *
 * - Arrival message (`role="status"`) from the `ended` value in the address (design 18.1).
 * - AC-34.3: blank fields are checked without a request; a refusal keeps the email, empties the password, and moves
 *   focus to the message area so a screen reader reads it (design 12.4).
 * - AC-34.8: no answer shows the F-2 text.
 * - F-3: while the request runs, the button is `aria-disabled` with the spinner, and further submits are ignored.
 * - AC-34.1, AC-34.2: after signing in here, the safe return path opens, or Stock.
 * - Design 12.3, architecture 17: a person already signed in who arrives at `/sign-in` goes to Stock, whatever its
 *   `return` value.
 */
export function SignInScreen() {
  // Hooks first, in the same order on every draw (React's rule for hooks), before any early return below.
  const session = useSession();
  // useSearchParams reads the query part of the address ("?ended=expired&return=/stock").
  const [searchParams] = useSearchParams();
  const destination = safeReturnPath(searchParams.get(returnParameter));
  const arrival = arrivalMessage(searchParams.get(endedParameter));

  // useState keeps a value between draws of this component and gives a function to change it; changing it draws the
  // component again. The fields are "controlled": React holds what is typed and the inputs show that value, so the
  // screen can keep the email and empty the password after a refusal (AC-34.3).
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [problem, setProblem] = useState<Problem | null>(null);
  const [busy, setBusy] = useState(false);

  // useRef keeps a value between draws without drawing again when it changes. Here: the page elements to move focus
  // to, and the F-3 busy flag. The flag is a ref as well as state because a state change only shows at the next draw;
  // two Enter presses in the same moment would both still see "not busy". The ref changes at once.
  const emailInput = useRef<HTMLInputElement>(null);
  const passwordInput = useRef<HTMLInputElement>(null);
  const messageArea = useRef<HTMLDivElement>(null);
  const sending = useRef(false);

  // useEffect runs code after React has put the screen on the page; the list at the end says when to run it again
  // ([] means only once, when the screen first appears). Design 16.1: focus starts in Email, or in Password when Email
  // is already filled (the browser's password manager may have filled it, row 83). The field's value on the page is
  // read, not the `email` state, because a password manager fills the page without telling React.
  useEffect(() => {
    if (emailInput.current !== null && emailInput.current.value !== '') {
      passwordInput.current?.focus();
    } else {
      emailInput.current?.focus();
    }
  }, []);

  // Design 12.4: after a refusal or a failed check, focus moves to the message area (it has tabIndex -1), so a screen
  // reader reads it at once. Each failure is a new object, so the same message twice still moves focus again.
  useEffect(() => {
    if (problem !== null) {
      messageArea.current?.focus();
    }
  }, [problem]);

  if (session !== null) {
    // Signed in. The session module's change draws this screen again, and `replace` swaps the sign-in entry, so Back
    // does not lead to the form again. Which address opens depends on how the session came to be here:
    // - AC-34.1, AC-34.2: this screen's own sign-in just succeeded (`busy` is still true, because a success never
    //   clears it): the safe return path opens, or Stock.
    // - Design 12.3, architecture 17: someone already signed in arrived at /sign-in (for example with Back, or a
    //   link inside the app): Stock opens, whatever the return value says.
    return <Navigate to={busy ? destination : homePath} replace />;
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    // A form's submit would normally load a new page; preventDefault stops that, so the request is sent from here.
    event.preventDefault();
    // F-3: one request per click or Enter; further submits while it runs are ignored.
    if (sending.current) {
      return;
    }
    const failedCheck = checkFields(email, password);
    if (failedCheck !== null) {
      setProblem(failedCheck);
      return;
    }

    sending.current = true;
    setBusy(true);
    const result = await signIn(email, password);
    if (result.ok) {
      // The session has started; this screen is already being replaced by the return path (above). `busy` is left
      // true on purpose: it is what tells that redraw that the sign-in happened here.
      return;
    }
    sending.current = false;
    setBusy(false);
    // AC-34.3, F-1, row 84: the email stays, the password is emptied after any refusal.
    setPassword('');
    setProblem(problemFrom(result.failure));
  }

  const emailErrors = problem?.fields.email;
  const passwordErrors = problem?.fields.password;

  return (
    <main className="signin" id="main">
      {/* React 19 moves a <title> drawn anywhere into the page's head: the browser tab's title (design 12.2). */}
      <title>{documentTitle('Sign in')}</title>
      <div className="signin__card">
        <p className="signin__brand">
          <span className="mark" aria-hidden="true"></span>
          {productName}
        </p>
        <h1 className="title title--gap">Sign in</h1>

        {problem !== null ? (
          // Design 16.1: a refusal replaces the arrival message, in the error style.
          <div className="msg msg--error" tabIndex={-1} ref={messageArea}>
            <span className="msg__icon" aria-hidden="true">
              !
            </span>
            <div className="msg__body">
              {problem.messages.map((message, index) => (
                <p key={index}>
                  <SafeText text={message} />
                </p>
              ))}
            </div>
          </div>
        ) : arrival !== null ? (
          // Design 16.1, 19: the arrival message is in the info style and is a "status" region, which a screen
          // reader announces without moving focus.
          <div className="msg msg--info" role="status" tabIndex={-1}>
            <span className="msg__icon" aria-hidden="true">
              i
            </span>
            <div className="msg__body">
              <p>{arrival}</p>
            </div>
          </div>
        ) : null}

        {/* noValidate: the browser's own check bubbles are off; the design's checks and texts are used instead. */}
        <form noValidate onSubmit={handleSubmit}>
          <div className="field">
            <label className="label" htmlFor="email">
              Email
            </label>
            <input
              className="input"
              id="email"
              name="email"
              type="email"
              autoComplete="username"
              spellCheck={false}
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              ref={emailInput}
              aria-invalid={emailErrors !== undefined ? true : undefined}
              aria-describedby={emailErrors !== undefined ? 'email-error' : undefined}
            />
            {emailErrors !== undefined ? <FieldError id="email-error" messages={emailErrors} /> : null}
          </div>
          <div className="field">
            <label className="label" htmlFor="password">
              Password
            </label>
            <input
              className="input"
              id="password"
              name="password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              ref={passwordInput}
              aria-invalid={passwordErrors !== undefined ? true : undefined}
              aria-describedby={passwordErrors !== undefined ? 'password-error' : undefined}
            />
            {passwordErrors !== undefined ? <FieldError id="password-error" messages={passwordErrors} /> : null}
          </div>
          {/*
            F-3, design 12.4: while busy the button is marked aria-disabled (not disabled, so it keeps focus and stays
            readable) and shows the 16 px spinner before its unchanged label, so it does not change size.
          */}
          <button className="btn btn--primary" type="submit" aria-disabled={busy ? true : undefined}>
            {busy ? <span className="spin" aria-hidden="true"></span> : null}
            Sign in
          </button>
        </form>
      </div>
      {/* F-8, design 12.2: the footer line, the sign-in screen included. */}
      <p className="footer">Times are local ({formatUtcOffset()}).</p>
    </main>
  );
}

/** Design 12.4: a field's message under it, tied to the field by `aria-describedby`. */
function FieldError({ id, messages }: { readonly id: string; readonly messages: readonly string[] }) {
  return (
    <p className="error" id={id}>
      <SafeText text={messages.join(' ')} />
    </p>
  );
}
