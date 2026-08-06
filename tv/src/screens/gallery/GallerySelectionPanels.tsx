import { useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { FocusableButton } from '../../components/FocusableButton';
import { colors, font, spacing } from '../../theme';
import { useI18n } from '../../i18n';
import {
  addPersonalItemsToDestination,
  trashPersonalGalleryItems,
  type PersonalGalleryDestination,
  type TvPersonalGalleryBulkResult,
} from '../../api/personalGallery';
import { PanelShell } from './PanelShell';

interface CommonProps {
  fileItemIds: string[];
  onDone: (result: TvPersonalGalleryBulkResult, label: string) => void;
  onCancel: () => void;
  onAuthError: (err: unknown) => boolean;
}

interface DestinationDefinition {
  key: PersonalGalleryDestination;
  it: string;
  en: string;
}

// Small typed registry: future owner-private containers can be added without
// growing permanent buttons in the Gallery HUD.
const DESTINATIONS: readonly DestinationDefinition[] = [
  { key: 'beauty-lab', it: 'Laboratorio bellezza', en: 'Beauty Lab' },
  { key: 'plates', it: 'Targhe', en: 'Plates' },
];

export function GalleryDestinationPanel({
  fileItemIds, onDone, onCancel, onAuthError,
}: CommonProps) {
  const { lang } = useI18n();
  const [busy, setBusy] = useState<PersonalGalleryDestination | null>(null);
  const [error, setError] = useState<string | null>(null);

  const run = async (destination: DestinationDefinition) => {
    if (busy !== null || fileItemIds.length === 0) return;
    setBusy(destination.key);
    setError(null);
    try {
      const result = await addPersonalItemsToDestination(destination.key, fileItemIds);
      onDone(result, lang === 'it' ? destination.it : destination.en);
    } catch (err) {
      if (!onAuthError(err)) {
        setError(lang === 'it' ? 'Operazione non completata. Riprova.' : 'The action could not be completed. Try again.');
      }
    } finally {
      setBusy(null);
    }
  };

  return (
    <PanelShell title={lang === 'it' ? 'Aggiungi a' : 'Add to'} onBack={onCancel}>
      <Text style={styles.message}>
        {lang === 'it' ? `${fileItemIds.length} foto selezionate` : `${fileItemIds.length} selected photos`}
      </Text>
      {DESTINATIONS.map((destination, index) => (
        <FocusableButton
          key={destination.key}
          label={lang === 'it' ? destination.it : destination.en}
          disabled={busy !== null}
          hasTVPreferredFocus={index === 0}
          onPress={() => { void run(destination); }}
        />
      ))}
      {busy !== null && <ActivityIndicator color={colors.accent} />}
      {error !== null && <Text style={styles.error}>{error}</Text>}
      <FocusableButton label={lang === 'it' ? 'Annulla' : 'Cancel'} onPress={onCancel} disabled={busy !== null} />
    </PanelShell>
  );
}

export function GalleryTrashConfirmPanel({
  fileItemIds, onDone, onCancel, onAuthError,
}: CommonProps) {
  const { lang } = useI18n();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const move = async () => {
    if (busy || fileItemIds.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      const result = await trashPersonalGalleryItems(fileItemIds);
      onDone(result, lang === 'it' ? 'Cestino' : 'Trash');
    } catch (err) {
      if (!onAuthError(err)) {
        setError(lang === 'it' ? 'Spostamento non completato. Riprova.' : 'Move could not be completed. Try again.');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <PanelShell title={lang === 'it' ? `Spostare ${fileItemIds.length} foto nel Cestino?` : `Move ${fileItemIds.length} photos to Trash?`} onBack={onCancel}>
      <Text style={styles.message}>
        {lang === 'it' ? 'Potrai ripristinarle dal Cestino.' : 'They can be restored from Trash.'}
      </Text>
      <View style={styles.actions}>
        <FocusableButton
          label={lang === 'it' ? 'Annulla' : 'Cancel'}
          onPress={onCancel}
          disabled={busy}
          hasTVPreferredFocus
        />
        <FocusableButton
          label={lang === 'it' ? 'Sposta nel Cestino' : 'Move to Trash'}
          onPress={() => { void move(); }}
          disabled={busy || fileItemIds.length === 0}
        />
      </View>
      {busy && <ActivityIndicator color={colors.accent} />}
      {error !== null && <Text style={styles.error}>{error}</Text>}
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  message: { color: colors.text, fontSize: font.body, textAlign: 'center', marginBottom: spacing.md },
  error: { color: '#ff8f8f', fontSize: font.body, textAlign: 'center' },
  actions: { flexDirection: 'row', justifyContent: 'center', gap: spacing.md },
});
