import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError, listPartyGuestChallenges, setPartyChallengeVote,
  type PartyGuestChallenges,
} from '@nubarca/api-client';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { useI18n } from '../i18n';

export function PartyChallengesPage() {
  const { token } = useParams<{ token: string }>();
  const { t } = useI18n();
  const [data, setData] = useState<PartyGuestChallenges | null>(null);
  const [error, setError] = useState<'unavailable' | 'error' | null>(null);
  const [voteError, setVoteError] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const kindLabel = (kind: 'dare' | 'penalty' | 'guess' | 'custom') => ({
    dare: t('partyChallenges.kind.dare'),
    penalty: t('partyChallenges.kind.penalty'),
    guess: t('partyChallenges.kind.guess'),
    custom: t('partyChallenges.kind.custom'),
  }[kind]);

  const load = useCallback((signal?: AbortSignal) => {
    if (!token) { setError('unavailable'); return; }
    listPartyGuestChallenges(token, signal).then(setData).catch((err: unknown) => {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setError(err instanceof ApiError && err.status === 404 ? 'unavailable' : 'error');
    });
  }, [token]);

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function toggle(id: string, voted: boolean) {
    if (!token) return;
    setBusy(id); setVoteError(false);
    try {
      const result = await setPartyChallengeVote(token, id, !voted);
      setData((current) => current && ({
        ...current, votesUsed: result.votesUsed, votesRemaining: result.votesRemaining,
        items: current.items.map((item) => item.id === id ? { ...item, voted: result.voted } : item),
      }));
    } catch { setVoteError(true); } finally { setBusy(null); }
  }

  if (error) return <main className="party-page"><div className="party-card"><h1>{t('partyChallenges.unavailable')}</h1></div></main>;
  if (!data) return <main className="party-page" aria-busy="true">
    <span className="visually-hidden">{t('common.loading')}</span>
    <div className="party-skeleton party-skeleton-hero" />
    <div className="party-skeleton party-skeleton-row" />
    <div className="party-skeleton party-skeleton-row" />
  </main>;

  return (
    <main className="party-page party-challenges-page">
      <header className="party-challenges-head">
        <div><Link to={`/party/${token}`}>{t('partyChallenges.back')}</Link>
          <h1>{t('partyChallenges.title')}</h1><p>{data.albumName}</p></div>
        <LanguageSwitcher className="language-switcher language-switcher-public" />
      </header>
      <div className="party-vote-budget" role="status">
        <strong>{data.votesRemaining}</strong> {t('partyChallenges.remaining')}
      </div>
      {voteError && <p className="party-status" role="alert">{t('partyChallenges.voteError')}</p>}
      {data.items.length === 0 ? <p className="party-status">{t('partyChallenges.empty')}</p> : (
        <ul className="party-challenge-list">
          {data.items.map((item) => (
            <li key={item.id} className={`party-challenge-card${item.voted ? ' is-voted' : ''}`}>
              {item.mediaUrl && <img src={item.mediaUrl} alt="" loading="lazy" />}
              <div className="party-challenge-copy">
                <span className="party-challenge-kind">{kindLabel(item.kind)}</span>
                <h2>{item.title}</h2><p>{item.body}</p>
                <button type="button" disabled={busy !== null || (!item.voted && data.votesRemaining === 0)}
                  aria-pressed={item.voted} onClick={() => void toggle(item.id, item.voted)}>
                  {item.voted ? t('partyChallenges.unvote') : t('partyChallenges.vote')}
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}
