import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  BackHandler,
  Pressable,
  StyleSheet,
  Text,
  View,
  useTVEventHandler,
  useWindowDimensions,
  type HWEvent,
} from 'react-native';
import { colors, font, overscan, spacing } from '../../theme';
import { loadTvMedia } from '../../api/client';
import { getPersonalMediaInfo, type TvPersonalGalleryItem, type TvPersonalMediaInfo } from '../../api/personalGallery';
import { SlideImage } from '../../components/SlideImage';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from './PanelShell';
import { useMenuOverlay } from '../../lib/useMenuOverlay';
import { useScreenAwake } from '../../lib/useScreenAwake';
import { useI18n } from '../../i18n';
import { tvDebug } from '../../debug';

// Personal Gallery viewer — the proven TV slideshow model (SlideImage: contain
// foreground over a blurred cover background; previous slide stays visible
// until the next foreground decodes; never originals) navigating the CURRENT
// filtered gallery result set:
//  - overlay HIDDEN: LEFT/RIGHT = prev/next, play/pause key toggles the
//    slideshow, MENU shows the overlay, BACK returns to the grid;
//  - overlay VISIBLE: the command bar (favorite / details / play-pause) is
//    focusable — LEFT/RIGHT move BETWEEN commands (photo navigation pauses
//    while commands are focused; hide the overlay to resume), the counter
//    reflects the filtered set, BACK hides the overlay first.
// Nearing the end of the loaded items asks the gallery for the next cursor
// page (bounded prefetch: only the adjacent previews are warmed).
const SLIDE_MS = 9000;
// Ask for the next page this many items before the end of the loaded set.
const NEED_MORE_AHEAD = 3;

interface Props {
  items: TvPersonalGalleryItem[];
  startIndex: number;
  // Server-authoritative total for the current filtered query (the counter
  // denominator). Stable across page loads — never items.length.
  totalCount: number;
  hasMore: boolean;
  loadingMore: boolean;
  onNeedMore: () => void;
  // Reports the index the user is on so the grid restores focus to it.
  onClose: (currentIndex: number) => void;
  // Server-confirmed favorite mutation (the parent reconciles the item list).
  onToggleFavorite: (id: string, favorite: boolean) => Promise<void>;
  onAuthError: (err: unknown) => boolean;
}

