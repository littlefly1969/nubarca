// Expo config plugin: make the Fire TV launcher tile show the approved NubArca
// TV artwork instead of a small square icon.
//
// WHAT WAS ACTUALLY WRONG
// -----------------------
// Diagnosed from a clean prebuild and from physical Fire Stick acceptance, not
// guessed. Three independent defects, the third found only on hardware:
//
//  1. WRONG DENSITY. `@react-native-tvos/config-tv` takes the single
//     `androidTVBanner` file and copies it, unscaled, into EVERY density
//     bucket: drawable/, -mdpi, -hdpi, -xhdpi, -xxhdpi and -xxxhdpi all held
//     the identical 320x180 bitmap. A drawable's density bucket declares the
//     scale it was DESIGNED for, so the same file means different dp sizes in
//     different buckets.
//
//  2. NO ACTIVITY BANNER. `android:banner` was declared on <application> only.
//     Stock Leanback falls back to the application banner, but Fire OS ships
//     Amazon's own launcher, and several of its surfaces read the banner from
//     the LEANBACK_LAUNCHER activity and fall back to `android:icon` — the
//     square — when the activity does not declare one.
//
//  3. LOST REGISTRATION. An earlier version of this plugin REMOVED
//     `android.intent.category.LAUNCHER`, guessing that it was what made Fire
//     OS pick the square icon over the banner. On 1.0.6 hardware that guess
//     failed twice over: the square was still square, and the app stopped
//     appearing in the Fire TV Applications library after an ordinary in-place
//     update until "Move application" forced a launcher refresh. Both
//     categories are declared again — see withActivityBanner below.
//
// THE DENSITY RULE, STATED CORRECTLY
// ----------------------------------
// The Android TV banner spec is **320x180 px AT XHDPI**. xhdpi is 2x, so the
// banner is 160x90 **dp**, and the correct pixel size per bucket is:
//
//     mdpi     1x    160x90
//     hdpi     1.5x  240x135
//     xhdpi    2x    320x180   <- the approved asset, and Fire TV's own density
//     xxhdpi   3x    480x270
//     xxxhdpi  4x    640x360
//
// An earlier version of this plugin read the spec as "320x180 **dp**" and was
// then perfectly self-consistent with that wrong premise: 320x180 px went to
// -mdpi (1x) and the 1280x720 Fire TV artwork went to -xxxhdpi (4x), because
// 1280/4 = 320 and 720/4 = 180. Both placements are wrong by exactly the same
// factor of two, which is why the arithmetic looked convincing.
//
// What it did on hardware: a Fire TV reports xhdpi, and the tree offered only
// -mdpi and -xxxhdpi. Resource resolution takes the nearest LARGER bucket and
// rescales, so the device picked the 1280x720 bitmap and scaled it by 2/4 to
// 640x360 px — which at xhdpi is 320x180 dp, twice the 160x90 dp the banner
// slot actually wants. The launcher drew an oversized banner.
//
// Note also that 1280x720 is not a density variant of a 160x90 dp banner at
// all: it would need an 8x bucket, and Android's ladder stops at 4x. That asset
// is the Amazon Appstore/promotional artwork, it stays in the brand package,
// and it is deliberately NOT copied into any tv_banner bucket.
//
// WHAT THIS DOES
// --------------
// Places the ONE approved asset in the ONE bucket where its pixel size is
// exactly right, removes every other generated copy, and declares the banner on
// the launcher activity as well as the application:
//
//     assets/brand/nubarca-android-tv-banner-320x180.png    → drawable-xhdpi (2x)
//
// Nothing is redrawn, recoloured, resized or recomposed — this is placement.
// A device at another density resolves to the xhdpi entry and rescales it,
// which is ordinary Android behaviour and correct.

// ORDERING. The resource placement runs as a FINALIZED mod, not a dangerous
// one. Expo's mod compiler gives `dangerous` precedence -2 (runs first) and
// `finalized` precedence 1 (runs last), and config-tv copies its banner from a
// dangerous mod — so a dangerous mod here is overwritten by it no matter where
// this plugin sits in the `plugins` array. Verified empirically: the manifest
// edit below survived while an earlier dangerous-mod version of the copy did
// not. `finalized` is the only phase guaranteed to run after every copy.
const {
  AndroidConfig,
  withAndroidManifest,
  withFinalizedMod,
} = require('expo/config-plugins');
const fs = require('node:fs');
const path = require('node:path');

