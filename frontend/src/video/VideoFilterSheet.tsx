import { useEffect, useRef, useState } from 'react';
import type { AlbumMembership } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { AlbumMembershipFilter } from '../media/filters/AlbumMembershipFilter';

// Video-gallery metadata filters (ffprobe-derived): duration range, minimum
// resolution, codec, audio presence — plus favorite / rating / capture-date,
// mirroring the photo gallery's manual filters. There is NO free-text search:
// video has no semantic search and a filename substring match was deliberately
// dropped (see the product decision). Codec options are data-driven (distinct
// codecs owned by the user).

export interface VideoFilterState {
  durationMinMin: number | null; // minutes
  durationMaxMin: number | null; // minutes
  minResolution: 'any' | '720' | '1080' | '2160';
  codec: string; // '' = any
  audio: 'any' | 'with' | 'without';
  favorite: boolean;
  minRating: number; // 0 = any
  dateFrom: string; // yyyy-mm-dd, '' = any
  dateTo: string;
  // Same shared album-organisation filter as the photo gallery.
  albumMembership: AlbumMembership;
}

export const EMPTY_VIDEO_FILTERS: VideoFilterState = {
  durationMinMin: null,
  durationMaxMin: null,
  minResolution: 'any',
  codec: '',
  audio: 'any',
  favorite: false,
  minRating: 0,
  dateFrom: '',
  dateTo: '',
  albumMembership: 'any',
};

export function isEmptyVideoFilters(f: VideoFilterState): boolean {
  return (
    f.durationMinMin === null &&
    f.durationMaxMin === null &&
    f.minResolution === 'any' &&
    f.codec === '' &&
    f.audio === 'any' &&
    !f.favorite &&
    f.minRating === 0 &&
    f.dateFrom === '' &&
    f.dateTo === '' &&
    f.albumMembership === 'any'
  );
}

interface Props {
  open: boolean;
  applied: VideoFilterState;
  codecs: string[];
  onApply(next: VideoFilterState): void;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

export function VideoFilterSheet({ open, applied, codecs, onApply, onClose, returnFocusRef }: Props) {
  const { t } = useI18n();
  const [draft, setDraft] = useState<VideoFilterState>(applied);
  const dialogRef = useRef<HTMLDivElement>(null);

  // Re-seed the draft from the applied set each time the sheet opens.
  useEffect(() => {
    if (open) setDraft(applied);
  }, [open, applied]);

  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  useEffect(() => {
    if (!open && returnFocusRef?.current) returnFocusRef.current.focus();
  }, [open, returnFocusRef]);

  if (!open) return null;

  const set = <K extends keyof VideoFilterState>(key: K, value: VideoFilterState[K]) =>
    setDraft((d) => ({ ...d, [key]: value }));

  return (
    <div className="ws-sheet-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div
        ref={dialogRef}
        className="ws-sheet"
        role="dialog"
        aria-modal="true"
        aria-label={t('videoFilters.title')}
      >
        <header className="ws-sheet-head">
          <h3 className="ws-sheet-title">{t('videoFilters.title')}</h3>
          <button type="button" className="ws-icon-button" aria-label={t('common.close')} onClick={onClose}>✕</button>
        </header>

        <form
          className="ws-sheet-body"
          onSubmit={(e) => { e.preventDefault(); onApply(draft); }}
        >
          <fieldset className="ws-field">
            <legend>{t('videoFilters.duration')}</legend>
            <div className="ws-range">
              <label>
                {t('videoFilters.min')}
                <input
                  type="number" min={0} inputMode="numeric"
                  value={draft.durationMinMin ?? ''}
                  onChange={(e) => set('durationMinMin', e.target.value === '' ? null : Math.max(0, Number(e.target.value)))}
                />
              </label>
              <span aria-hidden="true">–</span>
              <label>
                {t('videoFilters.max')}
                <input
                  type="number" min={0} inputMode="numeric"
                  value={draft.durationMaxMin ?? ''}
                  onChange={(e) => set('durationMaxMin', e.target.value === '' ? null : Math.max(0, Number(e.target.value)))}
                />
              </label>
              <span className="muted">{t('videoFilters.minutes')}</span>
            </div>
          </fieldset>

          <label className="ws-field">
            <span>{t('videoFilters.minResolution')}</span>
            <select value={draft.minResolution}
              onChange={(e) => set('minResolution', e.target.value as VideoFilterState['minResolution'])}>
              <option value="any">{t('videoFilters.any')}</option>
              <option value="720">≥ 720p</option>
              <option value="1080">≥ 1080p</option>
              <option value="2160">≥ 4K</option>
            </select>
          </label>

          <label className="ws-field">
            <span>{t('videoFilters.codec')}</span>
            <select value={draft.codec} onChange={(e) => set('codec', e.target.value)}>
              <option value="">{t('videoFilters.any')}</option>
              {codecs.map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
          </label>

          <label className="ws-field">
            <span>{t('videoFilters.audio')}</span>
            <select value={draft.audio}
              onChange={(e) => set('audio', e.target.value as VideoFilterState['audio'])}>
              <option value="any">{t('videoFilters.any')}</option>
              <option value="with">{t('videoFilters.withAudio')}</option>
              <option value="without">{t('videoFilters.withoutAudio')}</option>
            </select>
          </label>

          <label className="ws-field ws-check">
            <input type="checkbox" checked={draft.favorite}
              onChange={(e) => set('favorite', e.target.checked)} />
            <span>{t('videoFilters.favorite')}</span>
          </label>

          <label className="ws-field">
            <span>{t('videoFilters.minRating')}</span>
            <select value={draft.minRating} onChange={(e) => set('minRating', Number(e.target.value))}>
              <option value={0}>{t('videoFilters.any')}</option>
              {[1, 2, 3, 4, 5].map((r) => <option key={r} value={r}>{'★'.repeat(r)}</option>)}
            </select>
          </label>

          <fieldset className="ws-field">
            <legend>{t('videoFilters.dateRange')}</legend>
            <div className="ws-range">
              <label>
                {t('videoFilters.from')}
                <input type="date" value={draft.dateFrom} onChange={(e) => set('dateFrom', e.target.value)} />
              </label>
              <label>
                {t('videoFilters.to')}
                <input type="date" value={draft.dateTo} onChange={(e) => set('dateTo', e.target.value)} />
              </label>
            </div>
          </fieldset>

          <AlbumMembershipFilter
            value={draft.albumMembership}
            onChange={(albumMembership) => set('albumMembership', albumMembership)}
          />

          <footer className="ws-sheet-foot">
            <button type="button" className="row-action" onClick={() => setDraft(EMPTY_VIDEO_FILTERS)}>
              {t('videoFilters.reset')}
            </button>
            <div className="ws-sheet-foot-right">
              <button type="submit" className="row-action-primary">{t('videoFilters.apply')}</button>
            </div>
          </footer>
        </form>
      </div>
    </div>
  );
}
