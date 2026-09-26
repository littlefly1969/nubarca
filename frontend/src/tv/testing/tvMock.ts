import { installFetchMock, jsonResponse, type InstalledFetchMock } from '../../test-utils';

// A paired browser display talks to more of the TV API than any one test is
// about: the Personal Area status it checks before admitting a session, the
// heartbeat its control plane sends, and — on a party album — the greetings,
// the challenge hold and the face filter. This fills in the calm defaults for
// whatever a test did not state (a complete association, no greetings, no
// challenge, no face filter), so each test only has to say what it is about.

type Handlers = Parameters<typeof installFetchMock>[0];
type Handler = Handlers[string];

export const TV_SESSION = {
  status: 'active',
  expiresAt: '2026-08-05T12:00:00Z',
  lastSeenAt: '2026-07-05T12:00:00Z',
  language: 'it',
  assignment: { kind: 'general', albumId: null, albumName: null, partyAvailable: false, presentation: 'general' },
};

export const MEDIA_PLAYBACK = { mode: 'media', activeChallenge: null, nextChallengeAt: null, completedCount: 0 };

function defaultFor(key: string, handlers: Handlers): Handler | undefined {
  const [method, url = ''] = key.split(' ');
  const path = url.split('?')[0];
  if (method === 'POST' && path === '/api/tv/session/heartbeat') {
    return handlers['GET /api/tv/session'] ?? (() => jsonResponse(TV_SESSION));
  }
  if (method === 'GET' && path === '/api/tv/personal/status') {
    return () => jsonResponse({ pinConfigured: true, unlocked: false, scheme: 'dpad-v1' });
  }
  if (method === 'GET' && /^\/api\/tv\/albums\/[^/]+\/party-messages$/.test(path)) {
    return () => jsonResponse({ messages: [] });
  }
  if ((method === 'GET' && /^\/api\/tv\/albums\/[^/]+\/party-playback$/.test(path))
    || (method === 'POST' && /^\/api\/tv\/albums\/[^/]+\/party-playback\/boundary$/.test(path))) {
    return () => jsonResponse(MEDIA_PLAYBACK);
  }
  if (method === 'GET' && /^\/api\/tv\/albums\/[^/]+\/face-search\/active$/.test(path)) {
    return () => jsonResponse({
      active: false, searchId: null, activationVersion: null, activatedAt: null, faceThumbnailUrl: null, items: [],
    });
  }
  return undefined;
}

/** installFetchMock, with the display's background calls answered unless the test answers them. */
export function installTvMock(handlers: Handlers): InstalledFetchMock {
  const withDefaults = new Proxy(handlers, {
    get(target, property) {
      if (typeof property !== 'string') return undefined;
      if (property in target) return target[property];
      const [method, url = ''] = property.split(' ');
      const path = url.split('?')[0];
      // An explicit handler under another of the lookup's spellings wins.
      if (method !== '*' && (`${method} ${path}` in target || `* ${url}` in target || `* ${path}` in target)) {
        return undefined;
      }
      return method === '*' ? undefined : defaultFor(property, target);
    },
  });
  return installFetchMock(withDefaults);
}
