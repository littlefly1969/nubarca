import { useSyncExternalStore } from 'react';
import { useI18n } from '../i18n';
import type { MediaSelectionStore } from './mediaSelectionStore';

// What a bulk action started from the selection will actually touch.
//
// The selection belongs to the account, not to the screen: picks made in the
// Library, in another list or on another device are all in it. A confirmation
// shown over a list therefore says so, counts every file the action reaches,
// says how many of them are not on this screen, and shows the first few — so
// nobody trashes photos they cannot see while believing the action stops at
// the list in front of them.
const PREVIEW = 8;

export function SelectionScope({ store, ids, onScreen }: {
  store: MediaSelectionStore;
  // The exact ids the action will send (the caller's snapshot).
  ids: readonly string[];
  // What this view has loaded; ids outside it were picked elsewhere (or are
  // further down a view that has not loaded them yet).
  onScreen: ReadonlySet<string>;
}) {
  const { tn } = useI18n();
  const snapshot = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
  const elsewhere = ids.reduce((n, id) => (onScreen.has(id) ? n : n + 1), 0);
  const shown = ids.slice(0, PREVIEW);
  return (
    <div className="selection-scope" data-testid="selection-scope">
      <p className="ws-confirm-body" data-testid="selection-scope-count">
        {tn(ids.length, 'selectionScope.account')}
      </p>
      {elsewhere > 0 && (
        <p className="ws-confirm-body selection-scope__elsewhere" data-testid="selection-scope-elsewhere">
          {tn(elsewhere, 'selectionScope.elsewhere')}
        </p>
      )}
      <ul className="selection-scope__thumbs" aria-hidden="true">
        {shown.map((id) => {
          const url = snapshot.items.get(id)?.thumbnailUrl;
          return (
            <li key={id} data-testid="selection-scope-thumb">
              {url ? <img src={url} alt="" loading="lazy" decoding="async" /> : <span>🖼</span>}
            </li>
          );
        })}
        {ids.length > shown.length && (
          <li className="selection-scope__more" data-testid="selection-scope-more">+{ids.length - shown.length}</li>
        )}
      </ul>
    </div>
  );
}
