import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { FormEvent, KeyboardEvent as ReactKeyboardEvent } from 'react';
import {
  addTvPersonalItemsToDestination,
  addTvPersonalItemsToAlbum,
  fetchTvPersonalMediaObjectUrl,
  getTvPersonalMediaInfo,
  interpretTvPersonalGalleryCommand,
  listTvPersonalAlbums,
  listTvPersonalGallery,
  listTvPersonalPeople,
  setTvPersonalFavorite,
  trashTvPersonalGalleryItems,
  TvInterpretError,
  type TvCurrentFilterState,
  type TvGallerySortDirection,
  type TvGallerySortField,
  type TvInterpretDraft,
  type TvInterpretErrorKind,
  type TvInterpretResponse,
  type TvPersonalAlbum,
  type TvPersonalGalleryItem,
  type TvPersonalGalleryBulkResult,
  type TvPersonalGalleryDestination,
  type TvPersonalGalleryQuery,
  type TvPersonalMediaInfo,
  type TvPersonalPerson,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';

// /tv Personal Gallery — the browser-fallback equivalent of the native TV
// gallery: the SAME grant-gated projection endpoints, the same filters/sort/
// search/cursor semantics (authoritative on the backend), remote/keyboard-
// driven focus. Media bytes require the unlock grant HEADER, so thumbnails and
// previews are fetched explicitly and rendered as object URLs (never plain
// <img src> — the header cannot ride an image tag). The grant lives only in
// props/state; nothing is persisted.
//
// The grid is controlled exclusively by `applied`; the full-screen workspace
// edits an isolated `draft` and commits it only on Apply. Native TV implements
// the same state machine with remote-first RN controls.
const PAGE_SIZE = 50;
const TOAST_MS = 4000;

export interface AppliedQuery {
  q: string;
  sort: TvGallerySortField;
  direction: TvGallerySortDirection;
  favorite: boolean | null;
  minRating: number | null;
  hasGps: boolean | null;
  dateFrom: string; // 'YYYY-MM-DD' or ''
  dateTo: string;
  collapseDuplicates: boolean;
  includePeople: string[];
  excludePeople: string[];
  includePeopleMode: 'all' | 'any';
  // Slice 100: visual semantic residual + server-clamped Top-K (0 = none).
  semanticQuery: string;
  semanticTopK: number;
}

export const EMPTY_QUERY: AppliedQuery = {
  q: '',
  sort: 'created',
  direction: 'desc',
  favorite: null,
  minRating: null,
  hasGps: null,
  dateFrom: '',
  dateTo: '',
  collapseDuplicates: false,
  includePeople: [],
  excludePeople: [],
  includePeopleMode: 'all',
  semanticQuery: '',
  semanticTopK: 0,
};

function cloneQuery(query: AppliedQuery): AppliedQuery {
  return {
    ...query,
    includePeople: [...query.includePeople],
    excludePeople: [...query.excludePeople],
  };
}

function validDateRange(query: AppliedQuery): boolean {
  return query.dateFrom === '' || query.dateTo === '' || query.dateFrom <= query.dateTo;
}

type LoadPhase =
  | { kind: 'loadingInitial' }
  | { kind: 'ready' }
  | { kind: 'loadingMore' }
  | { kind: 'end' }
  | { kind: 'errorInitial' }
  | { kind: 'errorMore' };

export function toWireQuery(applied: AppliedQuery, cursor: string | null): TvPersonalGalleryQuery {
  return {
    q: applied.q.length > 0 ? applied.q : undefined,
    sort: applied.sort,
    direction: applied.direction,
    limit: PAGE_SIZE,
    cursor: cursor ?? undefined,
    favorite: applied.favorite ?? undefined,
    minRating: applied.minRating ?? undefined,
    hasGps: applied.hasGps ?? undefined,
    // Whole-day UTC bounds, matching the native TV client's date semantics.
    dateTakenFrom: applied.dateFrom.length > 0 ? `${applied.dateFrom}T00:00:00Z` : undefined,
    dateTakenTo: applied.dateTo.length > 0 ? `${applied.dateTo}T23:59:59Z` : undefined,
    collapseDuplicates: applied.collapseDuplicates || undefined,
    includePeople: applied.includePeople.length > 0 ? applied.includePeople : undefined,
    excludePeople: applied.excludePeople.length > 0 ? applied.excludePeople : undefined,
    includePeopleMode: applied.includePeople.length > 0 ? applied.includePeopleMode : undefined,
    semanticQuery: applied.semanticQuery.length > 0 ? applied.semanticQuery : undefined,
    semanticTopK: applied.semanticQuery.length > 0 ? applied.semanticTopK : undefined,
  };
}

// Maps a validated interpret draft into the applied query. For 'refine' it
// starts from the current state (the server already merged, but we mirror the
// draft's complete target state); for 'replace'/'clear' it starts from empty.
// Dates arrive as ISO instants and are narrowed to 'YYYY-MM-DD'.
export function draftToApplied(draft: TvInterpretDraft): AppliedQuery {
  if (draft.operation === 'clear') return { ...EMPTY_QUERY };
  const day = (iso: string | null): string => (iso ? iso.slice(0, 10) : '');
  const sort = (draft.sort as TvGallerySortField | null) ?? 'created';
  const direction = (draft.sortDirection as TvGallerySortDirection | null)
    ?? (draft.semanticQuery ? 'desc' : 'desc');
  return {
    q: draft.metadataSearch ?? '',
    sort,
    direction,
    favorite: draft.favorite,
    minRating: draft.minRating,
    hasGps: draft.hasGps,
    dateFrom: day(draft.dateTakenFrom),
    dateTo: day(draft.dateTakenTo),
    collapseDuplicates: draft.collapseDuplicates ?? false,
    includePeople: [...draft.peopleInclude],
    excludePeople: [...draft.peopleExclude],
    includePeopleMode: draft.peopleMatch,
    semanticQuery: draft.semanticQuery ?? '',
    semanticTopK: draft.semanticTopK ?? 0,
  };
}

// Builds the current-filter-state payload the interpreter needs for refine/clear.
function toCurrentFilterState(applied: AppliedQuery): TvCurrentFilterState {
  return {
    peopleInclude: applied.includePeople,
    peopleExclude: applied.excludePeople,
    peopleMatch: applied.includePeopleMode,
    favorite: applied.favorite,
    minRating: applied.minRating,
    hasGps: applied.hasGps,
    dateTakenFrom: applied.dateFrom ? `${applied.dateFrom}T00:00:00Z` : null,
    dateTakenTo: applied.dateTo ? `${applied.dateTo}T23:59:59Z` : null,
    collapseDuplicates: applied.collapseDuplicates,
    sort: applied.sort,
    sortDirection: applied.direction,
    metadataSearch: applied.q.length > 0 ? applied.q : null,
    semanticQuery: applied.semanticQuery.length > 0 ? applied.semanticQuery : null,
  };
}

// Human-readable, localized summary of a proposed draft for the confirmation
// dialog. Pure (exported for tests). Only includes the dimensions the draft sets.
export function draftSummaryLines(
  draft: TvInterpretDraft,
  peopleNames: string[],
  L: (it: string, en: string) => string,
): string[] {
  if (draft.operation === 'clear') {
    return [L('Azzera tutti i filtri', 'Clear all filters')];
  }
  const lines: string[] = [];
  if (peopleNames.length > 0 || draft.peopleInclude.length > 0 || draft.peopleExclude.length > 0) {
    const join = draft.peopleMatch === 'any' ? L(' o ', ' or ') : L(' e ', ' and ');
    const inc = peopleNames.length > 0 ? peopleNames.join(join) : String(draft.peopleInclude.length);
    let label = `${L('Persone', 'People')}: ${inc}`;
    if (draft.peopleExclude.length > 0) label += ` (${L('senza', 'without')} ${draft.peopleExclude.length})`;
    lines.push(label);
  }
  if (draft.dateTakenFrom || draft.dateTakenTo) {
    const from = draft.dateTakenFrom ? draft.dateTakenFrom.slice(0, 10) : '…';
    const to = draft.dateTakenTo ? draft.dateTakenTo.slice(0, 10) : '…';
    lines.push(`${L('Periodo', 'Period')}: ${from} → ${to}`);
  }
  if (draft.favorite === true) lines.push(L('Solo preferite', 'Favorites only'));
  if (draft.minRating != null) lines.push(`${L('Valutazione', 'Rating')}: ★ ${draft.minRating}+`);
  if (draft.hasGps === true) lines.push(L('Con posizione', 'With location'));
  if (draft.hasGps === false) lines.push(L('Senza posizione', 'Without location'));
  if (draft.collapseDuplicates === true) lines.push(L('Senza duplicati', 'No duplicates'));
  if (draft.metadataSearch) lines.push(`${L('Testo', 'Text')}: ${draft.metadataSearch}`);
  if (draft.semanticQuery) lines.push(`${L('Contenuto', 'Content')}: ${draft.semanticQuery}`);
  if (draft.sort) {
    lines.push(`${L('Ordine', 'Sort')}: ${draft.sort} ${draft.sortDirection ?? ''}`.trim());
  }
  if (draft.semanticQuery && draft.semanticTopK > 0) {
    lines.push(L(`Migliori ${draft.semanticTopK} risultati`, `Best ${draft.semanticTopK} results`));
  }
  if (lines.length === 0) lines.push(L('Tutte le foto', 'All photos'));
  return lines;
}

export function activeFilterCount(applied: AppliedQuery): number {
  let count = 0;
  if (applied.q.length > 0) count += 1;
  if (applied.semanticQuery.trim().length > 0) count += 1;
  if (applied.favorite !== null) count += 1;
  if (applied.minRating !== null) count += 1;
  if (applied.hasGps !== null) count += 1;
  if (applied.dateFrom !== '' || applied.dateTo !== '') count += 1;
  if (applied.collapseDuplicates) count += 1;
  if (applied.includePeople.length > 0) count += 1;
  if (applied.excludePeople.length > 0) count += 1;
  return count;
}

// Bounded object-URL cache for the grant-gated media bytes. Owned by the
// gallery root and revoked wholesale on unmount (lock/mode-exit/unpair all
// unmount the gallery, so no personal bytes outlive the session state).
export class PersonalMediaCache {
  private readonly urls = new Map<string, string>();
  private readonly inflight = new Map<string, Promise<string>>();
  private readonly maxEntries = 300;
  private closed = false;
  private generation = 0;

  load(grant: string, mediaUrl: string): Promise<string> {
    const hit = this.urls.get(mediaUrl);
    if (hit !== undefined) return Promise.resolve(hit);
    const pending = this.inflight.get(mediaUrl);
    if (pending !== undefined) return pending;
    const generation = this.generation;
    const task = fetchTvPersonalMediaObjectUrl(grant, mediaUrl)
      .then((objectUrl) => {
        this.inflight.delete(mediaUrl);
        // A download that completes AFTER the gallery unmounted must not leak
        // its object URL into a cache nobody will ever revoke.
        if (this.closed || generation !== this.generation) {
          URL.revokeObjectURL(objectUrl);
          return objectUrl;
        }
        this.urls.set(mediaUrl, objectUrl);
        // Bounded: evict the oldest object URLs beyond the cap.
        while (this.urls.size > this.maxEntries) {
          const oldest = this.urls.keys().next().value as string;
          const evicted = this.urls.get(oldest);
          this.urls.delete(oldest);
          if (evicted) URL.revokeObjectURL(evicted);
        }
        return objectUrl;
      })
      .catch((err: unknown) => {
        this.inflight.delete(mediaUrl);
        throw err;
      });
    this.inflight.set(mediaUrl, task);
    return task;
  }

  revoke(mediaUrl: string): void {
    const objectUrl = this.urls.get(mediaUrl);
    if (objectUrl) URL.revokeObjectURL(objectUrl);
    this.urls.delete(mediaUrl);
    this.inflight.delete(mediaUrl);
  }

  clear(): void {
    this.generation += 1;
    for (const objectUrl of this.urls.values()) URL.revokeObjectURL(objectUrl);
    this.urls.clear();
    this.inflight.clear();
  }

  dispose(): void {
    this.closed = true;
    this.clear();
  }
}

// One authenticated personal image (thumbnail or preview) rendered from an
// object URL. Failures render the shared placeholder.
function PersonalImg({
  grant,
  cache,
  mediaUrl,
  className,
  alt,
  onAuthError,
}: {
  grant: string;
  cache: PersonalMediaCache;
  mediaUrl: string;
  className: string;
  alt: string;
  onAuthError: (err: unknown) => boolean;
}) {
  const [src, setSrc] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setSrc(null);
    setFailed(false);
    cache.load(grant, mediaUrl)
      .then((objectUrl) => {
        if (!cancelled) setSrc(objectUrl);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        onAuthError(err);
        setFailed(true);
      });
    return () => {
      cancelled = true;
    };
  }, [grant, cache, mediaUrl, onAuthError]);

  if (failed) {
    return <span className={`${className} tv-personal-thumb-fallback`} aria-hidden="true">🖼</span>;
  }
  if (src === null) {
    return <span className={`${className} tv-personal-thumb-fallback`} aria-hidden="true" />;
  }
  return <img className={className} src={src} alt={alt} onError={() => setFailed(true)} />;
}

