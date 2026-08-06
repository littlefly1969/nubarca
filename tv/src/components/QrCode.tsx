import { useMemo } from 'react';
import { Image, type ImageStyle, type StyleProp } from 'react-native';
import qrcode from 'qrcode-generator';

// Pure-JS QR rendering. `qrcode-generator` has no native modules and no Node
// built-ins: it encodes the value and produces a GIF data URI in JS, which RN's
// <Image> renders directly. We encode the pairing approval URL (public by
// design — the phone opens it to approve; the secret lives in the URL fragment,
// same as the web /tv QR). No server-side token hashes are exposed.
interface Props {
  value: string;
  size?: number;
  style?: StyleProp<ImageStyle>;
  accessibilityLabel?: string;
}

export function QrCode({ value, size = 320, style, accessibilityLabel = 'Pairing QR code' }: Props) {
  const uri = useMemo(() => {
    const qr = qrcode(0, 'M');
    qr.addData(value);
    qr.make();
    return qr.createDataURL(6, 12);
  }, [value]);

  return (
    <Image
      accessibilityLabel={accessibilityLabel}
      source={{ uri }}
      style={[{ width: size, height: size, backgroundColor: '#fff' }, style]}
      resizeMode="contain"
    />
  );
}