export function PersonalViewer({
  items, startIndex, totalCount, hasMore, loadingMore, onNeedMore, onClose, onToggleFavorite, onAuthError,
}: Props) {
  const { t } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);
  const [index, setIndex] = useState(startIndex);
  const [playing, setPlaying] = useState(false);
  const [infoOpen, setInfoOpen] = useState(false);
  const [favoriteBusy, setFavoriteBusy] = useState(false);
  const [favoriteError, setFavoriteError] = useState(false);
  const {
    visible: overlayVisible,
    visibleRef: overlayVisibleRef,
    toggle: toggleOverlay,
    hide: hideOverlay,
    bump: bumpOverlay,
  } = useMenuOverlay();

  const clampedIndex = Math.min(index, Math.max(0, items.length - 1));
  const item = items[clampedIndex];

  // Keep the screen awake while the slideshow image is on screen. The details
  // sheet (infoOpen) replaces the slideshow with a full-screen panel, so it is
  // NOT a viewer — release there. Unmounting the viewer (exit, lock,
  // revocation, session loss) always releases via the hook's cleanup.
  useScreenAwake(!infoOpen);

  const itemsRef = useRef(items);
  itemsRef.current = items;
  const indexRef = useRef(clampedIndex);
  indexRef.current = clampedIndex;
  const infoOpenRef = useRef(infoOpen);
  useEffect(() => { infoOpenRef.current = infoOpen; }, [infoOpen]);
  const hasMoreRef = useRef(hasMore);
  hasMoreRef.current = hasMore;
  const onNeedMoreRef = useRef(onNeedMore);
  onNeedMoreRef.current = onNeedMore;

  const goNext = useCallback(() => {
    const len = itemsRef.current.length;
    if (len === 0) return;
    setIndex((i) => {
      const current = Math.min(i, len - 1);
      if (current >= len - 1) {
        // At the end: with more pages pending stay put (the page request below
        // extends the list); with the full set loaded, loop like the slideshow.
        return hasMoreRef.current ? current : 0;
      }
      return current + 1;
    });
  }, []);

  const goPrev = useCallback(() => {
    const len = itemsRef.current.length;
    if (len === 0) return;
    setIndex((i) => Math.max(0, Math.min(i, len - 1) - 1));
  }, []);

  const togglePlay = useCallback(() => setPlaying((p) => !p), []);

  // Cross-page navigation: nearing the end of the loaded set requests the next
  // cursor page (the parent ignores duplicate requests while one is in flight).
  useEffect(() => {
    if (hasMore && clampedIndex >= items.length - NEED_MORE_AHEAD) {
      onNeedMoreRef.current();
    }
  }, [clampedIndex, items.length, hasMore]);

  // Remote events. While the overlay is visible its commands are FOCUSABLE, so
  // LEFT/RIGHT belong to the native focus engine — the handler only navigates
  // photos while the overlay is hidden (and the info panel is closed).
  const onTVEvent = useCallback((evt: HWEvent) => {
    if (!evt || evt.eventKeyAction === 0) return;
    if (infoOpenRef.current) return;
    const type = evt.eventType;
    tvDebug('remote', type, 'personal-viewer overlay', overlayVisibleRef.current);
    if (type === 'menu') {
      toggleOverlay();
      return;
    }
    if (type === 'playPause') {
      togglePlay();
      bumpOverlay();
      return;
    }
    if (overlayVisibleRef.current) {
      bumpOverlay();
      return;
    }
    if (type === 'right' || type === 'longRight') goNext();
    else if (type === 'left' || type === 'longLeft') goPrev();
  }, [goNext, goPrev, togglePlay, toggleOverlay, bumpOverlay, overlayVisibleRef]);
  useTVEventHandler(onTVEvent);

  // BACK: info panel first (it registers its own handler), then the overlay,
  // then back to the grid — normal navigation, NEVER a lock.
  useEffect(() => {
    const onBackPress = () => {
      if (infoOpenRef.current) return false; // the info panel's handler owns it
      if (overlayVisibleRef.current) {
        hideOverlay();
        return true;
      }
      onClose(indexRef.current);
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onClose, hideOverlay, overlayVisibleRef]);

  // Auto-advance (explicitly started with the play/pause key or the overlay
  // button); re-armed on each index change.
  useEffect(() => {
    if (!playing || items.length === 0) return;
    const timer = setTimeout(goNext, SLIDE_MS);
    return () => clearTimeout(timer);
  }, [playing, clampedIndex, items.length, goNext]);

  // Warm ONLY the adjacent previews (low priority, grant-gated).
  useEffect(() => {
    if (items.length < 2) return;
    const warm = (it: TvPersonalGalleryItem | undefined) => {
      if (it) void loadTvMedia(it.previewUrl, { priority: 'low', personal: true }).catch(() => { /* warm-up only */ });
    };
    warm(items[clampedIndex + 1]);
    warm(items[clampedIndex - 1]);
  }, [clampedIndex, items]);

  // Favorite state changes arrive through the items prop (parent reconciles
  // after the server confirms) — clear the transient error when they do.
  useEffect(() => { setFavoriteError(false); }, [item?.favorite, clampedIndex]);

  const toggleFavorite = useCallback(async () => {
    const current = itemsRef.current[indexRef.current];
    if (!current || favoriteBusy) return;
    setFavoriteBusy(true);
    setFavoriteError(false);
    try {
      await onToggleFavorite(current.id, !current.favorite);
    } catch (err) {
      if (!onAuthError(err)) setFavoriteError(true);
    } finally {
      setFavoriteBusy(false);
      bumpOverlay();
    }
  }, [favoriteBusy, onToggleFavorite, onAuthError, bumpOverlay]);

  if (infoOpen && item) {
    return (
      <View style={StyleSheet.absoluteFill}>
        <MediaInfoPanel
          fileId={item.id}
          onClose={() => setInfoOpen(false)}
          onAuthError={onAuthError}
        />
      </View>
    );
  }

  return (
    <View style={[StyleSheet.absoluteFill, styles.container]}>
      <SlideImage path={item?.previewUrl ?? null} personal />

      {/* Focus anchor: while the overlay is hidden the screen owns the DPAD
          (SELECT reserved, matching the Party viewer). */}
      {!overlayVisible && (
        <Pressable
          focusable
          hasTVPreferredFocus
          accessibilityLabel={t('viewer.navHint')}
          onPress={() => { /* SELECT reserved — MENU owns the overlay */ }}
          style={styles.capture}
        />
      )}

      {overlayVisible && item && (
        <>
          <View style={[styles.titleRow, { top: inset.y }]} pointerEvents="none">
            <View style={styles.pill}>
              <Text style={styles.titleText} numberOfLines={1}>{item.name}</Text>
            </View>
          </View>

          <View style={[styles.counterPill, { bottom: inset.y + 84 }]} pointerEvents="none">
            <Text style={[styles.playbackText, playing ? styles.playingText : styles.pausedText]}>
              {playing ? t('viewer.playing') : t('viewer.paused')}
            </Text>
            <Text style={styles.separatorText}>•</Text>
            <Text style={styles.counterText}>
              {/* Absolute position (loaded items are a prefix of the ordered
                  result, so the loaded index IS the absolute position) over the
                  server total — never the loaded-items length. */}
              {clampedIndex + 1} / {totalCount}
            </Text>
            {loadingMore && (
              <ActivityIndicator color={colors.muted} style={styles.counterSpinner} />
            )}
          </View>

          {favoriteError && (
            <View style={[styles.errorRow, { bottom: inset.y + 140 }]} pointerEvents="none">
              <Text style={styles.errorText}>{t('gallery.favoriteError')}</Text>
            </View>
          )}

          <View style={[styles.commandBar, { left: inset.x, right: inset.x, bottom: inset.y }]}>
            <FocusableButton
              label={item.favorite ? t('gallery.favoriteOn') : t('gallery.favoriteOff')}
              onPress={() => void toggleFavorite()}
              disabled={favoriteBusy}
              onFocusChange={(f) => { if (f) bumpOverlay(); }}
              hasTVPreferredFocus
            />
            <FocusableButton
              label={t('gallery.details')}
              onPress={() => setInfoOpen(true)}
              onFocusChange={(f) => { if (f) bumpOverlay(); }}
            />
            <FocusableButton
              label={playing ? t('viewer.paused') : t('viewer.playing')}
              onPress={togglePlay}
              onFocusChange={(f) => { if (f) bumpOverlay(); }}
            />
          </View>
        </>
      )}
    </View>
  );
}