interface TvPersonalGalleryProps {
  grant: string;
  // BACK from the gallery root → Personal Area home (normal navigation, no lock).
  onBack: () => void;
  // Shared 401/403 handling from TvPairedExperience (session invalid / lock /
  // pin_changed). Returns true when the error was an auth teardown.
  onPersonalError: (err: unknown) => boolean;
}

export function TvPersonalGallery({ grant, onBack, onPersonalError }: TvPersonalGalleryProps) {
  const { t, tn, lang } = useI18n();
  const L = useCallback((it: string, en: string) => (lang === 'it' ? it : en), [lang]);
  const [applied, setApplied] = useState<AppliedQuery>(EMPTY_QUERY);
  const [draft, setDraft] = useState<AppliedQuery>(() => cloneQuery(EMPTY_QUERY));
  const [items, setItems] = useState<TvPersonalGalleryItem[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  // Server-authoritative total for the current query (null until page 1 lands).
  // The viewer counter denominator — never items.length. Reset on every new
  // query; set by the first page; kept stable while later pages append.
  const [totalCount, setTotalCount] = useState<number | null>(null);
  const [phase, setPhase] = useState<LoadPhase>({ kind: 'loadingInitial' });
  const [showFilters, setShowFilters] = useState(false);
  const [workspaceMode, setWorkspaceMode] = useState<'describe' | 'manual'>('describe');
  const [menuOpen, setMenuOpen] = useState(false);
  const [selectionLayer, setSelectionLayer] = useState<'none' | 'album' | 'destination' | 'trash'>('none');
  const [bulkBusy, setBulkBusy] = useState(false);
  const [people, setPeople] = useState<TvPersonalPerson[] | null>(null);
  const [selection, setSelection] = useState<string[] | null>(null);
  const [albums, setAlbums] = useState<TvPersonalAlbum[] | null>(null);
  const [selectedAlbumId, setSelectedAlbumId] = useState('');
  const [albumBusy, setAlbumBusy] = useState(false);
  const [toast, setToast] = useState<string | null>(null);
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);

  // Natural-language command flow. All local; the command is kept only in this
  // component state and is cleared on apply/cancel — never persisted.
  const [nlCommand, setNlCommand] = useState('');
  const [nlBusy, setNlBusy] = useState(false);
  const [nlResult, setNlResult] = useState<TvInterpretResponse | null>(null);
  const [nlError, setNlError] = useState<string | null>(null);
  // Chosen person id per ambiguous span text (before Apply).
  const [nlChoices, setNlChoices] = useState<Record<string, string>>({});
  const draftRevisionRef = useRef(0);
  const workspaceApplyingRef = useRef(false);
  const menuRestoreIdRef = useRef<string | null>(null);

  const cacheRef = useRef<PersonalMediaCache | null>(null);
  if (cacheRef.current === null) cacheRef.current = new PersonalMediaCache();
  const cache = cacheRef.current;
  // Revoke every personal object URL when the gallery unmounts (lock/exit).
  useEffect(() => () => cache.dispose(), [cache]);

  const gridRef = useRef<HTMLDivElement>(null);
  const loadingRef = useRef(false);
  const generationRef = useRef(0);
  const itemsRef = useRef(items);
  itemsRef.current = items;
  const nextCursorRef = useRef(nextCursor);
  nextCursorRef.current = nextCursor;
  const phaseRef = useRef(phase);
  phaseRef.current = phase;

  const handleError = useCallback((err: unknown): boolean => {
    if (err instanceof DOMException && err.name === 'AbortError') return true;
    return onPersonalError(err);
  }, [onPersonalError]);

  // Initial page: (applied) is the query identity; every change bumps the
  // generation, clears the accumulator and the selection, and fetches page 1.
  // Stale responses are ignored.
  useEffect(() => {
    const ctrl = new AbortController();
    cache.clear();
    generationRef.current += 1;
    const gen = generationRef.current;
    loadingRef.current = true;
    setItems([]);
    setNextCursor(null);
    setTotalCount(null);
    setViewerIndex(null);
    setSelection((cur) => (cur !== null ? [] : null));
    setPhase({ kind: 'loadingInitial' });
    listTvPersonalGallery(grant, toWireQuery(applied, null), ctrl.signal)
      .then((page) => {
        if (gen !== generationRef.current) return;
        setItems(page.items);
        setNextCursor(page.nextCursor);
        setTotalCount(page.totalCount);
        // Generic, non-technical notice when a semantic query cannot fully run.
        if (page.semanticActive === true && page.semanticStatus === 'unavailable') {
          setToast(L('Il motore di ricerca locale non è disponibile.',
            'The local search engine is unavailable.'));
        } else if (page.semanticActive === true && page.semanticStatus === 'indexing') {
          setToast(L('Le foto filtrate non sono ancora disponibili per la ricerca semantica.',
            'The filtered photos are not yet available for semantic search.'));
        }
        setPhase(page.nextCursor !== null ? { kind: 'ready' } : { kind: 'end' });
      })
      .catch((err: unknown) => {
        if (gen !== generationRef.current) return;
        if (handleError(err)) return;
        setPhase({ kind: 'errorInitial' });
      })
      .finally(() => {
        if (gen === generationRef.current) loadingRef.current = false;
      });
    return () => ctrl.abort();
  }, [grant, applied, handleError, cache]);

  const loadMore = useCallback(() => {
    if (loadingRef.current) return;
    const cursor = nextCursorRef.current;
    if (cursor === null) return;
    const current = phaseRef.current.kind;
    if (current !== 'ready' && current !== 'errorMore') return;
    const gen = generationRef.current;
    loadingRef.current = true;
    setPhase({ kind: 'loadingMore' });
    listTvPersonalGallery(grant, toWireQuery(applied, cursor))
      .then((page) => {
        if (gen !== generationRef.current) return;
        setItems((prev) => {
          const seen = new Set(prev.map((it) => it.id));
          return [...prev, ...page.items.filter((it) => !seen.has(it.id))];
        });
        setNextCursor(page.nextCursor);
        setPhase(page.nextCursor !== null ? { kind: 'ready' } : { kind: 'end' });
      })
      .catch((err: unknown) => {
        if (gen !== generationRef.current) return;
        if (handleError(err)) return;
        setPhase({ kind: 'errorMore' });
      })
      .finally(() => {
        if (gen === generationRef.current) loadingRef.current = false;
      });
  }, [grant, applied, handleError]);

  // Infinite scroll sentinel (same pattern as the web gallery).
  const loadMoreRef = useRef(loadMore);
  loadMoreRef.current = loadMore;
  const observerRef = useRef<IntersectionObserver | null>(null);
  const sentinelRef = useCallback((node: HTMLDivElement | null) => {
    observerRef.current?.disconnect();
    observerRef.current = null;
    if (node && typeof IntersectionObserver !== 'undefined') {
      const observer = new IntersectionObserver((entries) => {
        if (entries.some((e) => e.isIntersecting)) loadMoreRef.current();
      }, { rootMargin: '600px 0px' });
      observer.observe(node);
      observerRef.current = observer;
    }
  }, []);
  useEffect(() => () => observerRef.current?.disconnect(), []);

  // Toast auto-dismiss.
  useEffect(() => {
    if (toast === null) return;
    const timer = window.setTimeout(() => setToast(null), TOAST_MS);
    return () => window.clearTimeout(timer);
  }, [toast]);

  // People options for the filter panel (loaded once when it opens).
  useEffect(() => {
    if (!showFilters || people !== null) return;
    const ctrl = new AbortController();
    listTvPersonalPeople(grant, ctrl.signal)
      .then(setPeople)
      .catch((err: unknown) => {
        if (!handleError(err)) setPeople([]);
      });
    return () => ctrl.abort();
  }, [showFilters, people, grant, handleError]);

  // Albums for selection mode (loaded when selection starts).
  useEffect(() => {
    if (selection === null || albums !== null) return;
    const ctrl = new AbortController();
    listTvPersonalAlbums(grant, ctrl.signal)
      .then((list) => {
        setAlbums(list);
        if (list.length > 0) setSelectedAlbumId(list[0].id);
      })
      .catch((err: unknown) => {
        if (!handleError(err)) setAlbums([]);
      });
    return () => ctrl.abort();
  }, [selection, albums, grant, handleError]);

  const openWorkspace = useCallback(() => {
    workspaceApplyingRef.current = false;
    setDraft(cloneQuery(applied));
    draftRevisionRef.current += 1;
    setNlCommand('');
    setNlResult(null);
    setNlChoices({});
    setNlError(null);
    setWorkspaceMode('describe');
    setMenuOpen(false);
    setShowFilters(true);
  }, [applied]);

  const updateDraft = useCallback((next: Partial<AppliedQuery>) => {
    draftRevisionRef.current += 1;
    setDraft((prev) => ({ ...prev, ...next }));
  }, []);

  const resetDraft = useCallback(() => {
    draftRevisionRef.current += 1;
    setDraft(cloneQuery(EMPTY_QUERY));
    setNlCommand('');
    setNlResult(null);
    setNlChoices({});
    setNlError(null);
  }, []);

  const cancelWorkspace = useCallback(() => {
    draftRevisionRef.current += 1;
    setDraft(cloneQuery(applied));
    setNlResult(null);
    setNlChoices({});
    setNlError(null);
    setShowFilters(false);
    window.requestAnimationFrame(() => {
      const id = menuRestoreIdRef.current;
      const target = id
        ? gridRef.current?.querySelector<HTMLElement>(`[data-item-id="${CSS.escape(id)}"]`)
        : gridRef.current?.querySelector<HTMLElement>('[data-tile]');
      target?.focus();
    });
  }, [applied]);

  const nlErrorMessage = useCallback((kind: TvInterpretErrorKind): string => {
    switch (kind) {
      case 'busy':
        return L('Il motore di ricerca locale è occupato. Riprova tra poco.',
          'The local search engine is busy. Try again shortly.');
      case 'timeout':
        return L('Interpretazione troppo lenta. Riprova o usa i filtri manuali.',
          'Interpretation timed out. Try again or use the manual filters.');
      case 'unavailable':
        return L('Il motore di ricerca locale non è disponibile.',
          'The local search engine is unavailable.');
      case 'unsupported':
        return L('Non sono riuscito a interpretare la richiesta. Modifica la frase oppure usa i filtri manuali.',
          "I couldn't interpret that request. Rephrase it or use the manual filters.");
      case 'auth':
        return L('Sessione scaduta. Riprova.', 'Session expired. Please try again.');
      default:
        return L('Non sono riuscito a interpretare la richiesta.', "I couldn't interpret that request.");
    }
  }, [L]);

  // Interpret the typed command WITHOUT changing current filters. On failure the
  // current gallery stays exactly as-is (only nlError is set).
  const runInterpret = useCallback(async () => {
    const command = nlCommand.trim();
    if (command.length === 0) return;
    setNlBusy(true);
    setNlError(null);
    setNlResult(null);
    setNlChoices({});
    const revision = draftRevisionRef.current;
    try {
      const result = await interpretTvPersonalGalleryCommand(grant, {
        command,
        locale: lang === 'it' ? 'it-IT' : 'en-US',
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
        currentDate: new Date().toISOString(),
        currentFilters: toCurrentFilterState(draft),
      });
      if (revision !== draftRevisionRef.current) return;
      setDraft(draftToApplied(result.draft));
      setNlResult(result);
      draftRevisionRef.current += 1;
    } catch (err) {
      const kind = err instanceof TvInterpretError ? err.kind : 'failed';
      setNlError(nlErrorMessage(kind));
    } finally {
      setNlBusy(false);
    }
  }, [nlCommand, grant, lang, draft, nlErrorMessage]);

  const applyWorkspace = useCallback(() => {
    if (workspaceApplyingRef.current || !validDateRange(draft) || nlBusy) return;
    const next = cloneQuery(draft);
    if (nlResult !== null) {
      for (const ambiguity of nlResult.ambiguities) {
        const chosen = nlChoices[ambiguity.text];
        if (!chosen) return;
        if (ambiguity.mode === 'exclude') {
          if (!next.excludePeople.includes(chosen)) next.excludePeople.push(chosen);
        } else if (!next.includePeople.includes(chosen)) {
          next.includePeople.push(chosen);
        }
      }
    }
    workspaceApplyingRef.current = true;
    cache.clear();
    setApplied(next);
    setNlResult(null);
    setNlCommand('');
    setNlChoices({});
    setNlError(null);
    setShowFilters(false);
  }, [draft, nlBusy, nlResult, nlChoices, cache]);

  const cancelDraft = useCallback(() => {
    setNlResult(null);
    setNlCommand('');
    setNlChoices({});
    setNlError(null);
  }, []);

  const nlUnresolved = nlResult !== null
    && nlResult.ambiguities.some((a) => !nlChoices[a.text]);

  const toggleFavorite = useCallback(async (id: string, favorite: boolean) => {
    try {
      const result = await setTvPersonalFavorite(grant, id, favorite);
      const favFilter = applied.favorite;
      if (favFilter !== null && result.favorite !== favFilter) {
        // The toggle dropped the item OUT of the current filtered set (e.g.
        // un-favoriting while favoritesOnly is active). Remove it and decrement
        // the authoritative total so the counter never goes stale; the viewer's
        // clamped index moves to the next valid item (or the grid takes over
        // when none remain — see the viewer guard below).
        setItems((prev) => prev.filter((it) => it.id !== result.id));
        setTotalCount((prev) => (prev === null ? prev : Math.max(0, prev - 1)));
      } else {
        setItems((prev) => prev.map((it) => (
          it.id === result.id ? { ...it, favorite: result.favorite } : it
        )));
      }
      return true;
    } catch (err) {
      handleError(err);
      return false;
    }
  }, [grant, handleError, applied.favorite]);

  const cyclePersonState = useCallback((personId: string) => {
    draftRevisionRef.current += 1;
    setDraft((prev) => {
      const included = prev.includePeople.includes(personId);
      const excluded = prev.excludePeople.includes(personId);
      if (!included && !excluded) {
        return { ...prev, includePeople: [...prev.includePeople, personId] };
      }
      if (included) {
        return {
          ...prev,
          includePeople: prev.includePeople.filter((x) => x !== personId),
          excludePeople: [...prev.excludePeople, personId],
        };
      }
      return { ...prev, excludePeople: prev.excludePeople.filter((x) => x !== personId) };
    });
  }, []);

  const addSelectionToAlbum = useCallback(async () => {
    if (selection === null || selection.length === 0 || selectedAlbumId === '') return;
    setAlbumBusy(true);
    try {
      const result = await addTvPersonalItemsToAlbum(grant, selectedAlbumId, selection);
      const album = albums?.find((a) => a.id === selectedAlbumId);
      setToast(t('tvGallery.albumAdded', {
        added: result.succeeded,
        skipped: result.skipped,
        album: album?.name ?? '',
      }));
      setSelection(result.skipped > 0 ? selection : null);
      setSelectionLayer('none');
    } catch (err) {
      if (!handleError(err)) setToast(t('tvGallery.albumError'));
    } finally {
      setAlbumBusy(false);
    }
  }, [grant, selection, selectedAlbumId, albums, handleError, t]);

  const runDestination = useCallback(async (destination: TvPersonalGalleryDestination) => {
    if (selection === null || selection.length === 0 || bulkBusy) return;
    setBulkBusy(true);
    try {
      const result = await addTvPersonalItemsToDestination(grant, destination, selection);
      const failed = result.failures.map((item) => item.itemId);
      setSelection(failed.length > 0 ? failed : null);
      setSelectionLayer('none');
      const label = destination === 'beauty-lab' ? L('Laboratorio bellezza', 'Beauty Lab') : L('Targhe', 'Plates');
      setToast(L(
        `${label}: ${result.succeeded} aggiunte, ${result.skipped} non aggiunte.`,
        `${label}: ${result.succeeded} added, ${result.skipped} not added.`,
      ));
    } catch (err) {
      if (!handleError(err)) setToast(L('Operazione non completata.', 'The action could not be completed.'));
    } finally {
      setBulkBusy(false);
    }
  }, [selection, bulkBusy, grant, handleError, L]);

  const moveSelectionToTrash = useCallback(async () => {
    if (selection === null || selection.length === 0 || bulkBusy) return;
    setBulkBusy(true);
    try {
      const result: TvPersonalGalleryBulkResult = await trashTvPersonalGalleryItems(grant, selection);
      const removed = new Set(result.succeededItemIds);
      for (const item of itemsRef.current) {
        if (!removed.has(item.id)) continue;
        cache.revoke(item.thumbnailUrl);
        cache.revoke(item.previewUrl);
      }
      setItems((current) => current.filter((item) => !removed.has(item.id)));
      setTotalCount((current) => current === null ? current : Math.max(0, current - removed.size));
      const failed = result.failures.map((item) => item.itemId);
      setSelection(failed.length > 0 ? failed : null);
      setSelectionLayer('none');
      setToast(L(
        `${result.succeeded} spostate nel Cestino, ${result.skipped} non spostate.`,
        `${result.succeeded} moved to Trash, ${result.skipped} not moved.`,
      ));
      window.requestAnimationFrame(() => {
        gridRef.current?.querySelector<HTMLElement>('[data-tile]')?.focus();
      });
    } catch (err) {
      if (!handleError(err)) setToast(L('Spostamento non completato.', 'Move could not be completed.'));
    } finally {
      setBulkBusy(false);
    }
  }, [selection, bulkBusy, grant, cache, handleError, L]);

  const restoreGridFocus = useCallback(() => {
    window.requestAnimationFrame(() => {
      const id = menuRestoreIdRef.current;
      const target = id
        ? gridRef.current?.querySelector<HTMLElement>(`[data-item-id="${CSS.escape(id)}"]`)
        : gridRef.current?.querySelector<HTMLElement>('[data-tile]');
      target?.focus();
    });
  }, []);

  const closeMenu = useCallback(() => {
    setMenuOpen(false);
    restoreGridFocus();
  }, [restoreGridFocus]);

  const toggleMenu = useCallback(() => {
    if (menuOpen) {
      closeMenu();
      return;
    }
    menuRestoreIdRef.current = (document.activeElement as HTMLElement | null)?.dataset.itemId ?? null;
    setMenuOpen(true);
  }, [menuOpen, closeMenu]);

  const moveLayerFocus = useCallback((delta: number) => {
    const scope = document.querySelector<HTMLElement>('[data-tv-focus-scope="true"]');
    if (!scope) return;
    const controls = Array.from(scope.querySelectorAll<HTMLElement>(
      'button:not(:disabled), input:not(:disabled), select:not(:disabled)',
    ));
    if (controls.length === 0) return;
    const current = controls.indexOf(document.activeElement as HTMLElement);
    const next = current < 0 ? 0 : (current + delta + controls.length) % controls.length;
    controls[next]?.focus();
  }, []);

  // Grid keyboard: arrows move the roving focus; Backspace exits selection
  // mode first, then returns to the Personal Area home.
  const moveFocus = useCallback((delta: number) => {
    const container = gridRef.current;
    if (!container) return;
    const tiles = Array.from(container.querySelectorAll<HTMLElement>('[data-tile]'));
    if (tiles.length === 0) return;
    const current = tiles.indexOf(document.activeElement as HTMLElement);
    const next = current < 0 ? 0 : Math.min(tiles.length - 1, Math.max(0, current + delta));
    tiles[next]?.focus();
    // Nearing the end with the remote also pages (no wheel on a TV).
    if (next >= tiles.length - 6) loadMoreRef.current();
  }, []);

  const onRootKeyDown = useCallback((e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (viewerIndex !== null) return; // the viewer owns its keys
    const target = e.target as HTMLElement | null;
    const tag = target?.tagName;
    const typing = tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA';
    const layerOpen = showFilters || menuOpen || selectionLayer !== 'none';
    if ((e.key === 'ContextMenu' || e.key === 'F10' || e.key.toLowerCase() === 'm')
      && !typing && !showFilters && selectionLayer === 'none') {
      e.preventDefault();
      toggleMenu();
    } else if ((e.key === 'ArrowRight' || e.key === 'ArrowDown') && !typing) {
      e.preventDefault();
      if (layerOpen) moveLayerFocus(1);
      else moveFocus(1);
    } else if ((e.key === 'ArrowLeft' || e.key === 'ArrowUp') && !typing) {
      e.preventDefault();
      if (layerOpen) moveLayerFocus(-1);
      else moveFocus(-1);
    } else if ((e.key === 'Backspace' && !typing) || e.key === 'Escape') {
      e.preventDefault();
      if (selectionLayer !== 'none') setSelectionLayer('none');
      else if (showFilters) cancelWorkspace();
      else if (menuOpen) closeMenu();
      else if (selection !== null) setSelection(null);
      else onBack();
    }
  }, [viewerIndex, selection, onBack, moveFocus, showFilters, menuOpen, selectionLayer,
    toggleMenu, moveLayerFocus, cancelWorkspace, closeMenu]);

  const openTile = useCallback((index: number) => {
    if (selection !== null) {
      const id = itemsRef.current[index]?.id;
      if (!id) return;
      setSelection((cur) => (cur === null
        ? cur
        : cur.includes(id) ? cur.filter((x) => x !== id) : [...cur, id]));
      return;
    }
    setViewerIndex(index);
  }, [selection]);

  const closeViewer = useCallback((index: number) => {
    setViewerIndex(null);
    const current = itemsRef.current[index];
    const id = current?.id;
    if (current) cache.revoke(current.previewUrl);
    window.requestAnimationFrame(() => {
      const container = gridRef.current;
      if (!container) return;
      const target = (id && container.querySelector<HTMLElement>(`[data-item-id="${CSS.escape(id)}"]`))
        || container.querySelector<HTMLElement>('[data-tile]');
      target?.focus();
    });
  }, [cache]);

  const onSearchSubmit = useCallback((e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    updateDraft({ q: draft.q.trim() });
  }, [draft.q, updateDraft]);

  const filterCount = activeFilterCount(applied);
  const draftFilterCount = activeFilterCount(draft);
  // Display total is the server-authoritative count; fall back to the loaded
  // count only until page 1 lands (totalCount still null).
  const displayTotal = totalCount ?? items.length;
  const selectionSet = useMemo(() => (selection === null ? null : new Set(selection)), [selection]);
  const appliedPills = queryPills(applied, L);

  if (viewerIndex !== null && items[Math.min(viewerIndex, items.length - 1)] !== undefined) {
    return (
      <TvPersonalViewer
        grant={grant}
        cache={cache}
        items={items}
        startIndex={Math.min(viewerIndex, items.length - 1)}
        totalCount={displayTotal}
        hasMore={nextCursor !== null}
        onNeedMore={loadMore}
        onClose={closeViewer}
        onToggleFavorite={toggleFavorite}
        onPersonalError={handleError}
      />
    );
  }

  return (
    <div
      className="tv-browser tv-personal-gallery"
      data-testid="tv-personal-gallery"
      onKeyDown={onRootKeyDown}
      onContextMenu={(event) => { event.preventDefault(); if (!showFilters && selectionLayer === 'none') toggleMenu(); }}
    >
      <span className="visually-hidden" role="status" data-testid="tv-personal-count">
        {phase.kind === 'end'
          ? tn(displayTotal, 'tvGallery.countLoaded')
          : t('tvGallery.countMore', { count: displayTotal })}
      </span>
      {!showFilters && !menuOpen && selectionLayer === 'none' && (
        <button
          type="button"
          className="tv-gallery-workspace-trigger"
          data-testid="tv-personal-toggle-filters"
          onClick={openWorkspace}
          aria-label={L('Apri ricerca e filtri', 'Open search and filters')}
        >
          ⌕
        </button>
      )}
      {filterCount > 0 && !menuOpen && !showFilters && (
        <div className="tv-gallery-status-pill" data-testid="tv-personal-filter-status">
          {L(`${displayTotal} foto · ${filterCount} filtri`, `${displayTotal} photos · ${filterCount} filters`)}
        </div>
      )}
      {selection !== null && !menuOpen && (
        <div className="tv-gallery-selection-pill" data-testid="tv-personal-selection-bar">
          {tn(selection.length, 'tvGallery.selectedCount')}
        </div>
      )}

      {toast !== null && <p className="tv-gallery-toast" role="status" data-testid="tv-personal-toast">{toast}</p>}

      {phase.kind === 'loadingInitial' && <p>{t('tvGallery.loading')}</p>}
      {phase.kind === 'errorInitial' && (
        <div role="alert">
          {t('tvGallery.loadError')}
          <button type="button" onClick={() => setApplied((prev) => ({ ...prev }))}>
            {t('common.tryAgain')}
          </button>
        </div>
      )}

      {(phase.kind === 'ready' || phase.kind === 'end' || phase.kind === 'loadingMore'
        || phase.kind === 'errorMore') && items.length === 0 && (
        <p className="tv-empty" data-testid="tv-personal-empty">
          {filterCount > 0 ? t('tvGallery.noResults') : t('tvGallery.empty')}
        </p>
      )}

      {items.length > 0 && (
        <div className="tv-grid" ref={gridRef}>
          {items.map((item, i) => (
            <button
              key={item.id}
              type="button"
              data-tile
              data-item-id={item.id}
              tabIndex={i === 0 ? 0 : -1}
              className={`tv-tile${selectionSet?.has(item.id) ? ' tv-tile-selected' : ''}`}
              aria-label={item.name}
              aria-pressed={selectionSet?.has(item.id) ?? undefined}
              onClick={() => openTile(i)}
            >
              <PersonalImg
                grant={grant}
                cache={cache}
                mediaUrl={item.thumbnailUrl}
                className="tv-tile-thumb"
                alt=""
                onAuthError={handleError}
              />
              {item.favorite && <span className="tv-tile-badge" aria-hidden="true">♥</span>}
              {selectionSet !== null && (
                <span className="tv-tile-selectmark" aria-hidden="true">
                  {selectionSet.has(item.id) ? '✓' : ''}
                </span>
              )}
              <span className="visually-hidden">{item.name}</span>
            </button>
          ))}
        </div>
      )}

      <div className="tv-personal-footer">
        {(phase.kind === 'ready' || phase.kind === 'loadingMore') && nextCursor !== null && (
          <div ref={sentinelRef} aria-hidden="true" />
        )}
        {phase.kind === 'loadingMore' && <p role="status">{t('tvGallery.loadingMore')}</p>}
        {phase.kind === 'errorMore' && (
          <div role="alert">
            {t('tvGallery.loadMoreError')}
            <button type="button" onClick={loadMore}>{t('common.tryAgain')}</button>
          </div>
        )}
      </div>

      {menuOpen && (
        <div className="tv-gallery-hud" role="dialog" aria-modal="true" data-tv-focus-scope="true">
          <div className="tv-gallery-hud-top">
            <div>
              <h2>{t('tv.personalGallery')}</h2>
              <p>{L(`${displayTotal} risultati`, `${displayTotal} results`)}</p>
            </div>
            <div className="tv-gallery-pills">
              {appliedPills.slice(0, 3).map((pill) => <span key={pill}>{pill}</span>)}
              {appliedPills.length > 3 && <span>+{appliedPills.length - 3}</span>}
            </div>
          </div>
          <div className="tv-gallery-command-rail">
            {selection === null ? (
              <>
                <button type="button" autoFocus onClick={onBack}>{t('tv.personalBack')}</button>
                <button type="button" onClick={openWorkspace}>
                  {L('Ricerca e filtri', 'Search and filters')}
                </button>
                <button type="button" data-testid="tv-personal-select" onClick={() => { setSelection([]); closeMenu(); }}>
                  {t('tvGallery.select')}
                </button>
              </>
            ) : (
              <>
                <button type="button" autoFocus disabled={selection.length === 0} onClick={() => { setMenuOpen(false); setSelectionLayer('album'); }}>
                  Album
                </button>
                <button type="button" disabled={selection.length === 0} onClick={() => { setMenuOpen(false); setSelectionLayer('destination'); }}>
                  {L('Aggiungi a', 'Add to')}
                </button>
                <button type="button" disabled={selection.length === 0} onClick={() => { setMenuOpen(false); setSelectionLayer('trash'); }}>
                  {L('Cestino', 'Trash')}
                </button>
                <button type="button" data-testid="tv-personal-cancel-selection" onClick={() => { setSelection(null); closeMenu(); }}>
                  {L('Azzera selezione', 'Clear selection')}
                </button>
              </>
            )}
          </div>
        </div>
      )}

      {showFilters && (
        <div className="tv-gallery-workspace" role="dialog" aria-modal="true" data-testid="tv-personal-filters" data-tv-focus-scope="true">
          <header>
            <div>
              <h2>{L('Ricerca e filtri', 'Search and filters')}</h2>
              <p>{L(`${filterCount} applicati · ${displayTotal} risultati`, `${filterCount} applied · ${displayTotal} results`)}</p>
            </div>
            <p className="tv-gallery-draft-count">{L(`Bozza: ${draftFilterCount} filtri`, `Draft: ${draftFilterCount} filters`)}</p>
          </header>
          <div className="tv-gallery-workspace-body">
            <nav className="tv-gallery-workspace-tabs" aria-label={L('Modalità', 'Mode')}>
              <button type="button" autoFocus aria-pressed={workspaceMode === 'describe'} onClick={() => setWorkspaceMode('describe')}>
                {L('Descrivi', 'Describe')}
              </button>
              <button type="button" aria-pressed={workspaceMode === 'manual'} onClick={() => setWorkspaceMode('manual')}>
                {L('Filtri manuali', 'Manual filters')}
              </button>
            </nav>

            <section className="tv-gallery-workspace-editor">
              <div className="tv-gallery-query-compare">
                <div><strong>{L('Attualmente applicati', 'Currently applied')}</strong><p>{querySummary(applied, L).join(' · ')}</p></div>
                <div><strong>{L('Modifiche in bozza', 'Draft changes')}</strong><p>{querySummary(draft, L).join(' · ')}</p></div>
              </div>

              {workspaceMode === 'describe' ? (
                <div className="tv-gallery-describe" data-testid="tv-personal-nl">
                  <form onSubmit={(event) => { event.preventDefault(); void runInterpret(); }}>
                    <label htmlFor="tv-personal-nl-input">{L('Descrivi cosa cerchi', 'Describe what you are looking for')}</label>
                    <input
                      id="tv-personal-nl-input"
                      type="text"
                      maxLength={400}
                      value={nlCommand}
                      placeholder={L('Anna al mare l’estate scorsa', 'Anna at the beach last summer')}
                      onChange={(event) => setNlCommand(event.target.value)}
                      data-testid="tv-personal-nl-input"
                    />
                    <button type="submit" disabled={nlBusy || nlCommand.trim().length === 0} data-testid="tv-personal-nl-submit">
                      {nlBusy ? L('Interpreto…', 'Interpreting…') : L('Interpreta', 'Interpret')}
                    </button>
                  </form>
                  {nlError !== null && <p role="alert" data-testid="tv-personal-nl-error">{nlError}</p>}
                  {nlResult !== null && (
                    <div className="tv-gallery-interpretation" data-testid="tv-personal-draft">
                      <h3>{L('Bozza interpretata', 'Interpreted draft')}</h3>
                      <ul>{querySummary(draft, L).map((line) => <li key={line}>{line}</li>)}</ul>
                      {nlResult.ambiguities.map((ambiguity) => (
                        <div key={ambiguity.text} className="tv-personal-ambiguity" data-testid="tv-personal-ambiguity">
                          <p>{L(`Quale ${ambiguity.text}?`, `Which ${ambiguity.text}?`)}</p>
                          {ambiguity.candidates.map((candidate) => (
                            <button
                              type="button"
                              key={candidate.personId}
                              aria-pressed={nlChoices[ambiguity.text] === candidate.personId}
                              data-testid={`tv-personal-ambiguity-${candidate.personId}`}
                              onClick={() => setNlChoices((current) => ({ ...current, [ambiguity.text]: candidate.personId }))}
                            >
                              {candidate.name ?? ambiguity.text}
                            </button>
                          ))}
                        </div>
                      ))}
                      {nlUnresolved && <p role="alert">{L('Risolvi le persone ambigue prima di applicare.', 'Resolve ambiguous people before applying.')}</p>}
                      <button type="button" data-testid="tv-personal-draft-edit" onClick={() => setWorkspaceMode('manual')}>
                        {L('Modifica manualmente', 'Edit manually')}
                      </button>
                      <button type="button" data-testid="tv-personal-draft-cancel" onClick={cancelDraft}>
                        {L('Scarta interpretazione', 'Discard interpretation')}
                      </button>
                    </div>
                  )}
                </div>
              ) : (
                <div className="tv-gallery-manual">
                  <fieldset>
                    <legend>{L('Contenuto', 'Content')}</legend>
                    <label>{L('Contenuto visivo', 'Visual content')}
                      <input
                        value={draft.semanticQuery}
                        data-testid="tv-personal-semantic-input"
                        onChange={(event) => updateDraft({ semanticQuery: event.target.value, semanticTopK: 0 })}
                      />
                    </label>
                    <form onSubmit={onSearchSubmit} role="search">
                      <label>{L('Testo e metadati', 'Text and metadata')}
                        <input
                          type="search"
                          maxLength={256}
                          value={draft.q}
                          onChange={(event) => updateDraft({ q: event.target.value })}
                          data-testid="tv-personal-search-input"
                        />
                      </label>
                    </form>
                  </fieldset>

                  <fieldset>
                    <legend>{L('Persone', 'People')}</legend>
                    {draft.includePeople.length >= 2 && (
                      <select
                        value={draft.includePeopleMode}
                        aria-label={t('tvGallery.peopleModeLabel')}
                        data-testid="tv-personal-people-mode"
                        onChange={(event) => updateDraft({ includePeopleMode: event.target.value as 'all' | 'any' })}
                      >
                        <option value="all">{t('tvGallery.peopleModeAll')}</option>
                        <option value="any">{t('tvGallery.peopleModeAny')}</option>
                      </select>
                    )}
                    <div className="tv-personal-people" data-testid="tv-personal-people">
                      {people === null && <span>{t('tvGallery.loading')}</span>}
                      {people?.map((person) => {
                        const state = draft.includePeople.includes(person.id) ? 'include'
                          : draft.excludePeople.includes(person.id) ? 'exclude' : 'off';
                        const stateKey: MessageKey = state === 'include' ? 'tvGallery.personInclude'
                          : state === 'exclude' ? 'tvGallery.personExclude' : 'tvGallery.personOff';
                        return (
                          <button
                            key={person.id}
                            type="button"
                            className={`tv-personal-person tv-personal-person-${state}`}
                            data-testid={`tv-personal-person-${person.id}`}
                            onClick={() => cyclePersonState(person.id)}
                          >
                            {person.name ?? t('tvGallery.unnamedPerson')} ({person.faceCount}) · {t(stateKey)}
                          </button>
                        );
                      })}
                    </div>
                  </fieldset>

                  <fieldset>
                    <legend>{L('Periodo', 'Period')}</legend>
                    <label>{t('tvGallery.dateFrom')}<input type="date" value={draft.dateFrom} data-testid="tv-personal-date-from" onChange={(event) => updateDraft({ dateFrom: event.target.value })} /></label>
                    <label>{t('tvGallery.dateTo')}<input type="date" value={draft.dateTo} data-testid="tv-personal-date-to" onChange={(event) => updateDraft({ dateTo: event.target.value })} /></label>
                    {!validDateRange(draft) && <p role="alert">{L('Intervallo di date non valido.', 'Invalid date range.')}</p>}
                  </fieldset>

                  <fieldset>
                    <legend>{L('Attributi', 'Attributes')}</legend>
                    <label>{t('tvGallery.favorite')}
                      <select value={draft.favorite === null ? '' : String(draft.favorite)} data-testid="tv-personal-favorite" onChange={(event) => updateDraft({ favorite: event.target.value === '' ? null : event.target.value === 'true' })}>
                        <option value="">{t('tvGallery.any')}</option><option value="true">{t('tvGallery.favOnly')}</option><option value="false">{t('tvGallery.favNot')}</option>
                      </select>
                    </label>
                    <label>{t('tvGallery.minRating')}
                      <select value={draft.minRating === null ? '' : String(draft.minRating)} data-testid="tv-personal-minrating" onChange={(event) => updateDraft({ minRating: event.target.value === '' ? null : Number(event.target.value) })}>
                        <option value="">{t('tvGallery.any')}</option>{[1, 2, 3, 4, 5].map((n) => <option key={n} value={n}>★ {n}+</option>)}
                      </select>
                    </label>
                    <label>{t('tvGallery.gps')}
                      <select value={draft.hasGps === null ? '' : String(draft.hasGps)} data-testid="tv-personal-gps" onChange={(event) => updateDraft({ hasGps: event.target.value === '' ? null : event.target.value === 'true' })}>
                        <option value="">{t('tvGallery.any')}</option><option value="true">{t('tvGallery.gpsWith')}</option><option value="false">{t('tvGallery.gpsWithout')}</option>
                      </select>
                    </label>
                    <label><input type="checkbox" checked={draft.collapseDuplicates} data-testid="tv-personal-duplicates" onChange={(event) => updateDraft({ collapseDuplicates: event.target.checked })} />{t('tvGallery.hideDuplicates')}</label>
                  </fieldset>

                  <fieldset>
                    <legend>{L('Ordinamento', 'Ordering')}</legend>
                    {draft.semanticQuery.trim() ? <strong>{L('Rilevanza', 'Relevance')}</strong> : (
                      <>
                        <select value={draft.sort} data-testid="tv-personal-sort" onChange={(event) => updateDraft({ sort: event.target.value as TvGallerySortField })}>
                          <option value="created">{t('tvGallery.sortCreated')}</option><option value="datetaken">{t('tvGallery.sortDateTaken')}</option><option value="name">{t('tvGallery.sortName')}</option><option value="size">{t('tvGallery.sortSize')}</option>
                        </select>
                        <select value={draft.direction} aria-label={t('tvGallery.directionLabel')} data-testid="tv-personal-direction" onChange={(event) => updateDraft({ direction: event.target.value as TvGallerySortDirection })}>
                          <option value="desc">{t('tvGallery.dirDesc')}</option><option value="asc">{t('tvGallery.dirAsc')}</option>
                        </select>
                      </>
                    )}
                  </fieldset>
                </div>
              )}
            </section>
          </div>
          <footer className="tv-gallery-workspace-actions">
            <button type="button" data-testid="tv-personal-clear-filters" onClick={resetDraft}>{L('Azzera bozza', 'Reset draft')}</button>
            <button type="button" onClick={cancelWorkspace}>{L('Annulla', 'Cancel')}</button>
            <button type="button" data-testid="tv-personal-draft-apply" disabled={nlUnresolved || !validDateRange(draft) || nlBusy} onClick={applyWorkspace}>{L('Applica filtri', 'Apply filters')}</button>
          </footer>
        </div>
      )}

      {selectionLayer === 'album' && selection !== null && (
        <div className="tv-gallery-dialog" role="dialog" aria-modal="true" data-tv-focus-scope="true">
          <h2>{L('Aggiungi a un album', 'Add to an album')}</h2>
          <p>{tn(selection.length, 'tvGallery.selectedCount')}</p>
          {albums !== null && albums.length > 0 ? (
            <select autoFocus value={selectedAlbumId} aria-label={t('tvGallery.chooseAlbum')} data-testid="tv-personal-album-select" onChange={(event) => setSelectedAlbumId(event.target.value)}>
              {albums.map((album) => <option key={album.id} value={album.id}>{album.name}</option>)}
            </select>
          ) : <p>{t('tvGallery.albumEmpty')}</p>}
          <div><button type="button" onClick={() => setSelectionLayer('none')}>{L('Annulla', 'Cancel')}</button><button type="button" disabled={albumBusy || selection.length === 0 || selectedAlbumId === ''} data-testid="tv-personal-album-add" onClick={() => void addSelectionToAlbum()}>{t('tvGallery.addToAlbum')}</button></div>
        </div>
      )}

      {selectionLayer === 'destination' && selection !== null && (
        <div className="tv-gallery-dialog" role="dialog" aria-modal="true" data-tv-focus-scope="true">
          <h2>{L('Aggiungi a', 'Add to')}</h2><p>{tn(selection.length, 'tvGallery.selectedCount')}</p>
          <button type="button" autoFocus disabled={bulkBusy} onClick={() => void runDestination('beauty-lab')}>{L('Laboratorio bellezza', 'Beauty Lab')}</button>
          <button type="button" disabled={bulkBusy} onClick={() => void runDestination('plates')}>{L('Targhe', 'Plates')}</button>
          <button type="button" disabled={bulkBusy} onClick={() => setSelectionLayer('none')}>{L('Annulla', 'Cancel')}</button>
        </div>
      )}

      {selectionLayer === 'trash' && selection !== null && (
        <div className="tv-gallery-dialog" role="dialog" aria-modal="true" data-tv-focus-scope="true">
          <h2>{L(`Spostare ${selection.length} foto nel Cestino?`, `Move ${selection.length} photos to Trash?`)}</h2>
          <p>{L('Potrai ripristinarle dal Cestino.', 'They can be restored from Trash.')}</p>
          <div><button type="button" autoFocus disabled={bulkBusy} onClick={() => setSelectionLayer('none')}>{L('Annulla', 'Cancel')}</button><button type="button" disabled={bulkBusy} onClick={() => void moveSelectionToTrash()}>{L('Sposta nel Cestino', 'Move to Trash')}</button></div>
        </div>
      )}
    </div>
  );
}