const BANNER_RESOURCE = 'tv_banner';
const LAUNCHER = 'android.intent.category.LAUNCHER';
const LEANBACK_LAUNCHER = 'android.intent.category.LEANBACK_LAUNCHER';
// Density buckets the config-tv plugin populates, and the approved asset that
// genuinely belongs in each. `null` means "delete it and let Android downscale
// from the next larger bucket" — never "leave a wrong-size bitmap there".
// 320x180 px IS the xhdpi (2x) rendering of a 160x90 dp banner. Every other
// bucket is emptied: `null` means "delete the copy config-tv left there", never
// "leave a wrongly-scaled bitmap behind". The 1280x720 Fire TV artwork appears
// nowhere here on purpose — see the density rule above.
const BANNER_BY_BUCKET = {
  'drawable-xhdpi': 'nubarca-android-tv-banner-320x180.png',
  drawable: null,
  'drawable-mdpi': null,
  'drawable-hdpi': null,
  'drawable-xxhdpi': null,
  'drawable-xxxhdpi': null,
};

const withBannerResources = (config) =>
  withFinalizedMod(config, [
    'android',
    (modConfig) => {
      const projectRoot = modConfig.modRequest.projectRoot;
      const res = path.join(
        modConfig.modRequest.platformProjectRoot, 'app', 'src', 'main', 'res');

      for (const [bucket, asset] of Object.entries(BANNER_BY_BUCKET)) {
        const target = path.join(res, bucket, `${BANNER_RESOURCE}.png`);
        if (asset === null) {
          if (fs.existsSync(target)) fs.rmSync(target);
          continue;
        }
        const source = path.join(projectRoot, 'assets', 'brand', asset);
        if (!fs.existsSync(source)) {
          // Fail loudly: silently shipping the wrong banner is the defect this
          // plugin exists to fix, and a missing approved asset must not
          // degrade into "whatever config-tv left behind".
          throw new Error(
            `withFireTvBanner: approved asset missing: ${source}. ` +
              'Run the brand sync before building; refusing to ship an ' +
              'unapproved or wrongly-scaled launcher banner.',
          );
        }
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(source, target);
      }
      return modConfig;
    },
  ]);

const withActivityBanner = (config) =>
  withAndroidManifest(config, (manifestConfig) => {
    const application = AndroidConfig.Manifest.getMainApplicationOrThrow(
      manifestConfig.modResults);
    const activity = AndroidConfig.Manifest.getMainActivityOrThrow(
      manifestConfig.modResults);

    // Keep the application banner (stock Leanback reads it) AND add the
    // activity banner (Fire OS's launcher reads it, and falls back to the
    // square android:icon without it).
    application.$['android:banner'] = `@drawable/${BANNER_RESOURCE}`;
    activity.$['android:banner'] = `@drawable/${BANNER_RESOURCE}`;

    // BOTH launcher categories. This previously stripped LAUNCHER and kept only
    // LEANBACK_LAUNCHER, on the theory that an ordinary launcher category would
    // make Fire OS register the square phone icon instead of the TV banner.
    //
    // Physical acceptance of 1.0.6 on a Fire Stick disproved that theory in
    // both directions at once: with LAUNCHER already removed, the tile in the
    // sideloaded-apps surface was STILL square, AND the app did not appear in
    // the Fire TV Applications library at all after an ordinary in-place
    // update — it showed up only after Fire OS "Move application" forced a
    // launcher refresh. Removing LAUNCHER bought nothing and cost the app its
    // registration.
    //
    // So this is a return to the ordinary, documented contract: Amazon's own
    // Fire TV samples declare MAIN with LAUNCHER and LEANBACK_LAUNCHER
    // together. The banner declarations above are what select TV artwork; the
    // categories are what make the app VISIBLE, and the two are independent.
    // Exactly one of each, so repeated prebuilds cannot accumulate duplicates.
    let hasMainIntent = false;
    for (const filter of activity['intent-filter'] ?? []) {
      const isMain = (filter.action ?? []).some(
        (action) => action.$?.['android:name'] === 'android.intent.action.MAIN',
      );
      if (!isMain) continue;
      hasMainIntent = true;
      const categories = [];
      const seen = new Set();
      for (const category of filter.category ?? []) {
        const name = category.$?.['android:name'];
        if (seen.has(name)) continue; // never duplicate what is already there
        seen.add(name);
        categories.push(category);
      }
      for (const required of [LAUNCHER, LEANBACK_LAUNCHER]) {
        if (seen.has(required)) continue;
        seen.add(required);
        categories.push({ $: { 'android:name': required } });
      }
      filter.category = categories;
    }
    if (!hasMainIntent) {
      throw new Error('withFireTvBanner: MainActivity has no MAIN intent');
    }
    return manifestConfig;
  });

/** @type {import('expo/config-plugins').ConfigPlugin} */
const withFireTvBanner = (config) => withActivityBanner(withBannerResources(config));

module.exports = withFireTvBanner;
