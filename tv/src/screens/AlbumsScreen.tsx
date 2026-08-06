import { useEffect, useState } from 'react';
import { ActivityIndicator, ScrollView, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { listTvAlbums, type TvAlbum } from '../api/tv';
import { ApiError } from '../api/client';
import { FocusableTile } from '../components/FocusableTile';
import { AuthedImage } from '../components/AuthedImage';
import { useI18n } from '../i18n';

interface Props {
  onOpenAlbum: (album: TvAlbum) => void;
  // Called on a 401 (session revoked/expired) so the app returns to pairing.
  onSessionInvalid: () => void;
}

// Shows only the owner's ShowOnTv-enabled albums. Refreshes periodically so an
// album the owner disables disappears without restarting the app.
export function AlbumsScreen({ onOpenAlbum, onSessionInvalid }: Props) {
  const { t, tn } = useI18n();
  const [albums, setAlbums] = useState<TvAlbum[] | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const load = () => {
      listTvAlbums()
        .then((list) => {
          if (!cancelled) {
            setAlbums(list);
            setError(false);
          }
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          if (err instanceof ApiError && err.status === 401) {
            onSessionInvalid();
            return;
          }
          setError(true);
        });
    };
    load();
    const timer = setInterval(load, 20000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [onSessionInvalid]);

  if (error) {
    return (
      <View style={styles.centered}>
        <Text style={styles.body}>{t('albums.loadError')}</Text>
      </View>
    );
  }

  if (albums === null) {
    return (
      <View style={styles.centered}>
        <ActivityIndicator size="large" color={colors.accent} />
      </View>
    );
  }

  if (albums.length === 0) {
    return (
      <View style={styles.centered}>
        <Text style={styles.heading}>{t('albums.title')}</Text>
        <Text style={styles.body}>{t('albums.empty')}</Text>
      </View>
    );
  }

  return (
    <View style={styles.container}>
      <Text style={styles.heading}>{t('albums.title')}</Text>
      <ScrollView contentContainerStyle={styles.grid}>
        {albums.map((album, i) => (
          <FocusableTile
            key={album.id}
            accessibilityLabel={t('albums.tileAccessibility', { name: album.name, count: album.itemCount })}
            style={styles.tile}
            hasTVPreferredFocus={i === 0}
            onSelect={() => onOpenAlbum(album)}
          >
            <AuthedImage path={album.coverThumbnailUrl} style={styles.cover} />
            <Text style={styles.name} numberOfLines={1}>{album.name}</Text>
            <Text style={styles.count}>{tn(album.itemCount, 'albums.itemCount')}</Text>
          </FocusableTile>
        ))}
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.bg, padding: spacing.lg },
  centered: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.bg,
    padding: spacing.xl,
  },
  heading: { color: colors.text, fontSize: font.heading, fontWeight: '700', marginBottom: spacing.md },
  body: { color: colors.muted, fontSize: font.body, marginTop: spacing.md, textAlign: 'center' },
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.md },
  tile: { width: 300, padding: spacing.sm },
  cover: { width: '100%', height: 220, borderRadius: 10 },
  name: { color: colors.text, fontSize: font.body, fontWeight: '600', marginTop: spacing.xs },
  count: { color: colors.muted, fontSize: font.caption },
});
