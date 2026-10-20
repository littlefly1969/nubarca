import { useEffect, useRef, useSyncExternalStore } from 'react';
import { useI18n } from '../i18n';
import type { MediaSelectionStore } from './mediaSelectionStore';

// The tray: what is selected, wherever it was picked.
//
// The dock only states a count; the tray shows the picks themselves, in the
// order they were made, each removable on its own. It reads the same store as
// the grid and the viewer, so removing a photo here unticks its tile at once.
// A pick made on another screen whose details this browser has not seen yet
// still appears (as a placeholder) — the selection is the ids, not the
// thumbnails.
export function SelectionTray({ store, onClose }: { store: MediaSelectionStore; onClose(): void }) {
  const { t } = useI18n();
  const snapshot = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
  const closeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => { closeRef.current?.focus(); }, []);
  useEffect(() => {
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [onClose]);

  return (
    <div className="selection-tray" role="dialog" aria-modal="false" aria-label={t('gallerySel.trayTitle')}
      data-testid="selection-tray">
      <div className="selection-tray__head">
        <h3>{t('gallerySel.trayTitle')}</h3>
        <button ref={closeRef} type="button" className="selection-tray__close" aria-label={t('gallerySel.trayClose')}
          onClick={onClose}>✕</button>
      </div>
      {snapshot.ids.length === 0
        ? <p className="muted">{t('gallerySel.trayEmpty')}</p>
        : (
          <ul className="selection-tray__list">
            {snapshot.ids.map((id) => {
              const item = snapshot.items.get(id);
              const name = item?.displayName ?? t('gallerySel.trayUnknown');
              return (
                <li key={id} className="selection-tray__item" data-testid="selection-tray-item">
                  {item?.thumbnailUrl
                    ? <img src={item.thumbnailUrl} alt="" loading="lazy" decoding="async" />
                    : <span className="selection-tray__placeholder" aria-hidden="true">🖼</span>}
                  <span className="selection-tray__name" title={name}>{name}</span>
                  <button type="button" className="selection-tray__remove" data-testid="selection-tray-remove"
                    aria-label={t('gallerySel.trayRemove', { name })}
                    onClick={() => store.remove([id])}>✕</button>
                </li>
              );
            })}
          </ul>
        )}
    </div>
  );
}