// Curated per-item metadata (the TV-safe owner projection: GPS presence only,
// user annotations, capture data). Fetched on open, never per navigation step.
function MediaInfoPanel({
  fileId,
  onClose,
  onAuthError,
}: {
  fileId: string;
  onClose: () => void;
  onAuthError: (err: unknown) => boolean;
}) {
  const { t, formatDate } = useInfoI18n();
  const [state, setState] = useState<
    { kind: 'loading' } | { kind: 'ready'; info: TvPersonalMediaInfo } | { kind: 'error' }
  >({ kind: 'loading' });

  useEffect(() => {
    let cancelled = false;
    getPersonalMediaInfo(fileId)
      .then((info) => {
        if (!cancelled) setState({ kind: 'ready', info });
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (onAuthError(err)) return;
        setState({ kind: 'error' });
      });
    return () => {
      cancelled = true;
    };
  }, [fileId, onAuthError]);

  const rows: Array<[string, string]> = [];
  if (state.kind === 'ready') {
    const info = state.info;
    rows.push([t('gallery.infoDate'), formatDate(info.dateTaken)]);
    if (info.width !== null && info.height !== null) {
      rows.push([t('gallery.infoDimensions'), `${info.width}×${info.height}`]);
    }
    rows.push([t('gallery.infoSize'), formatBytes(info.sizeBytes)]);
    const camera = [info.cameraMake, info.cameraModel].filter(Boolean).join(' ');
    if (camera) rows.push([t('gallery.infoCamera'), camera]);
    if (info.lensModel) rows.push([t('gallery.infoLens'), info.lensModel]);
    if (info.iso !== null) rows.push(['ISO', String(info.iso)]);
    if (info.aperture !== null) rows.push([t('gallery.infoAperture'), `f/${info.aperture}`]);
    if (info.exposureTime) rows.push([t('gallery.infoExposure'), info.exposureTime]);
    if (info.focalLength !== null) rows.push([t('gallery.infoFocal'), `${info.focalLength} mm`]);
    rows.push([
      t('gallery.infoGps'),
      info.hasGps ? t('gallery.gpsPresent') : t('gallery.gpsNone'),
    ]);
    if (info.title) rows.push([t('gallery.infoTitleField'), info.title]);
    if (info.description) rows.push([t('gallery.infoDescription'), info.description]);
    if (info.tags.length > 0) rows.push([t('gallery.infoTags'), info.tags.join(', ')]);
    if (info.rating !== null) rows.push([t('gallery.infoRating'), `${info.rating}/5`]);
    if (info.favorite) rows.push([t('gallery.favorite'), '♥']);
    if (info.location) rows.push([t('gallery.infoLocation'), info.location]);
  }

  return (
    <PanelShell title={t('gallery.infoTitle')} onBack={onClose}>
      {state.kind === 'loading' && <ActivityIndicator color={colors.accent} />}
      {state.kind === 'error' && <Text style={styles.infoError}>{t('gallery.infoError')}</Text>}
      {state.kind === 'ready' && rows.map(([label, value]) => (
        <View key={label} style={styles.infoRow}>
          <Text style={styles.infoLabel} numberOfLines={1}>{label}</Text>
          <Text style={styles.infoValue} numberOfLines={3}>{value}</Text>
        </View>
      ))}
      <View style={styles.infoActions}>
        <FocusableButton label={t('gallery.done')} onPress={onClose} hasTVPreferredFocus />
      </View>
    </PanelShell>
  );
}

