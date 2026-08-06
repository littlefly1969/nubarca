import { useEffect, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from './PanelShell';
import {
  addPersonalItemsToAlbum,
  listPersonalAlbums,
  type TvPersonalAlbum,
  type TvPersonalAlbumAddResult,
} from '../../api/personalGallery';
import { useI18n } from '../../i18n';

// Album picker for the selection-mode "add to album" bulk action (the ONLY
// bulk action the web gallery has). Non-destructive and idempotent server-side
// (duplicates are skipped), so a single SELECT on an album performs the add;
// the result (added/skipped counts) is reported back to the gallery. Creating
// a NEW album needs free-text entry and stays a web-only affordance.
interface Props {
  fileItemIds: string[];
  onDone: (result: TvPersonalAlbumAddResult, albumName: string) => void;
  onCancel: () => void;
  onAuthError: (err: unknown) => boolean;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; albums: TvPersonalAlbum[] }
  | { kind: 'error' };

export function GalleryAlbumPanel({ fileItemIds, onDone, onCancel, onAuthError }: Props) {
  const { t, tn } = useI18n();
  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [reloadKey, setReloadKey] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoad({ kind: 'loading' });
    listPersonalAlbums()
      .then((albums) => {
        if (!cancelled) setLoad({ kind: 'ready', albums });
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (onAuthError(err)) return;
        setLoad({ kind: 'error' });
      });
    return () => {
      cancelled = true;
    };
  }, [reloadKey, onAuthError]);

  const addTo = async (album: TvPersonalAlbum) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await addPersonalItemsToAlbum(album.id, fileItemIds);
      onDone(result, album.name);
    } catch (err) {
      if (!onAuthError(err)) setError(t('gallery.albumError'));
      setBusy(false);
    }
  };

  return (
    <PanelShell title={t('gallery.albumTitle')} onBack={onCancel}>
      <Text style={styles.muted}>{tn(fileItemIds.length, 'gallery.selectedCount')}</Text>
      {load.kind === 'loading' && <ActivityIndicator color={colors.accent} />}
      {load.kind === 'error' && (
        <>
          <Text style={styles.muted}>{t('gallery.albumLoadError')}</Text>
          <FocusableButton
            label={t('common.tryAgain')}
            onPress={() => setReloadKey((k) => k + 1)}
            hasTVPreferredFocus
          />
        </>
      )}
      {load.kind === 'ready' && load.albums.length === 0 && (
        <Text style={styles.muted}>{t('gallery.albumEmpty')}</Text>
      )}
      {load.kind === 'ready' && load.albums.map((album, index) => (
        <FocusableButton
          key={album.id}
          label={`${album.name} (${album.itemCount})`}
          onPress={() => void addTo(album)}
          disabled={busy}
          hasTVPreferredFocus={index === 0}
        />
      ))}
      {error !== null && <Text style={styles.error}>{error}</Text>}
      <FocusableButton label={t('gallery.cancel')} onPress={onCancel} disabled={busy} />
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  muted: {
    color: colors.muted,
    fontSize: font.body,
    textAlign: 'center',
    marginBottom: spacing.md,
  },
  error: {
    color: colors.danger,
    fontSize: font.body,
    textAlign: 'center',
    marginVertical: spacing.sm,
  },
});