function queryPills(
  query: AppliedQuery,
  L: (it: string, en: string) => string,
): string[] {
  const pills: string[] = [];
  if (query.semanticQuery.trim()) pills.push(query.semanticQuery.trim());
  if (query.q.trim()) pills.push(query.q.trim());
  const people = query.includePeople.length + query.excludePeople.length;
  if (people > 0) pills.push(L(`${people} persone`, `${people} people`));
  if (query.dateFrom || query.dateTo) pills.push(`${query.dateFrom || '…'} → ${query.dateTo || '…'}`);
  if (query.favorite !== null) pills.push(query.favorite ? L('Preferite', 'Favorites') : L('Non preferite', 'Not favorites'));
  if (query.minRating !== null) pills.push(`★ ${query.minRating}+`);
  if (query.hasGps !== null) pills.push(query.hasGps ? 'GPS ✓' : 'GPS —');
  if (query.collapseDuplicates) pills.push(L('Senza duplicati', 'No duplicates'));
  return pills;
}

function querySummary(
  query: AppliedQuery,
  L: (it: string, en: string) => string,
): string[] {
  const lines = queryPills(query, L);
  lines.push(query.semanticQuery.trim()
    ? L('Ordine: rilevanza', 'Order: relevance')
    : `${L('Ordine', 'Order')}: ${query.sort} · ${query.direction}`);
  return lines.length > 0 ? lines : [L('Tutte le foto', 'All photos')];
}

