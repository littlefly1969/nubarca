import { StyleSheet, Text, View } from 'react-native';
import { colors, spacing } from '../theme';
import { getBaseUrl } from '../api/client';
import { QrCode } from './QrCode';
import { useI18n } from '../i18n';

// The canonical Party Guest Hub QR for the MENU overlay (grid + slideshow)
// stays consistently bottom-left. The card sits inside the overscan insets,
// is sized responsively by the caller, never takes focus
// (plain <Image>-based QR + pointerEvents none), and never resize the photo or
// grid underneath (absolute positioning).
interface Props {
  partyUrl: string | null;
  partyUploadUrl: string | null;
  insetX: number;
  insetY: number;
  qrSize: number;
}

export function OverlayQrCorners({ partyUrl, partyUploadUrl, insetX, insetY, qrSize }: Props) {
  const { t } = useI18n();
  // One visible QR: the canonical Guest Hub exposes browse, face search,
  // contribution and voting. partyUploadUrl remains in the API solely so old
  // printed upload QR codes keep working.
  void partyUploadUrl;
  const cards = [
    partyUrl
      ? { url: partyUrl, caption: t('items.partyGuestHub'), a11y: t('items.partyGuestHubQr') }
      : null,
  ].filter((c): c is NonNullable<typeof c> => c !== null);
  if (cards.length === 0) return null;

  const renderCard = (card: (typeof cards)[number], corner: 'left' | 'right') => (
    <View
      pointerEvents="none"
      style={[
        styles.card,
        corner === 'left' ? { left: insetX, bottom: insetY } : { right: insetX, bottom: insetY },
      ]}
    >
      <QrCode
        value={`${getBaseUrl()}${card.url}`}
        size={qrSize}
        style={styles.qr}
        accessibilityLabel={card.a11y}
      />
      <Text style={styles.caption} numberOfLines={1}>{card.caption}</Text>
    </View>
  );

  return (
    <>
      {renderCard(cards[0], 'left')}
      {cards[1] && renderCard(cards[1], 'right')}
    </>
  );
}

const styles = StyleSheet.create({
  card: {
    position: 'absolute',
    alignItems: 'center',
    gap: spacing.xs,
    padding: spacing.sm,
    borderRadius: 12,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  qr: { borderRadius: 6 },
  caption: { color: colors.text, fontSize: 16, fontWeight: '700' },
});
