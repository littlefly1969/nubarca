// The NubArca typefaces, bundled locally (BRAND-TYPE-01).
//
// Space Grotesk carries display and heading roles; Exo 2 carries body and UI.
// Both are bundled as static instances under mobile/assets/fonts, with their
// provenance and checksums in fonts-manifest.json beside them.
//
// NOTHING IS FETCHED AT RUNTIME. There is no CDN, no @font-face over the wire,
// no network dependency in the type system — an app that needs the network to
// draw its own name is an app that looks broken on a train.
//
// A load FAILURE is survivable by construction. React Native falls back to the
// platform face when a fontFamily is unknown, so a device that cannot load the
// bundle renders in the system font with every size, weight and colour intact.
// That is why the boot sequence releases the splash on failure too: the app is
// usable without its typefaces, and unusable behind a splash that never lifts.

import { useFonts } from 'expo-font';

/**
 * Canonical family names. These are the strings React Native resolves, and
 * they must match the PostScript names inside the bundled files.
 */
export const fontFamilies = {
  // Display — Space Grotesk 500 / 600 / 700.
  displayMedium: 'SpaceGrotesk-Medium',
  displaySemiBold: 'SpaceGrotesk-SemiBold',
  displayBold: 'SpaceGrotesk-Bold',
  // UI — Exo 2 400 / 500 / 600. The brand contract allows no heavier UI weight.
  uiRegular: 'Exo2-Regular',
  uiMedium: 'Exo2-Medium',
  uiSemiBold: 'Exo2-SemiBold',
} as const;

export type FontFamily = (typeof fontFamilies)[keyof typeof fontFamilies];

// The loading map. Keyed by the same canonical names, so a style that names a
// family the bundle does not carry is a key that does not exist here.
export const fontAssets = {
  [fontFamilies.displayMedium]: require('../../assets/fonts/SpaceGrotesk-Medium.ttf'),
  [fontFamilies.displaySemiBold]: require('../../assets/fonts/SpaceGrotesk-SemiBold.ttf'),
  [fontFamilies.displayBold]: require('../../assets/fonts/SpaceGrotesk-Bold.ttf'),
  [fontFamilies.uiRegular]: require('../../assets/fonts/Exo2-Regular.ttf'),
  [fontFamilies.uiMedium]: require('../../assets/fonts/Exo2-Medium.ttf'),
  [fontFamilies.uiSemiBold]: require('../../assets/fonts/Exo2-SemiBold.ttf'),
};

/**
 * Load the bundle.
 *
 * `settled` is true once loading has finished, WHETHER OR NOT it succeeded —
 * it answers "may the native splash go away now?", not "did the fonts load?".
 * `loaded` reports the outcome for anything that wants to know.
 */
export function useBrandFonts(): { settled: boolean; loaded: boolean } {
  const [loaded, error] = useFonts(fontAssets);
  return { settled: loaded || error !== null, loaded };
}