// Full-screen viewer over the CURRENT filtered result set: arrows navigate
// (crossing page boundaries via onNeedMore), Enter toggles the slideshow,
// Backspace/Escape return to the grid (focus restored by item id). Favorite
// and the curated details panel live in the bar — never destructive, never
// original bytes (medium preview only).
function TvPersonalViewer({
  grant,
  cache,
  items,
  startIndex,
  totalCount,
  hasMore,
  onNeedMore,
  onClose,
  onToggleFavorite,
  onPersonalError,
}: {
  grant: string;
  cache: PersonalMediaCache;
  items: TvPersonalGalleryItem[];
  startIndex: number;
  // Server-authoritative total for the current query (the counter denominator);
  // stable across page loads — never items.length.
  totalCount: number;
  hasMore: boolean;
  onNeedMore: () => void;
  onClose: (index: number) => void;
  onToggleFavorite: (id: string, favorite: boolean) => Promise<boolean>;
  onPersonalError: (err: unknown) => boolean;
}) {
  const { t, formatDate } = useI18n();
  const [index, setIndex] = useState(startIndex);
  const [playing, setPlaying] = useState(false);
  const [infoOpen, setInfoOpen] = useState(false);
  const [info, setInfo] = useState<TvPersonalMediaInfo | null>(null);
  const [infoError, setInfoError] = useState(false);
  const [favoriteBusy, setFavoriteBusy] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  const clamped = Math.min(index, Math.max(0, items.length - 1));
  const item = items[clamped];

  useEffect(() => {
    rootRef.current?.focus();
  }, []);

  // Cross-page prefetch near the end of the loaded set.
  useEffect(() => {
    if (hasMore && clamped >= items.length - 3) onNeedMore();
  }, [clamped, items.length, hasMore, onNeedMore]);

  // Slideshow auto-advance.
  useEffect(() => {
    if (!playing || items.length === 0) return;
    const timer = window.setTimeout(() => {
      setIndex((i) => (hasMore && i >= items.length - 1 ? i : (i + 1) % items.length));
    }, 9000);
    return () => window.clearTimeout(timer);
  }, [playing, clamped, items.length, hasMore]);

  // Details are fetched only while the panel is open, per current item.
  useEffect(() => {
    if (!infoOpen || !item) return;
    const ctrl = new AbortController();
    setInfo(null);
    setInfoError(false);
    getTvPersonalMediaInfo(grant, item.id, ctrl.signal)
      .then(setInfo)
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (!onPersonalError(err)) setInfoError(true);
      });
    return () => ctrl.abort();
  }, [infoOpen, item, grant, onPersonalError]);

  const goPrev = useCallback(() => setIndex((i) => Math.max(0, i - 1)), []);
  const goNext = useCallback(() => {
    setIndex((i) => Math.min(items.length - 1, i + 1));
  }, [items.length]);

  const onKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
      e.preventDefault();
      goNext();
    } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
      e.preventDefault();
      goPrev();
    } else if (e.key === 'Enter' || e.key === ' ') {
      const tag = (e.target as HTMLElement | null)?.tagName;
      if (tag === 'BUTTON') return; // let focused bar buttons act normally
      e.preventDefault();
      setPlaying((p) => !p);
    } else if (e.key === 'Backspace' || e.key === 'Escape') {
      e.preventDefault();
      if (infoOpen) setInfoOpen(false);
      else onClose(clamped);
    }
  };

  const toggleFavorite = async () => {
    if (!item || favoriteBusy) return;
    setFavoriteBusy(true);
    await onToggleFavorite(item.id, !item.favorite);
    setFavoriteBusy(false);
  };

  if (!item) return null;

  const infoRows: Array<[string, string]> = [];
  if (info !== null) {
    infoRows.push([t('tvGallery.infoDate'), formatDate(info.dateTaken)]);
    if (info.width !== null && info.height !== null) {
      infoRows.push([t('tvGallery.infoDimensions'), `${info.width}×${info.height}`]);
    }
    const camera = [info.cameraMake, info.cameraModel].filter(Boolean).join(' ');
    if (camera) infoRows.push([t('tvGallery.infoCamera'), camera]);
    infoRows.push([
      t('tvGallery.infoGps'),
      info.hasGps ? t('tvGallery.gpsPresent') : t('tvGallery.gpsNone'),
    ]);
    if (info.title) infoRows.push([t('tvGallery.infoTitleField'), info.title]);
    if (info.description) infoRows.push([t('tvGallery.infoDescription'), info.description]);
    if (info.tags.length > 0) infoRows.push([t('tvGallery.infoTags'), info.tags.join(', ')]);
    if (info.rating !== null) infoRows.push([t('tvGallery.infoRating'), `${info.rating}/5`]);
    if (info.location) infoRows.push([t('tvGallery.infoLocation'), info.location]);
  }

  return (
    <div
      className="tv-viewer tv-personal-viewer"
      ref={rootRef}
      tabIndex={-1}
      role="dialog"
      aria-label={item.name}
      data-testid="tv-personal-viewer"
      onKeyDown={onKeyDown}
    >
      <div className="tv-viewer-stage">
        <PersonalImg
          grant={grant}
          cache={cache}
          mediaUrl={item.previewUrl}
          className="tv-viewer-media"
          alt={item.name}
          onAuthError={onPersonalError}
        />
      </div>
      {infoOpen && (
        <aside className="tv-personal-info" data-testid="tv-personal-info">
          <h3>{t('tvGallery.infoTitle')}</h3>
          {info === null && !infoError && <p role="status">{t('tvGallery.loading')}</p>}
          {infoError && <p role="alert">{t('tvGallery.infoError')}</p>}
          {info !== null && (
            <dl>
              {infoRows.map(([label, value]) => (
                <div key={label}>
                  <dt>{label}</dt>
                  <dd>{value}</dd>
                </div>
              ))}
            </dl>
          )}
        </aside>
      )}
      <div className="tv-viewer-bar">
        <button type="button" data-testid="tv-personal-viewer-back" onClick={() => onClose(clamped)}>
          {t('tv.viewerBack')}
        </button>
        <button type="button" onClick={goPrev}>{t('tv.prev')}</button>
        <button
          type="button"
          aria-pressed={playing}
          onClick={() => setPlaying((p) => !p)}
        >
          {playing ? t('tv.pause') : t('tv.play')}
        </button>
        <button type="button" onClick={goNext}>{t('tv.next')}</button>
        <button
          type="button"
          disabled={favoriteBusy}
          data-testid="tv-personal-viewer-favorite"
          aria-pressed={item.favorite}
          onClick={() => void toggleFavorite()}
        >
          {item.favorite ? t('tvGallery.favoriteOn') : t('tvGallery.favoriteOff')}
        </button>
        <button
          type="button"
          aria-pressed={infoOpen}
          data-testid="tv-personal-viewer-details"
          onClick={() => setInfoOpen((v) => !v)}
        >
          {t('tvGallery.details')}
        </button>
        <span className="tv-viewer-caption" data-testid="tv-personal-viewer-counter">
          {/* Absolute position (loaded items are an ordered prefix, so the
              loaded index IS the absolute position) over the server total —
              never the loaded-items length. */}
          {clamped + 1} / {totalCount} · {item.name}
        </span>
      </div>
    </div>
  );
}
