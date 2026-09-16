import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router';
import {
  ApiError,
  getPartyInvitation,
  selfCheckInPartyGuest,
  submitPartyRsvp,
  undoSelfCheckInPartyGuest,
  type PartyGuestContentKind,
  type PartyInvitationViewModel,
  type PartyRsvpWrite,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { isOpenablePoster } from '../party/PartyGuestContent';
import { PartyBeforeHome } from '../party/PartyGuestSurfaces';
import { PartyHubTopBar } from '../party/PartyHubTopBar';
import { PartyImageViewer } from '../party/PartyImageViewer';
import {
  PartyRsvpSheet, PartyRsvpSummary, partyRsvpAnswered, type PartyRsvpNotice,
} from '../party/PartyRsvpCard';
import { PartySelfCheckInCard } from '../party/PartySelfCheckInCard';
import './PartyGuestHub.css';

// A PERSONAL INVITATION — the same party, reached by one group's own link.
//
// Not a second invitation app. The page is the party's own invitation surface
// (`PartyBeforeHome`: its cover, its title and date, the host's slots in their
// order) with this group's reply composed into it. What it deliberately is not
// is the party itself: the link it was opened with grants no upload, game,
// print, greeting or face search, so none of those are drawn in any phase —
// they are not part of what this capability opens, and the server would refuse
// them. It sets no participant cookie either.
//
// THE REPLY IS NOT THE FIRST THING ON IT. The form used to sit between the
// cover and the host's words, so every guest met a radio group before they had
// read what they were invited to. It lives in a sheet now, opened from one
// button pinned to the bottom edge, and what stays on the page is the answer
// once there is one — at the END, where a reply belongs.
//
// While the party is live the group can say "Sono qui" for its own people, and
// "Entra nel Party" leads to the party's ordinary public page — navigation to
// the same capability the room's QR opens, never an identity carried across.
//
// No login, no polling: an invitation is read, answered, and put away.

type State =
  | { kind: 'loading' }
  | { kind: 'ready'; view: PartyInvitationViewModel }
  | { kind: 'unavailable' }
  | { kind: 'error' };

export function PartyInvitationPage() {
  const { token } = useParams<{ token: string }>();
  const { t } = useI18n();
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<PartyRsvpNotice | null>(null);
  const [poster, setPoster] = useState<PartyGuestContentKind | null>(null);
  const [checkingIn, setCheckingIn] = useState<string | null>(null);
  const [checkInNotice, setCheckInNotice] = useState<MessageKey | null>(null);
  const [replying, setReplying] = useState(false);

  const load = useCallback((signal?: AbortSignal) => {
    if (!token) { setState({ kind: 'unavailable' }); return; }
    setState({ kind: 'loading' });
    getPartyInvitation(token, signal)
      .then((view) => setState({ kind: 'ready', view }))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setState(err instanceof ApiError && err.status === 404 ? { kind: 'unavailable' } : { kind: 'error' });
      });
  }, [token]);

  useEffect(() => {
    const ctrl = new AbortController();
    load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  const submit = useCallback(async (reply: PartyRsvpWrite) => {
    if (!token) return;
    setSaving(true);
    setNotice(null);
    try {
      const view = await submitPartyRsvp(token, reply);
      setState({ kind: 'ready', view });
      setNotice({ tone: 'ok', messageKey: 'partyRsvp.saved' });
      // Sent: the sheet has done its job and gets out of the way. A REFUSAL
      // leaves it open, because what the guest typed is still in it.
      setReplying(false);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // A refusal that describes a state carries it: the invitation as it is
        // now. It is adopted, and the guest is told why the form changed.
        const body = err.body as { error?: string; invitation?: PartyInvitationViewModel } | null;
        if (body?.invitation) setState({ kind: 'ready', view: body.invitation });
        setNotice({
          tone: 'error',
          messageKey: body?.error === 'rsvp_closed' ? 'partyRsvp.error.closed' : 'partyRsvp.error.conflict',
        });
      } else if (err instanceof ApiError && err.status === 404) {
        setState({ kind: 'unavailable' });
      } else {
        // Nothing adopted, nothing lost: the draft stays exactly as typed.
        setNotice({ tone: 'error', messageKey: 'partyRsvp.error.generic' });
      }
    } finally {
      setSaving(false);
    }
  }, [token]);

  // "Sono qui" and its undo. The answer is the invitation as it now is, in
  // success and refusal alike — the version does not move, so the reply card
  // keeps whatever it was showing.
  const checkIn = useCallback(async (guestId: string, undo: boolean) => {
    if (!token) return;
    setCheckingIn(guestId);
    setCheckInNotice(null);
    try {
      const view = undo
        ? await undoSelfCheckInPartyGuest(token, guestId)
        : await selfCheckInPartyGuest(token, guestId);
      setState({ kind: 'ready', view });
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        const body = err.body as { error?: string; invitation?: PartyInvitationViewModel } | null;
        if (body?.invitation) setState({ kind: 'ready', view: body.invitation });
        setCheckInNotice(body?.error === 'attendance_recorded_by_host'
          ? 'partyRsvp.checkIn.error.byHost'
          : 'partyRsvp.checkIn.error.closed');
      } else if (err instanceof ApiError && err.status === 404) {
        setState({ kind: 'unavailable' });
      } else {
        setCheckInNotice('partyRsvp.checkIn.error.generic');
      }
    } finally {
      setCheckingIn(null);
    }
  }, [token]);

  if (state.kind === 'loading') {
    return (
      <main className="party-guest-hub" aria-busy="true">
        <p className="visually-hidden" role="status">{t('common.loading')}</p>
      </main>
    );
  }
  if (state.kind === 'unavailable' || state.kind === 'error') {
    return (
      <main className="party-guest-hub">
        <div className="party-guest-hub-state-page">
          <PartyHubTopBar />
          <div className="party-guest-hub-state">
            <h1>{state.kind === 'unavailable' ? t('partyRsvp.unavailableTitle') : t('party.errorTitle')}</h1>
            <p role="alert">{state.kind === 'unavailable' ? t('partyRsvp.unavailableBody') : t('party.errorBody')}</p>
            {state.kind === 'error' && (
              <button className="party-guest-hub-retry" type="button" onClick={() => load()}>
                {t('common.tryAgain')}
              </button>
            )}
          </div>
        </div>
      </main>
    );
  }

  const { party, invitation } = state.view;
  const posterSlot = poster ? party.content.find((s) => s.kind === poster && isOpenablePoster(s)) : undefined;
  const eyebrow = party.phase === 'live'
    ? t('partyRsvp.eyebrow.live')
    : party.phase === 'after' ? t('partyRsvp.eyebrow.after') : undefined;

  // Nothing on the page until there is something to say: before a reply exists
  // the invitation is only an invitation, and the one thing it asks for is the
  // button pinned to the bottom of it.
  const answered = partyRsvpAnswered(invitation);
  const showSummary = answered || !invitation.canRespond;

  return (
    <>
      <main className="party-guest-hub" data-testid="party-invitation" data-phase={party.phase}>
        <PartyBeforeHome
          context={party}
          topBar={<PartyHubTopBar />}
          eyebrow={eyebrow}
          footnote={t('partyRsvp.footnote')}
          onOpenPoster={setPoster}
          after={showSummary && (
            <PartyRsvpSummary
              invitation={invitation}
              phase={party.phase}
              notice={replying ? null : notice}
              onChange={invitation.canRespond ? () => setReplying(true) : undefined}
            />
          )}
        >
          {/* While the party is on, arriving comes before everything: it is the
              one thing a guest standing at a door needs. */}
          {invitation.canCheckIn && (
            <PartySelfCheckInCard
              invitation={invitation}
              partyUrl={party.partyUrl}
              busyGuestId={checkingIn}
              notice={checkInNotice}
              onCheckIn={(guestId) => void checkIn(guestId, false)}
              onUndo={(guestId) => void checkIn(guestId, true)}
            />
          )}
        </PartyBeforeHome>
      </main>

      {/* The invitation's one call to action, pinned where a thumb is. It is
          drawn only while there is nothing to show at the end — once the group
          has replied, changing that reply is the summary's own button and a
          second one here would be the same door twice. */}
      {invitation.canRespond && !answered && (
        <div className="party-invitation-cta" data-testid="party-rsvp-open-bar">
          <button
            type="button"
            className="party-invitation-cta-button"
            data-testid="party-rsvp-open"
            onClick={() => setReplying(true)}
          >
            {t('partyRsvp.openSheet')}
          </button>
        </div>
      )}

      {/* Keyed by the SERVER's reply: a save or an adopted conflict is a fresh
          draft, and a failed send — same version — keeps what the guest typed. */}
      {replying && invitation.canRespond && (
        <PartyRsvpSheet
          key={`${invitation.version}`}
          invitation={invitation}
          saving={saving}
          notice={notice}
          onSubmit={(reply) => void submit(reply)}
          onClose={() => setReplying(false)}
        />
      )}

      {posterSlot?.mediaUrl && (
        <PartyImageViewer src={posterSlot.mediaUrl} label={t('party.photoViewer')} onClose={() => setPoster(null)} />
      )}
    </>
  );
}
