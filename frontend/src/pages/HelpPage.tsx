import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  askHelpAssistant,
  getHelpAssistantStatus,
  type HelpAssistantStatus,
  type HelpChatTurn,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// "Ask NubArca" — an optional assistant that explains the PRODUCT.
//
// It does not act on NubArca and it cannot see the library. The disclosure below
// says so in the words that are actually true, and there are now TWO of them,
// because the model can be on either side of the boundary:
//
//   external      the question IS sent to a third-party provider, and NubArca
//                 attaches nothing to it. Saying "no data leaves NubArca" would
//                 be simpler and false, since the user's own words leave by
//                 definition — and a privacy promise that is false in the easy
//                 case is worth nothing in the hard one.
//   localTrusted  the question is processed by an endpoint the operator
//                 declares as their own, and this version still answers from
//                 public product documentation only. It deliberately does NOT
//                 claim the endpoint has no internet egress: NubArca does not
//                 run that process and cannot prove it.
//
// Which one is shown comes from the server, never from the browser. There is
// deliberately no attach button, no "use current photo", no "use this album",
// no "use my search". Those are the features that would turn an explainer into
// a data pipeline.
const MAX_QUESTION = 2000;
const MAX_HISTORY_TURNS = 8;

interface Turn {
  fromUser: boolean;
  text: string;
  sources?: string[];
}

export function HelpPage() {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<HelpAssistantStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [question, setQuestion] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const endRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const s = await getHelpAssistantStatus();
        if (!cancelled) setStatus(s);
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (!cancelled) {
          setStatus({
            enabled: false, providerLabel: '',
            knowledgeAvailable: false, modelBoundary: 'external',
          });
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [invalidateAuth]);

  // `?.` guards a null ref, not a missing method: scrollIntoView is absent in
  // some environments, and a convenience scroll is not worth throwing over.
  useEffect(() => {
    const end = endRef.current;
    if (typeof end?.scrollIntoView === 'function') end.scrollIntoView({ block: 'end' });
  }, [turns]);

  const ask = useCallback(async () => {
    const text = question.trim();
    if (text.length === 0 || busy) return;
    setBusy(true);
    setError(null);
    setQuestion('');
    // The conversation lives HERE, in the browser, and a bounded slice of it
    // rides with each request. NubArca stores no help conversations: a new
    // permanent category of user data is not something a help feature should
    // create on its own.
    const history: HelpChatTurn[] = turns
      .slice(-MAX_HISTORY_TURNS)
      .map((turn) => ({ fromUser: turn.fromUser, text: turn.text }));
    setTurns((prev) => [...prev, { fromUser: true, text }]);
    try {
      const answer = await askHelpAssistant(text, history);
      if (answer.ok) {
        setTurns((prev) => [...prev, { fromUser: false, text: answer.text, sources: answer.sources }]);
      } else {
        setError(t(reasonKey(answer.reason)));
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(t('help.ai.errorUnavailable'));
    } finally {
      setBusy(false);
    }
  }, [question, busy, turns, t, invalidateAuth]);

  if (loading) return <p className="muted">{t('common.loading')}</p>;

  return (
    <section className="help-ai" aria-label={t('help.ai.title')}>
      <header className="help-ai-header">
        <h1>{t('help.ai.title')}</h1>
        {status?.enabled && (
          <span
            className={
              status.modelBoundary === 'localTrusted'
                ? 'help-ai-badge help-ai-badge--local'
                : 'help-ai-badge'
            }
            title={status.providerLabel}
          >
            {t(status.modelBoundary === 'localTrusted'
              ? 'help.ai.localBadge'
              : 'help.ai.externalBadge')}
          </span>
        )}
      </header>

      {!status?.enabled ? (
        // Disabled is a normal state, not an error: an installation that never
        // configured a provider should read as "not set up", and the rest of the
        // product Help stays exactly as usable as before.
        <p className="muted">{t('help.ai.disabled')}</p>
      ) : !status.knowledgeAvailable ? (
        // Configured, but with no approved product knowledge for this release.
        // The server refuses to call the provider in this state, so offering a
        // composer would invite a question that is guaranteed to fail — and the
        // message says what a person can act on without naming a corpus path, a
        // revision or a configuration value.
        <p className="muted">{t('help.ai.knowledgeUnavailable')}</p>
      ) : (
        <>
          <p className="help-ai-privacy">
            {t(
              status.modelBoundary === 'localTrusted' ? 'help.ai.privacyLocal' : 'help.ai.privacy',
              { provider: status.providerLabel },
            )}
          </p>

          <div className="help-ai-thread" role="log" aria-live="polite">
            {turns.length === 0 && <p className="muted">{t('help.ai.empty')}</p>}
            {turns.map((turn, i) => (
              <div
                key={i}
                className={turn.fromUser ? 'help-ai-turn help-ai-turn--user' : 'help-ai-turn'}
              >
                <span className="help-ai-role">
                  {turn.fromUser ? t('help.ai.you') : status.providerLabel}
                </span>
                <p>{turn.text}</p>
                {turn.sources && turn.sources.length > 0 && (
                  <p className="help-ai-sources">
                    {t('help.ai.sources', { sources: turn.sources.join(', ') })}
                  </p>
                )}
              </div>
            ))}
            <div ref={endRef} />
          </div>

          {error && <p className="error">{error}</p>}

          <form
            className="help-ai-composer"
            onSubmit={(e) => { e.preventDefault(); void ask(); }}
          >
            <label htmlFor="help-ai-question">{t('help.ai.questionLabel')}</label>
            <textarea
              id="help-ai-question"
              value={question}
              maxLength={MAX_QUESTION}
              rows={3}
              placeholder={t('help.ai.placeholder')}
              onChange={(e) => setQuestion(e.target.value)}
            />
            <button type="submit" disabled={busy || question.trim().length === 0}>
              {busy ? t('help.ai.asking') : t('help.ai.ask')}
            </button>
          </form>
        </>
      )}
    </section>
  );
}

/// Sanitized server reasons → copy. Anything unrecognised falls back to the
/// generic message rather than being rendered raw.
function reasonKey(reason: string): Parameters<ReturnType<typeof useI18n>['t']>[0] {
  switch (reason) {
    case 'provider_unauthorized': return 'help.ai.errorProvider';
    case 'provider_rate_limited': return 'help.ai.errorBusy';
    case 'provider_timeout': return 'help.ai.errorTimeout';
    case 'help_knowledge_unavailable': return 'help.ai.knowledgeUnavailable';
    // Distinct from the one above: the corpus is fine, and nothing in it
    // answers this. Nobody has anything to fix, so the copy asks for a
    // rephrasing instead of naming an administrator.
    case 'help_no_supporting_knowledge': return 'help.ai.noKnowledge';
    case 'help_disabled':
    case 'help_not_configured': return 'help.ai.disabled';
    default: return 'help.ai.errorUnavailable';
  }
}
