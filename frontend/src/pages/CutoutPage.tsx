import { useEffect, useState } from 'react';
import { listCutoutQueue, removeFromCutoutQueue, PERMISSIONS, type LithophaneQueueEntry } from '@nubarca/api-client';
import { usePermissions } from '../auth/usePermissions';
import { useI18n } from '../i18n';
import { SubjectCutoutDialog } from '../subjectCutout/SubjectCutoutDialog';
import { useSubjectCutoutSupport } from '../subjectCutout/useSubjectCutoutAvailable';

export function CutoutPage() {
  const { t } = useI18n();
  const perms = usePermissions();
  const { available } = useSubjectCutoutSupport(true);
  const [items, setItems] = useState<LithophaneQueueEntry[]>([]);
  const [selected, setSelected] = useState<LithophaneQueueEntry | null>(null);
  const [status, setStatus] = useState<'loading' | 'ready' | 'failed'>('loading');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const c = new AbortController();
    setStatus('loading');
    listCutoutQueue(c.signal).then(x => { if (!c.signal.aborted) { setItems(x); setStatus('ready'); } })
      .catch(() => { if (!c.signal.aborted) setStatus('failed'); });
    return () => c.abort();
  }, [attempt]);
  async function remove(id: string) {
    try { await removeFromCutoutQueue(id); setItems(old => old.filter(x => x.fileItemId !== id)); }
    catch { setStatus('failed'); }
  }
  return <section className="lab-cutout">
    <p>{t('labCutout.intro')}</p>
    {status === 'loading' && <p role="status">{t('subjectCutout.working')}</p>}
    {status === 'failed' && <p role="alert">{t('labCutout.error')} <button onClick={() => setAttempt(x => x + 1)}>{t('common.retry')}</button></p>}
    {status === 'ready' && !items.length && <p>{t('labCutout.empty')}</p>}
    {!available && <p>{t('subjectCutout.error.unavailable')}</p>}
    <div className="lab-cutout-queue">{items.map(item => <article key={item.fileItemId}>
      <button disabled={!available} onClick={() => setSelected(item)}><img src={item.thumbnailUrl} alt="" loading="lazy" /><span>{item.name}</span></button>
      <button onClick={() => void remove(item.fileItemId)}>{t('labCutout.remove')}</button>
    </article>)}</div>
    {selected && <SubjectCutoutDialog fileId={selected.fileItemId} fileName={selected.name} onClose={() => setSelected(null)}
      laboratory canSendToLithophanes={perms.has(PERMISSIONS.laboratoryLithophanes)} />}
  </section>;
}