// Thin wrapper adding a locale-aware date formatter to the TV i18n context.
function useInfoI18n() {
  const i18n = useI18n();
  const formatDate = (iso: string): string => {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    return date.toLocaleString(i18n.lang === 'it' ? 'it-IT' : 'en-GB', {
      year: 'numeric', month: 'long', day: 'numeric', hour: '2-digit', minute: '2-digit',
    });
  };
  return { ...i18n, formatDate };
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 'B';
  for (const next of units) {
    if (value < 1024) break;
    value /= 1024;
    unit = next;
  }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${unit}`;
}

const styles = StyleSheet.create({
  container: { backgroundColor: '#05070b' },
  capture: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0 },
  titleRow: { position: 'absolute', left: 0, right: 0, alignItems: 'center' },
  pill: {
    maxWidth: '60%',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 12,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  titleText: { color: colors.text, fontSize: font.body, fontWeight: '700' },
  counterPill: {
    position: 'absolute',
    alignSelf: 'center',
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  counterText: { color: colors.text, fontSize: 24, fontWeight: '800' },
  counterSpinner: { marginLeft: spacing.sm },
  playbackText: { fontSize: 22, fontWeight: '900' },
  playingText: { color: '#7ee787' },
  pausedText: { color: '#ffd166' },
  separatorText: { color: colors.muted, fontSize: 20, marginHorizontal: spacing.sm },
  errorRow: { position: 'absolute', left: 0, right: 0, alignItems: 'center' },
  errorText: {
    color: colors.danger,
    fontSize: font.body,
    backgroundColor: 'rgba(0,0,0,0.78)',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 12,
  },
  commandBar: {
    position: 'absolute',
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderRadius: 14,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  infoRow: { flexDirection: 'row', gap: spacing.md, alignItems: 'flex-start' },
  infoLabel: { color: colors.muted, fontSize: font.body, width: 280, textAlign: 'right' },
  infoValue: { color: colors.text, fontSize: font.body, flex: 1 },
  infoError: { color: colors.danger, fontSize: font.body, textAlign: 'center' },
  infoActions: { alignItems: 'center', marginTop: spacing.lg },
});
