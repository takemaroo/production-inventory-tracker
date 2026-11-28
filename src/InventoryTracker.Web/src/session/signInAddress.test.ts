import { describe, expect, it } from 'vitest';
import {
  arrivalMessage,
  expiredText,
  idleText,
  passwordChangedText,
  safeReturnPath,
  signInAddress,
} from './signInAddress';

describe('safeReturnPath (design 12.3, architecture 15: no open redirect)', () => {
  // T-041 criterion "the return-path rule": a path on this site is opened after signing in (AC-34.1, AC-34.6).
  // A dot inside a segment, or anywhere in the query, is not a dot segment and is kept.
  it.each(['/production-orders/12', '/production-orders?status=Created&page=2', '/stock', '/', '/users/8b1c2f9e', '/a.b/c..d', '/stock?note=../x'])(
    'keeps the same-site path %j',
    (path) => {
      expect(safeReturnPath(path)).toBe(path);
    },
  );

  // T-041 criterion: "//evil.example", "/sign-in" and "https://..." all give /stock. The others are the doors a
  // browser would also read as another site (a backslash, a tab or line break inside the address) or the sign-in
  // screen in another spelling the router accepts.
  it.each([
    ['//evil.example', 'another site, written without its scheme'],
    ['https://evil.example/stock', 'a full address'],
    ['/sign-in', 'the sign-in screen'],
    ['/sign-in?return=/stock', 'the sign-in screen with a query'],
    ['/SIGN-IN', 'the sign-in screen in capitals'],
    ['/sign-in/', 'the sign-in screen with a trailing slash'],
    ['/\\evil.example', 'a backslash, which a browser reads as a slash'],
    [`/${String.fromCodePoint(0x09)}/evil.example`, 'a tab, which a browser drops'],
    [`/${String.fromCodePoint(0x0a)}/evil.example`, 'a line break, which a browser drops'],
    // T-041 review: dot segments, which a browser removes, so the rule does not depend on the router.
    ['/..//evil.example', 'a ".." segment that would leave "//evil.example"'],
    ['/./sign-in', 'a "." segment before the sign-in screen'],
    ['/sign-in/.', 'a "." segment after the sign-in screen'],
    ['/stock/../users', 'a ".." segment inside a path'],
    ['/%2e%2e//evil.example', 'a ".." segment written as "%2e%2e"'],
    ['/%2E/sign-in', 'a "." segment written as "%2E"'],
    ['stock', 'a relative path'],
    ['javascript:alert(1)', 'a script address'],
    ['', 'an empty value'],
  ])('gives /stock for %j (%s)', (value) => {
    expect(safeReturnPath(value)).toBe('/stock');
  });

  // AC-34.2: no return value at all opens Stock.
  it('gives /stock when there is no return value', () => {
    expect(safeReturnPath(null)).toBe('/stock');
  });
});

describe('signInAddress (architecture 15, table "Every end of a session")', () => {
  // AC-34.4: signing out opens the plain sign-in address, with no message.
  it('is /sign-in with nothing to carry', () => {
    expect(signInAddress()).toBe('/sign-in');
  });

  // AC-34.1, design 12.1: the address keeps the screen that was opened, readable as the design writes it.
  it('carries the return path with its slashes', () => {
    expect(signInAddress({ returnTo: '/production-orders/12' })).toBe('/sign-in?return=/production-orders/12');
  });

  // AC-34.1, AC-36.3: the screen's own query stays inside the one return value and adds no parameter of its own.
  it('escapes the return path query so it stays one value', () => {
    const address = signInAddress({ returnTo: '/production-orders?status=Created&page=2' });

    expect(address).toBe('/sign-in?return=/production-orders%3Fstatus%3DCreated%26page%3D2');
    const params = new URLSearchParams(address.split('?')[1]);
    expect([...params.keys()]).toEqual(['return']);
    expect(params.get('return')).toBe('/production-orders?status=Created&page=2');
  });

  // AC-34.6: a 401 ends at /sign-in?ended=expired&return=...
  it('puts ended before return', () => {
    expect(signInAddress({ ended: 'expired', returnTo: '/stock' })).toBe('/sign-in?ended=expired&return=/stock');
  });

  // AC-34.7 (T-046) and AC-35.1 (T-045) use the same builder.
  it('writes the idle and password-changed ends', () => {
    expect(signInAddress({ ended: 'idle' })).toBe('/sign-in?ended=idle');
    expect(signInAddress({ ended: 'password-changed' })).toBe('/sign-in?ended=password-changed');
  });

  // No open redirect: a path that would not be opened is not written into the address either.
  it('leaves out a return path that is not safe', () => {
    expect(signInAddress({ returnTo: '//evil.example' })).toBe('/sign-in');
    expect(signInAddress({ ended: 'expired', returnTo: '/sign-in' })).toBe('/sign-in?ended=expired');
  });
});

describe('arrivalMessage (design 16.1, 18.1)', () => {
  // AC-34.7, AC-34.6, AC-35.1: each end has its own fixed sentence, exactly as design 18.1 writes it.
  it('gives the design sentence for each end', () => {
    expect(arrivalMessage('idle')).toBe('You were signed out after 15 minutes without activity.');
    expect(arrivalMessage('expired')).toBe('Your sign-in is missing or has expired. Sign in again.');
    expect(arrivalMessage('password-changed')).toBe('Password changed. Sign in with your new password.');
    expect([idleText, expiredText, passwordChangedText]).toEqual([
      arrivalMessage('idle'),
      arrivalMessage('expired'),
      arrivalMessage('password-changed'),
    ]);
  });

  // The address cannot put words of its own on the screen: any other value shows no message (AC-34.4: none).
  it.each([null, '', 'Idle', 'expired ', 'You have won', 'signed-out'])('gives no message for %j', (value) => {
    expect(arrivalMessage(value)).toBeNull();
  });
});
