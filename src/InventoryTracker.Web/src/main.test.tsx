import { describe, expect, it, vi } from 'vitest';

// T-039 review hand-over 1: the session module must be connected to the request function when the app starts,
// before any request can go out. Requests are only sent by what App draws, so "before App is first drawn" is the
// moment that matters. This test replaces App with a stand-in that notes, when React first draws it, whether
// startSession has already run, and wraps the real startSession so its calls can be counted.

// vi.hoisted runs before the vi.mock calls below (which Vitest moves to the top of the file), so both can use it.
const seen = vi.hoisted(() => ({ startCallsWhenAppFirstDrawn: undefined as number | undefined }));

// vi.mock swaps a module for this test file. The session module stays the real one; only startSession is wrapped.
vi.mock('./session/session', async (importOriginal) => {
  const real = await importOriginal<typeof import('./session/session')>();
  return { ...real, startSession: vi.fn(real.startSession) };
});

vi.mock('./app/App', async () => {
  const session = await import('./session/session');
  return {
    App: () => {
      seen.startCallsWhenAppFirstDrawn ??= vi.mocked(session.startSession).mock.calls.length;
      return null;
    },
  };
});

describe('src/main.tsx start-up (T-039 review hand-over 1)', () => {
  // AC-34.6 depends on it: the session is started once, with the real page loader, before App is first drawn.
  it('starts the session before the first draw', async () => {
    const root = document.createElement('div');
    root.id = 'root';
    document.body.append(root);
    const session = await import('./session/session');

    await import('./main');

    await vi.waitFor(() => expect(seen.startCallsWhenAppFirstDrawn).toBe(1));
    expect(session.startSession).toHaveBeenCalledOnce();
    // No options: the real loader, window.location.replace (AC-34.4).
    expect(session.startSession).toHaveBeenCalledWith();
  });
});
