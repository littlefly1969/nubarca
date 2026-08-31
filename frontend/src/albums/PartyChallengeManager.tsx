import { useEffect, useState } from 'react';
import {
  createPartyChallenge, deletePartyChallenge, listAlbumItems, listPartyChallenges,
  reorderPartyChallenges, updatePartyChallenge,
  type AlbumItemSummary, type PartyChallenge, type PartyChallengeKind,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';

const EMPTY = {
  title: '', body: '', kind: 'dare' as PartyChallengeKind,
  mediaFileItemId: null as string | null, isEnabled: true,
};

export function PartyChallengeManager({ albumId }: { albumId: string }) {
  const { t } = useI18n();
  const [items, setItems] = useState<PartyChallenge[]>([]);
  const [media, setMedia] = useState<AlbumItemSummary[]>([]);
  const [draft, setDraft] = useState(EMPTY);
  const [editing, setEditing] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const selectedMedia = media.find((x) => x.fileItemId === draft.mediaFileItemId);

  const refresh = async () => {
    const [deck, members] = await Promise.all([listPartyChallenges(albumId), listAlbumItems(albumId)]);
    setItems(deck.items);
    setMedia(members.filter((x) => x.thumbnailUrl));
  };
  useEffect(() => { void refresh().catch(() => setError(true)); }, [albumId]);

  const edit = (item: PartyChallenge) => {
    setEditing(item.id);
    setDraft({
      title: item.title, body: item.body, kind: item.kind,
      mediaFileItemId: item.mediaFileItemId, isEnabled: item.isEnabled,
    });
  };
  const reset = () => { setEditing(null); setDraft(EMPTY); };
  const save = async () => {
    if (!draft.title.trim() || !draft.body.trim()) return;
    setBusy(true); setError(false);
    try {
      if (editing) await updatePartyChallenge(albumId, editing, draft);
      else await createPartyChallenge(albumId, draft);
      reset(); await refresh();
    } catch { setError(true); } finally { setBusy(false); }
  };
  const remove = async (id: string) => {
    if (!window.confirm(t('partyGame.deleteConfirm'))) return;
    setBusy(true);
    try { await deletePartyChallenge(albumId, id); await refresh(); }
    catch { setError(true); } finally { setBusy(false); }
  };
  const move = async (at: number, delta: -1 | 1) => {
    const next = [...items];
    const to = at + delta;
    if (to < 0 || to >= next.length) return;
    [next[at], next[to]] = [next[to], next[at]];
    setItems(next);
    try { await reorderPartyChallenges(albumId, next.map((x) => x.id)); }
    catch { setError(true); await refresh(); }
  };

  return (
    <section className="party-game-manager" data-testid="party-challenge-manager">
      <h4>{t('partyGame.deckTitle')}</h4>
      <p className="muted">{t('partyGame.deckHelp')}</p>
      <div className="party-game-editor">
        <label>{t('partyGame.challengeTitle')}<input maxLength={100} value={draft.title}
          onChange={(e) => setDraft((x) => ({ ...x, title: e.target.value }))} /></label>
        <label>{t('partyGame.challengeBody')}<textarea maxLength={500} value={draft.body}
          onChange={(e) => setDraft((x) => ({ ...x, body: e.target.value }))} /></label>
        <label>{t('partyGame.kind')}<select value={draft.kind}
          onChange={(e) => setDraft((x) => ({ ...x, kind: e.target.value as PartyChallengeKind }))}>
          <option value="dare">{t('partyChallenges.kind.dare')}</option>
          <option value="penalty">{t('partyChallenges.kind.penalty')}</option>
          <option value="guess">{t('partyChallenges.kind.guess')}</option>
          <option value="custom">{t('partyChallenges.kind.custom')}</option>
        </select></label>
        <label>{t('partyGame.photo')}<select value={draft.mediaFileItemId ?? ''}
          onChange={(e) => setDraft((x) => ({ ...x, mediaFileItemId: e.target.value || null }))}>
          <option value="">{t('partyGame.noPhoto')}</option>
          {media.map((x) => <option key={x.fileItemId} value={x.fileItemId}>{x.name}</option>)}
        </select></label>
        <label className="album-tv-label"><input type="checkbox" checked={draft.isEnabled}
          onChange={(e) => setDraft((x) => ({ ...x, isEnabled: e.target.checked }))} />
          <span>{t('partyGame.enabled')}</span></label>
        <div className="party-game-editor-actions">
          <button type="button" disabled={busy || !draft.title.trim() || !draft.body.trim()} onClick={() => void save()}>
            {editing ? t('partyGame.update') : t('partyGame.add')}
          </button>
          {editing && <button type="button" onClick={reset}>{t('common.cancel')}</button>}
        </div>
        {(draft.title.trim() || draft.body.trim()) && (
          <div className="party-game-preview-wrap">
            <strong>{t('partyGame.preview')}</strong>
            <div className="party-game-tv-preview" data-testid="party-game-tv-preview">
              {selectedMedia?.thumbnailUrl && <img src={selectedMedia.thumbnailUrl} alt="" />}
              <div>
                <span>{t(`partyChallenges.kind.${draft.kind}`)}</span>
                <h5>{draft.title || t('partyGame.challengeTitle')}</h5>
                <p>{draft.body}</p>
                <small>{t('partyGame.continueHint')}</small>
              </div>
            </div>
          </div>
        )}
      </div>
      {error && <p className="inline-error" role="alert">{t('partyGame.error')}</p>}
      {items.length === 0 ? <p className="muted">{t('partyGame.empty')}</p> : (
        <ol className="party-game-owner-list">
          {items.map((item, at) => (
            <li key={item.id}>
              {item.mediaUrl && <img src={item.mediaUrl} alt="" />}
              <div><strong>{item.title}</strong><p>{item.body}</p>
                <small>{item.voteCount} {t('partyGame.votes')} · {item.isEnabled ? t('partyGame.on') : t('partyGame.off')}</small></div>
              <div className="party-game-row-actions">
                <button type="button" aria-label={t('partyGame.moveUp')} disabled={at === 0 || busy} onClick={() => void move(at, -1)}>↑</button>
                <button type="button" aria-label={t('partyGame.moveDown')} disabled={at === items.length - 1 || busy} onClick={() => void move(at, 1)}>↓</button>
                <button type="button" disabled={busy} onClick={() => edit(item)}>{t('partyGame.edit')}</button>
                <button type="button" disabled={busy} onClick={() => void remove(item.id)}>{t('common.delete')}</button>
              </div>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
