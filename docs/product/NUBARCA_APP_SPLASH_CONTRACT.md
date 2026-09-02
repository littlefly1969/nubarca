# NubArca Mobile Splash & Boot Contract

**Invariant IDs:** BRAND-SPLASH-01, BRAND-BOOT-01  
**Target:** `mobile/` (Expo / React Native)

## Objective

Cold launch must feel like one NubArca experience rather than three unrelated states.

```
OS launch
  ↓
Native splash
  ↓
Branded React boot state
  ↓
Login OR authenticated application
```

## Native splash

### Visual
- Background: Midnight Navy `#0A0F1A`.
- Image: approved `nubarca-mark-flat-on-dark-256.png`.
- Render width target: 112–120 dp/pt class.
- `contain`; never cover/crop.
- Centered.
- No text, spinner or tagline.

### Source
The image is copied byte-for-byte from:

`assets/brand/nubarca/runtime/web/nubarca-mark-flat-on-dark-256.png`

to:

`mobile/assets/brand/nubarca-mark-flat-on-dark-256.png`

through `scripts/sync-brand-assets.py`.

This keeps the splash inside the existing approved brand package; no new logo binary is generated.

## Expo integration

Use the `expo-splash-screen` config plugin rather than relying on a generic top-level legacy splash field.

Proposed config:

```js
[
  'expo-splash-screen',
  {
    backgroundColor: '#0A0F1A',
    image: './assets/brand/nubarca-mark-flat-on-dark-256.png',
    imageWidth: 120,
    resizeMode: 'contain',
    dark: {
      backgroundColor: '#0A0F1A',
      image: './assets/brand/nubarca-mark-flat-on-dark-256.png',
    },
  },
],
```

The exact package version must be installed with `npx expo install expo-splash-screen` so Expo selects the SDK-compatible version.

## Boot lifecycle

1. Call `SplashScreen.preventAutoHideAsync()` at module scope.
2. Load critical local fonts.
3. Establish the React visual root.
4. Hide native splash.
5. If session is still restoring, render `BrandBootState`.
6. Route to login/authenticated stack when restoration resolves.

**Do not keep the native splash waiting for server/network/session restoration.**

## Branded React boot state

Use a theme-independent identity palette:

- `identityBootBackground`: Midnight Navy
- `identityBootForeground`: Cloud White
- `identityBootActivity`: Cyan Glow

Composition:

- approved on-dark wordmark at a visible 180–220 px class, or flat mark where space is constrained;
- restrained activity indicator below the lockup;
- optional localized status label (`Opening NubArca…` / existing restoring copy) after a short perceived delay;
- no cards, borders or generic system spinner as the dominant visual.

The boot state must support reduced motion.

## Light-theme behavior

Native launch remains Midnight Navy even when the user's eventual application theme is Light. Stored user preference is JS/application state and is not a reliable native-launch dependency.

The transition to a Light authenticated UI may fade only after the branded boot state is ready. Identity continuity takes priority over attempting to guess the saved preference in native configuration.

## Release verification

Test real release builds. Development clients may not reproduce Android's production splash behavior accurately.

Acceptance:
- no white flash;
- no app-icon tile used as splash art;
- no stretched art;
- no frame/halo from launcher artwork;
- no unbranded spinner-only restoring screen;
- startup does not wait indefinitely on remote work;
- font-load failure falls back and still releases the native splash.
