import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ApiError,
  listImages,
  listMedia,
  listAlbumMedia,
  getMediaWindow,
  searchSemanticMedia,
  type ImageItem,
  type ListImagesQuery,
  type MediaItem,
  type SemanticBestMatch,
} from '@nubarca/api-client';
import { useMediaSelection, type MediaSelection } from '../../gallery/useMediaSelection';
import {
  DEFAULT_MEDIA_LIMIT,
  isSemanticActive,
  queryFingerprint,
  queryToWire,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from './mediaWorkspaceQuery';

// The workspace engine shared by the library and album surfaces. It owns the
// applied query identity, the deduplicated infinite-scroll accumulator, the
// server-authoritative counts, aborting, the selection and the viewer index.
// Any change to the query identity (source/scope/kind/filters/sort) resets the
// accumulator + cursor + selection + viewer and refetches page one. Visual
// (semantic) photo search is routed to the dedicated /api/images path — which
// accepts an albumId, so it works for both sources — and mapped to MediaItem.

export type LoadPhase =
  | { kind: 'loadingInitial' }
  | { kind: 'ready' }
  | { kind: 'loadingMore' }
  | { kind: 'end' }
  | { kind: 'errorInitial'; message: string }
  | { kind: 'errorMore'; message: string };

export interface MediaViewerController {
  index: number | null;
  isOpen: boolean;
  // SEARCH-SEM-01: `atMs` opens the item at an explicit position — a semantic
  // marker's own representative timestamp. Omitting it opens normally, which is
  // what every non-semantic caller does and why their behaviour is unchanged.
  open(index: number, atMs?: number): void;
  close(): void;
  setIndex(index: number): void;
  // The explicitly requested position for the CURRENTLY open item, or null.
  // Deliberately cleared by close() and setIndex() so navigating to the next
  // item in the viewer can never inherit the previous item's seek.
  seekMs: number | null;
}

// VSEM-03: the temporal evidence of one semantic result, keyed by media id.
// Present only for items returned by the unified semantic search; photos carry
// null temporal fields and videos their best segment + representative
// timestamp, plus up to three further distinct intervals.
export interface SemanticEvidence {
  bestMatch: SemanticBestMatch;
  additionalMatches: SemanticBestMatch[];
}

export interface UseMediaWorkspaceResult {
  items: MediaItem[];
  selectedItems: MediaItem[];
  navigationBusy: boolean;
  navigationRevision: number;
  previousCursor: string | null;
  scrollTarget: { id: string; revision: number } | null;
  jumpTo(target: string): Promise<boolean>;
  loadPrevious(): Promise<boolean>;
  orderedIds: string[];
  total: number | null;
  photoCount: number | null;
  videoCount: number | null;
  // Temporal evidence by media id (empty unless a unified semantic search is
  // active). The grid renders a badge and the viewer opens at the timestamp.
  semanticEvidence: Map<string, SemanticEvidence>;

  phase: LoadPhase;
  loading: boolean;
  loadingMore: boolean;
  error: string | null;
  hasMore: boolean;
  // Non-null when the semantic photo search reports a non-'ok' status
  // (already-localized message) — e.g. the AI profile is unavailable or the
  // embeddings are still indexing.
  semanticNotice: string | null;

  loadMore(): void;
  retryMore(): void;
  refresh(): void;

  selection: MediaSelection;
  viewer: MediaViewerController;

  removeLoadedIds(ids: string[]): void;
  reconcileAfterPartialMutation(): void;
  patchItem(id: string, patch: Partial<MediaItem>): void;
}

// Project an ImageItem (semantic photo path) onto the unified MediaItem shape.
// The semantic path carries no favorite/rating/gps; the card does not need them.
function imageItemToMediaItem(it: ImageItem): MediaItem {
  return {
    id: it.id,
    kind: 'image',
    name: it.name,
    title: it.title,
    displayName: it.displayName,
    mimeType: it.mimeType,
    sizeBytes: it.sizeBytes,
    width: it.width,
    height: it.height,
    createdAt: it.createdAt,
    updatedAt: it.updatedAt,
    takenAt: null,
    favorite: false,
    rating: null,
    thumbnailUrl: it.thumbnailUrl,
    occurrenceCount: it.occurrenceCount,
    hasDuplicates: it.hasDuplicates,
    hasGps: null,
  };
}

// Compose the legacy /api/images query for the visual-search path from the
// workspace identity (physical photo filters + the semantic residual). The
// album source is expressed as an albumId so the semantic ranking is correctly
// restricted server-side (never filtered client-side).
function identityToImagesQuery(
  identity: MediaWorkspaceIdentity,
  source: MediaWorkspaceSource,
  cursor: string | null,
): ListImagesQuery {
  const { common, photo } = identity.filters;
  return {
    mediaScope: identity.libraryScope,
    q: common.metadataQuery.length > 0 ? common.metadataQuery : undefined,
    favorite: common.favorite ?? undefined,
    minRating: common.minRating ?? undefined,
    dateTakenFrom: common.dateTakenFrom.length > 0 ? common.dateTakenFrom : undefined,
    dateTakenTo: common.dateTakenTo.length > 0 ? common.dateTakenTo : undefined,
    albumMembership: source.kind === 'library' && common.albumMembership !== 'any'
      ? common.albumMembership
      : undefined,
    hasGps: photo.hasGps ?? undefined,
    collapseDuplicates: photo.collapseDuplicates || undefined,
    similarTo: photo.similarTo.length > 0 ? photo.similarTo : undefined,
    includePeople: photo.includePeople.length > 0 ? photo.includePeople : undefined,
    excludePeople: photo.excludePeople.length > 0 ? photo.excludePeople : undefined,
    includePeopleMode: photo.includePeople.length > 0 ? photo.includePeopleMode : undefined,
    // OMIT semanticTopK when it is not explicitly set (0): the backend's
    // ClampTopK(null) → DefaultTopK (a full result set), whereas ClampTopK(0) →
    // Math.Clamp(0, min, max) = min (== 1 result). Only send a positive value.
    semanticQuery: photo.visualQuery.trim().length > 0 ? photo.visualQuery.trim() : undefined,
    semanticTopK: photo.semanticTopK > 0 ? photo.semanticTopK : undefined,
    albumId: source.kind === 'album' ? source.albumId : undefined,
    cursor: cursor ?? undefined,
  };
}

// The photo tab uses the dedicated /api/images path (not /api/media) when a
// visual query OR a similarity anchor is active — that endpoint resolves the
// semantic ranking / similarity restrict-set server-side and honours albumId,
// so results stay server-scoped for both the library and an album. VSEM-03
// leaves this path untouched: only the "Tutti" and "Video" tabs route to the
// new unified endpoint.
function usesLegacyPhotoPath(identity: MediaWorkspaceIdentity): boolean {
  return identity.mediaKind === 'image'
    && (isSemanticActive(identity) || identity.filters.photo.similarTo.length > 0);
}

// VSEM-03: mixed photo+video semantic search.
//
// The unified endpoint now takes an optional albumId, so this path serves an
// album too — confined to it. Before that it was library-scoped, which meant a
// visual query on the "Tutti" or "Video" tab inside an album did nothing at
// all: the capability was simply missing there. The album is passed below; it
// is a PHYSICAL filter the server applies before ranking, so it also binds the
// cursor.
function usesUnifiedSemanticPath(identity: MediaWorkspaceIdentity): boolean {
  return identity.mediaKind !== 'image' && isSemanticActive(identity);
}

interface FetchedPage {
  items: MediaItem[];
  nextCursor: string | null;
  total: number;
  photoCount: number;
  videoCount: number;
  // Only set on a semantic path: 'ok' | 'unavailable' | 'indexing'.
  semanticStatus: 'ok' | 'unavailable' | 'indexing' | null;
  // VSEM-03 temporal evidence by media id (empty on non-semantic paths).
  evidence: Map<string, SemanticEvidence>;
}

const NO_EVIDENCE: Map<string, SemanticEvidence> = new Map();

async function fetchPage(
  identity: MediaWorkspaceIdentity,
  source: MediaWorkspaceSource,
  cursor: string | null,
  signal: AbortSignal,
): Promise<FetchedPage> {
  // Photo tab with an active visual query OR similarity anchor → dedicated
  // /api/images path (server-side ranking / similarity restrict + albumId).
  if (usesLegacyPhotoPath(identity)) {
    const res = await listImages(identityToImagesQuery(identity, source, cursor), signal);
    const items = res.items.map(imageItemToMediaItem);
    const total = res.total ?? items.length;
    // Surface the semantic status so an empty result explains itself (the AI
    // profile is unavailable, or embeddings are still indexing) instead of
    // looking like a broken filter. Only meaningful when a visual query is set.
    const semanticStatus = res.semanticActive === true ? (res.semanticStatus ?? 'ok') : null;
    return {
      items, nextCursor: res.nextCursor, total,
      photoCount: total, videoCount: 0, semanticStatus, evidence: NO_EVIDENCE,
    };
  }

  // "Tutti" / "Video" with a visual query → unified semantic retrieval.
  if (usesUnifiedSemanticPath(identity)) {
    const { common } = identity.filters;
    let res;
    try {
      res = await searchSemanticMedia({
        q: identity.filters.photo.visualQuery.trim(),
        kind: identity.mediaKind,
        limit: DEFAULT_MEDIA_LIMIT,
        cursor: cursor ?? undefined,
        favorite: common.favorite ?? undefined,
        minRating: common.minRating ?? undefined,
        dateTakenFrom: common.dateTakenFrom.length > 0 ? common.dateTakenFrom : undefined,
        dateTakenTo: common.dateTakenTo.length > 0 ? common.dateTakenTo : undefined,
        // Library only, and only when set: a physical filter the server must
        // apply to the candidate scope before ranking.
        albumMembership: source.kind === 'library' && common.albumMembership !== 'any'
          ? common.albumMembership
          : undefined,
        // Confine the search to the album being browsed. Without it this path
        // would answer an album's visual search from the whole library — worse
        // than the absence it replaces, because the results would look right.
        albumId: source.kind === 'album' ? source.albumId : undefined,
      }, signal);
    } catch (err) {
      // The AI profile / text tower is unavailable: an expected operational
      // state, not a load failure. Show an empty result with the standard
      // notice instead of an error banner (the reason is never surfaced).
      if (err instanceof ApiError && err.status === 503) {
        return {
          items: [], nextCursor: null, total: 0, photoCount: 0, videoCount: 0,
          semanticStatus: 'unavailable', evidence: NO_EVIDENCE,
        };
      }
      throw err;
    }

    const items = res.items.map((it) => it.media);
    const evidence = new Map<string, SemanticEvidence>();
    for (const it of res.items) {
      evidence.set(it.media.id, {
        bestMatch: it.bestMatch,
        additionalMatches: it.additionalMatches,
      });
    }
    const photoCount = items.filter((it) => it.kind === 'image').length;
    return {
      items,
      nextCursor: res.nextCursor,
      total: res.total,
      photoCount,
      videoCount: items.length - photoCount,
      semanticStatus: res.semanticStatus,
      evidence,
    };
  }

  const wire = queryToWire(identity, cursor);
  const res = source.kind === 'album'
    ? await listAlbumMedia(source.albumId, wire, signal)
    : await listMedia(wire, signal);
  return {
    items: res.items,
    nextCursor: res.nextCursor,
    total: res.total,
    photoCount: res.photoCount,
    videoCount: res.videoCount,
    semanticStatus: null,
    evidence: NO_EVIDENCE,
  };
}

export interface UseMediaWorkspaceOptions {
  source: MediaWorkspaceSource;
  // Controlled query identity — the page derives it from the URL (single source
  // of truth), so back/forward navigation refetches naturally via the
  // fingerprint effect below.
  identity: MediaWorkspaceIdentity;
  onAuthError?(): void;
  translate: {
    loadError: string;
    loadMoreError: string;
    semanticUnavailable: string;
    semanticIndexing: string;
  };
}

export function useMediaWorkspace(
  { source, identity, onAuthError, translate }: UseMediaWorkspaceOptions,
): UseMediaWorkspaceResult {
  const selection = useMediaSelection();

  const [items, setItems] = useState<MediaItem[]>([]);
  const [previousCursor, setPreviousCursor] = useState<string | null>(null);
  const [navigationBusy, setNavigationBusy] = useState(false);
  const [navigationRevision, setNavigationRevision] = useState(0);
  const [scrollTarget, setScrollTarget] = useState<{ id: string; revision: number } | null>(null);
  const selectedCache = useRef(new Map<string, MediaItem>());
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [total, setTotal] = useState<number | null>(null);
  const [photoCount, setPhotoCount] = useState<number | null>(null);
  const [videoCount, setVideoCount] = useState<number | null>(null);
  const [phase, setPhase] = useState<LoadPhase>({ kind: 'loadingInitial' });
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);
  // SEARCH-SEM-01: an explicit one-shot seek for the open item (a semantic
  // marker). Null means "use whatever default the item carries".
  const [viewerSeekMs, setViewerSeekMs] = useState<number | null>(null);
  const [semanticNotice, setSemanticNotice] = useState<string | null>(null);
  const [semanticEvidence, setSemanticEvidence] = useState<Map<string, SemanticEvidence>>(NO_EVIDENCE);

  const noticeFor = useCallback((status: 'ok' | 'unavailable' | 'indexing' | null): string | null => {
    if (status === 'unavailable') return translate.semanticUnavailable;
    if (status === 'indexing') return translate.semanticIndexing;
    return null;
  }, [translate.semanticUnavailable, translate.semanticIndexing]);

  const loadingRef = useRef(false);
  const generationRef = useRef(0);
  const controllerRef = useRef<AbortController | null>(null);

  const fingerprint = queryFingerprint(identity);

  const handleError = useCallback((err: unknown, fallback: string): string | 'auth' => {
    if (err instanceof ApiError && err.status === 401) {
      onAuthError?.();
      return 'auth';
    }
    return fallback;
  }, [onAuthError]);

  // Initial load on mount + whenever the query identity changes.
  useEffect(() => {
    const controller = new AbortController();
    controllerRef.current = controller;
    generationRef.current += 1;
    const gen = generationRef.current;
    loadingRef.current = true;
    setItems([]);
    setPreviousCursor(null);
    setNavigationBusy(false);
    setScrollTarget(null);
    selectedCache.current.clear();
    setNavigationRevision((v) => v + 1);
    setNextCursor(null);
    setTotal(null);
    setViewerIndex(null);
    setSemanticNotice(null);
    setSemanticEvidence(NO_EVIDENCE);
    selection.clear();
    setPhase({ kind: 'loadingInitial' });

    void (async () => {
      try {
        const data = await fetchPage(identity, source, null, controller.signal);
        if (gen !== generationRef.current) return;
        setItems(data.items);
        setNextCursor(data.nextCursor);
        setTotal(data.total);
        setPhotoCount(data.photoCount);
        setVideoCount(data.videoCount);
        setSemanticNotice(noticeFor(data.semanticStatus));
        setSemanticEvidence(data.evidence);
        setPhase(data.nextCursor ? { kind: 'ready' } : { kind: 'end' });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (gen !== generationRef.current) return;
        const message = handleError(err, translate.loadError);
        if (message === 'auth') return;
        setPhase({ kind: 'errorInitial', message });
      } finally {
        if (gen === generationRef.current) loadingRef.current = false;
      }
    })();

    return () => {
      generationRef.current += 1;
      controllerRef.current?.abort();
    };
    // Refetch keyed on the query identity fingerprint + source, not object
    // identity — a structurally-equal setIdentity does not refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fingerprint, source.kind, source.kind === 'album' ? source.albumId : '']);

  const startLoadMore = useCallback((isRetry: boolean) => {
    if (loadingRef.current || nextCursor === null) return;
    if (isRetry) { if (phase.kind !== 'errorMore') return; }
    else if (phase.kind !== 'ready') return;

    const controller = controllerRef.current;
    const gen = generationRef.current;
    const cursor = nextCursor;
    loadingRef.current = true;
    setPhase({ kind: 'loadingMore' });

    void (async () => {
      try {
        const data = await fetchPage(identity, source, cursor, controller!.signal);
        if (gen !== generationRef.current) return;
        setItems((prev) => {
          const seen = new Set(prev.map((it) => it.id));
          return [...prev, ...data.items.filter((it) => !seen.has(it.id))];
        });
        // Accumulate the page's temporal evidence alongside the items.
        if (data.evidence.size > 0) {
          setSemanticEvidence((prev) => new Map([...prev, ...data.evidence]));
        }
        setNextCursor(data.nextCursor);
        // The counts are a query-identity property; the server computes them only
        // on the first page and returns -1 ("unchanged") for load-more, so keep
        // the first page's totals rather than overwriting them with a sentinel.
        if (data.total >= 0) setTotal(data.total);
        if (data.photoCount >= 0) setPhotoCount(data.photoCount);
        if (data.videoCount >= 0) setVideoCount(data.videoCount);
        setPhase(data.nextCursor ? { kind: 'ready' } : { kind: 'end' });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (gen !== generationRef.current) return;
        const message = handleError(err, translate.loadMoreError);
        if (message === 'auth') return;
        setPhase({ kind: 'errorMore', message });
      } finally {
        if (gen === generationRef.current) loadingRef.current = false;
      }
    })();
  }, [identity, source, nextCursor, phase, handleError, translate.loadMoreError]);

  const orderedIds = useMemo(() => items.map((it) => it.id), [items]);

  const refresh = useCallback(() => {
    // Bump the effect by cloning identity (same fingerprint would not refetch,
    // so mutate a throwaway wrapper: clone forces the effect via a new object is
    // not enough — instead re-run by toggling through a shallow clone of nested
    // objects that preserves the fingerprint but is a new reference is a no-op).
    // The robust refresh is a first-page refetch keyed off a generation bump.
    generationRef.current += 1;
    const gen = generationRef.current;
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;
    loadingRef.current = true;
    setPreviousCursor(null);
    setNavigationBusy(false);
    setScrollTarget(null);
    setNavigationRevision((v) => v + 1);
    selectedCache.current.clear();
    selection.clear();
    setViewerIndex(null);
    setSemanticNotice(null);
    setSemanticEvidence(NO_EVIDENCE);
    setPhase({ kind: 'loadingInitial' });
    void (async () => {
      try {
        const data = await fetchPage(identity, source, null, controller.signal);
        if (gen !== generationRef.current) return;
        setItems(data.items);
        setNextCursor(data.nextCursor);
        setTotal(data.total);
        setPhotoCount(data.photoCount);
        setVideoCount(data.videoCount);
        setSemanticNotice(noticeFor(data.semanticStatus));
        setSemanticEvidence(data.evidence);
        setPhase(data.nextCursor ? { kind: 'ready' } : { kind: 'end' });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (gen !== generationRef.current) return;
        const message = handleError(err, translate.loadError);
        if (message === 'auth') return;
        setPhase({ kind: 'errorInitial', message });
      } finally {
        if (gen === generationRef.current) loadingRef.current = false;
      }
    })();
  }, [identity, source, selection, handleError, translate.loadError, noticeFor]);

  const navigateWindow = useCallback(async (target: string | null): Promise<boolean> => {
    const before = target === null;
    if (before && (loadingRef.current || previousCursor === null)) return false;
    if (isSemanticActive(identity) || identity.sort === 'size' || identity.filters.photo.similarTo) return false;
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;
    const gen = ++generationRef.current;
    loadingRef.current = true;
    setNavigationBusy(true);
    // A stalled read must leave the current wall usable, not wait forever.
    const deadline = window.setTimeout(() => controller.abort(), 30_000);
    try {
      const data = await getMediaWindow(source.kind === 'album' ? source.albumId : null,
        { ...queryToWire(identity, before ? previousCursor : null),
          target: target ?? undefined, before }, controller.signal);
      if (controller.signal.aborted || gen !== generationRef.current) return false;
      if (data.items.length === 0) { if (before) setPreviousCursor(null); return false; }
      if (before) {
        const seen = new Set(items.map((it) => it.id));
        const prefix = data.items.filter((it) => !seen.has(it.id));
        setItems([...prefix, ...items]);
        setViewerIndex((index) => index === null ? null : index + prefix.length);
        setPreviousCursor(data.previousCursor);
      } else {
        setViewerIndex(null);
        for (const id of selectedCache.current.keys()) if (!selection.isSelected(id)) selectedCache.current.delete(id);
        for (const item of items) if (selection.isSelected(item.id)) selectedCache.current.set(item.id, item);
        setItems(data.items);
        setNextCursor(data.nextCursor);
        setPreviousCursor(data.previousCursor);
        setScrollTarget((prev) => ({ id: data.items[0].id, revision: (prev?.revision ?? 0) + 1 }));
        setPhase(data.nextCursor ? { kind: 'ready' } : { kind: 'end' });
      }
      return true;
    } catch (err) {
      if (gen === generationRef.current) {
        handleError(err, translate.loadError);
        // A bucket may have vanished or been renamed; re-read the aggregate.
        if (err instanceof ApiError && err.status === 404) setNavigationRevision((v) => v + 1);
      }
      return false;
    } finally {
      window.clearTimeout(deadline);
      if (gen === generationRef.current) {
        loadingRef.current = false;
        setNavigationBusy(false);
        // An interrupted load-more may still have left its old phase behind.
        setPhase((prev) => prev.kind === 'loadingMore' ? { kind: 'ready' } : prev);
      }
    }
  }, [identity, source, previousCursor, items, selection, handleError, translate.loadError]);

  const selectedItems = useMemo(() => {
    const available = new Map([...selectedCache.current, ...items.map((it) => [it.id, it] as const)]);
    return [...selection.selected].flatMap((id) => available.has(id) ? [available.get(id)!] : []);
  }, [items, selection.selected]);

  const removeLoadedIds = useCallback((ids: string[]) => {
    const set = new Set(ids);
    setItems((prev) => {
      setViewerIndex((idx) => (idx !== null && prev[idx] && set.has(prev[idx].id) ? null : idx));
      return prev.filter((it) => !set.has(it.id));
    });
    setTotal((prev) => (prev !== null ? Math.max(0, prev - ids.length) : prev));
    selection.clear();
    setNavigationRevision((v) => v + 1);
  }, [selection]);

  const reconcileAfterPartialMutation = useCallback(() => {
    selection.clear();
    setViewerIndex(null);
    refresh();
  }, [selection, refresh]);

  const patchItem = useCallback((id: string, patch: Partial<MediaItem>) => {
    if (selectedCache.current.has(id)) selectedCache.current.set(id, { ...selectedCache.current.get(id)!, ...patch } as MediaItem);
    setNavigationRevision((v) => v + 1);
    setItems((prev) => prev.map((it) => (it.id === id ? ({ ...it, ...patch } as MediaItem) : it)));
  }, []);

  const viewer = useMemo<MediaViewerController>(() => ({
    index: viewerIndex,
    isOpen: viewerIndex !== null,
    open: (index: number, atMs?: number) => {
      // Set the seek BEFORE the index so the first render of the newly opened
      // item already carries its position; opening without `atMs` clears any
      // previous one rather than letting it leak onto an unrelated item.
      setViewerSeekMs(atMs ?? null);
      setViewerIndex(index);
    },
    close: () => {
      setViewerSeekMs(null);
      setViewerIndex(null);
    },
    setIndex: (index: number) => {
      // In-viewer navigation to a different item: the previous item's semantic
      // position must not follow it.
      setViewerSeekMs(null);
      setViewerIndex(index);
    },
    seekMs: viewerSeekMs,
  }), [viewerIndex, viewerSeekMs]);

  return {
    items,
    selectedItems,
    navigationBusy,
    navigationRevision,
    previousCursor,
    scrollTarget,
    jumpTo: (target) => navigateWindow(target),
    loadPrevious: () => navigateWindow(null),
    orderedIds,
    total,
    photoCount,
    videoCount,
    semanticEvidence,
    phase,
    loading: phase.kind === 'loadingInitial',
    loadingMore: phase.kind === 'loadingMore',
    error: phase.kind === 'errorInitial' || phase.kind === 'errorMore' ? phase.message : null,
    hasMore: nextCursor !== null,
    semanticNotice,
    loadMore: () => startLoadMore(false),
    retryMore: () => startLoadMore(true),
    refresh,
    selection,
    viewer,
    removeLoadedIds,
    reconcileAfterPartialMutation,
    patchItem,
  };
}
