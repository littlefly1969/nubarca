import { useRef, useState } from 'react';
import {
  GalleryInterpretError,
  interpretGalleryCommand,
  type GalleryInterpretResponse,
  type GalleryPersonAmbiguity,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { LOCALE, useI18n, type MessageKey } from '../../i18n';
import { applyInterpretDraft, type GalleryQuery } from '../galleryQuery';

// Describe mode: a natural-language command → the LOCAL interpreter → a validated
// draft that POPULATES the same draft query the manual editor edits. It never
// applies the gallery directly (the sheet's Apply does), never exposes raw model
// output/prompts/errors, and cannot let a stale (late) parser response overwrite
// newer draft edits — the caller supplies a monotonic edit sequence we capture
// at request time and re-check on response. Ambiguous people are resolved here
// (inside the sheet) and folded into the draft's include/exclude before Apply.
interface Props {
  draft: GalleryQuery;
  initialCommand?: string;
  // Apply the resolved (non-ambiguous) interpreter draft to the sheet's draft.
  onResolvedDraft(next: GalleryQuery): void;
  // Add one resolved-ambiguity person into the draft include/exclude arrays.
  onAddPerson(mode: 'include' | 'exclude', personId: string): void;
  // Monotonic edit sequence of the sheet's draft (bumps on every manual edit).
  getSeq(): number;
  onUnresolvedChange(hasUnresolved: boolean): void;
  onSwitchToManual(): void;
  announce(message: string): void;
}

const ERROR_KEYS: Record<GalleryInterpretError['kind'], MessageKey> = {
  busy: 'gallery.command.errBusy',
  unavailable: 'gallery.command.errUnavailable',
  timeout: 'gallery.command.errTimeout',
  failed: 'gallery.command.errFailed',
  unsupported: 'gallery.command.errFailed',
  auth: 'gallery.command.errFailed',
};

export function NaturalFilterEditor({
  draft,
  initialCommand,
  onResolvedDraft,
  onAddPerson,
  getSeq,
  onUnresolvedChange,
  onSwitchToManual,
  announce,
}: Props) {
  const { t, lang } = useI18n();
  const { invalidateAuth } = useAuth();
  const [command, setCommand] = useState(initialCommand ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [ambiguities, setAmbiguities] = useState<GalleryPersonAmbiguity[]>([]);
  const [roundTripMs, setRoundTripMs] = useState<number | null>(null);
  const reqIdRef = useRef(0);
  const abortRef = useRef<AbortController | null>(null);

  async function run() {
    const text = command.trim();
    if (text.length === 0 || busy) return;
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    const reqId = ++reqIdRef.current;
    const startSeq = getSeq(); // draft version at send time
    setBusy(true);
    setError(null);
    const startedAt = performance.now();
    try {
      const res: GalleryInterpretResponse = await interpretGalleryCommand(
        {
          command: text,
          locale: LOCALE[lang],
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          currentDate: new Date().toISOString(),
          currentFilters: {
            // Refine/clear commands are computed server-side against these.
            peopleInclude: draft.includePeople,
            peopleExclude: draft.excludePeople,
            peopleMatch: draft.includePeopleMode,
            favorite: draft.favorite,
            minRating: draft.minRating,
            hasGps: draft.hasGps,
            dateTakenFrom: draft.dateTakenFrom.length > 0 ? draft.dateTakenFrom : null,
            dateTakenTo: draft.dateTakenTo.length > 0 ? draft.dateTakenTo : null,
            collapseDuplicates: draft.collapseDuplicates,
            sort: draft.sort,
            sortDirection: draft.direction,
            metadataSearch: draft.metadataQuery.length > 0 ? draft.metadataQuery : null,
            semanticQuery: draft.visualQuery.length > 0 ? draft.visualQuery : null,
          },
        },
        controller.signal,
      );
      setRoundTripMs(Math.round(performance.now() - startedAt));
      // Supersede + stale-edit guard: ignore this response if a newer request
      // was issued OR the user changed the draft while we were waiting.
      if (reqId !== reqIdRef.current) return;
      if (startSeq !== getSeq()) return;

      onResolvedDraft(applyInterpretDraft(draft, res.draft));
      setAmbiguities(res.ambiguities);
      onUnresolvedChange(res.ambiguities.length > 0);
      announce(t('gallery.ws.sr.interpreted'));
      // Stay on Describe while ambiguities need resolving (their chooser lives
      // here); only reveal the populated Manual fields once there is nothing
      // left to disambiguate.
      if (res.ambiguities.length === 0) onSwitchToManual();
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (reqId !== reqIdRef.current) return;
      if (err instanceof GalleryInterpretError && err.kind === 'auth') {
        invalidateAuth();
        return;
      }
      const kind = err instanceof GalleryInterpretError ? err.kind : 'failed';
      setError(t(ERROR_KEYS[kind]));
    } finally {
      if (reqId === reqIdRef.current) setBusy(false);
    }
  }

  function chooseCandidate(ambiguity: GalleryPersonAmbiguity, personId: string) {
    onAddPerson(ambiguity.mode, personId);
    const remaining = ambiguities.filter((a) => a !== ambiguity);
    setAmbiguities(remaining);
    onUnresolvedChange(remaining.length > 0);
    if (remaining.length === 0) onSwitchToManual(); // reveal the populated fields
  }

  return (
    <div className="ws-describe">
      <p className="ws-help">{t('gallery.ws.describeHelp')}</p>
      <label className="visually-hidden" htmlFor="ws-describe-input">{t('gallery.ws.nlAria')}</label>
      <textarea
        id="ws-describe-input"
        className="ws-textarea"
        data-testid="ws-describe-input"
        rows={3}
        maxLength={512}
        placeholder={t('gallery.ws.describePlaceholder')}
        value={command}
        onChange={(e) => setCommand(e.target.value)}
      />
      <div className="ws-describe-actions">
        <button
          type="button"
          className="row-action-primary"
          data-testid="ws-describe-run"
          onClick={run}
          disabled={busy || command.trim().length === 0}
        >
          {busy ? t('gallery.command.interpreting') : t('gallery.ws.describeRun')}
        </button>
        {roundTripMs !== null && (
          <span className="ws-help" data-testid="gallery-command-timing">
            {t('gallery.command.roundTrip', { ms: roundTripMs })}
          </span>
        )}
      </div>
      {error !== null && (
        <p className="ws-error" role="alert" data-testid="ws-describe-error">{error}</p>
      )}
      {ambiguities.length > 0 && (
        <div className="ws-ambiguities" data-testid="ws-ambiguities">
          {ambiguities.map((a) => (
            <fieldset key={`${a.mode}:${a.text}`} className="ws-ambiguity">
              <legend>{t('gallery.command.whichPerson', { name: a.text })}</legend>
              <div className="ws-ambiguity-options">
                {a.candidates.map((c) => (
                  <button
                    key={c.personId}
                    type="button"
                    className="row-action"
                    onClick={() => chooseCandidate(a, c.personId)}
                  >
                    {c.name ?? t('peopleFilter.unnamed')} ({c.faceCount})
                  </button>
                ))}
              </div>
            </fieldset>
          ))}
        </div>
      )}
    </div>
  );
}
