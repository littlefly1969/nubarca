import type { MessageKey } from '../i18n';
import './textFonts.css';

/**
 * The few faces an owner may choose for words of their own: the line in a
 * framed print's band and the text of a lithophane. Mirrors the server's
 * TextFonts table; the browser draws with the very same files (served from
 * /fonts/text/), so the preview's letters are the paper's.
 *
 * 'nubarca' is the face these words were always drawn with (Space Grotesk
 * Bold, already part of the app) and the default: a request that names no
 * font comes out exactly as before.
 */
export type TextFontId = 'nubarca' | 'serif' | 'hand' | 'marker' | 'script';

export interface TextFont {
  id: TextFontId;
  label: MessageKey;
  /** CSS font-family for the preview, with the weight it is drawn at. */
  family: string;
  weight: number;
}

export const DEFAULT_TEXT_FONT: TextFontId = 'nubarca';

export const TEXT_FONTS: readonly TextFont[] = [
  { id: 'nubarca', label: 'textFont.nubarca', family: "'Space Grotesk', sans-serif", weight: 700 },
  { id: 'serif', label: 'textFont.serif', family: "'NubArca Text Serif', serif", weight: 400 },
  { id: 'hand', label: 'textFont.hand', family: "'NubArca Text Hand', cursive", weight: 400 },
  { id: 'marker', label: 'textFont.marker', family: "'NubArca Text Marker', cursive", weight: 400 },
  { id: 'script', label: 'textFont.script', family: "'NubArca Text Script', cursive", weight: 400 },
];

export function textFont(id: string | null | undefined): TextFont {
  return TEXT_FONTS.find((font) => font.id === id) ?? TEXT_FONTS[0];
}

/** The request value: the default is sent as absent, so earlier requests stay identical. */
export function textFontRequest(id: TextFontId): TextFontId | undefined {
  return id === DEFAULT_TEXT_FONT ? undefined : id;
}
