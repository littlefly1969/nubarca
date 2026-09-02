# NubArca Visual QA Matrix v1

Use this matrix for every brand-system slice.

## Required viewport/device matrix

### Mobile
- Android compact phone
- Android large phone
- iPhone compact/standard simulator or device
- iPhone large/notched simulator or device
- system Light + NubArca Dark
- system Dark + NubArca Light
- increased text size
- reduced motion

### Web
- 1280×720
- 1440×900
- 1920×1080
- mobile/narrow breakpoint
- keyboard-only navigation
- Dark / Light / System

### TV
- 1920×1080 reference
- overscan/title-safe review
- D-pad-only navigation
- focus visible from 10-foot distance
- low-light dark-surface review

## Brand checks

- [ ] Product spelling is exactly NubArca / NubArca TV.
- [ ] Correct dark/light logo variant is used.
- [ ] Flat mark is used in small chrome.
- [ ] No launcher icon is reused as a UI mark.
- [ ] No reference/source asset ships.
- [ ] Electric/Cyan/Violet semantics are correct.
- [ ] Cyan/Violet are scarce rather than decorative.
- [ ] Typography roles use approved families/weights.
- [ ] Shape roles use canonical radii.
- [ ] No heavy generic dashboard shadow language.
- [ ] Focus is visible.
- [ ] Reduced motion works.
- [ ] Text scales without clipping.
- [ ] Status is not color-only.

## Mobile launch checks

- [ ] Release build native splash uses Midnight Navy.
- [ ] Approved flat on-dark mark is centered and uncropped.
- [ ] No white flash before React root.
- [ ] Native splash does not wait on remote/session work.
- [ ] Branded boot state takes over slow restoration.
- [ ] Brand fonts load locally.
- [ ] Font failure still releases splash and leaves usable fallback.
- [ ] Light-theme destination transition is intentional.

## Screenshot naming

Use:

`<surface>__<platform>__<theme>__<viewport>__<state>.png`

Examples:

- `boot__android__brand-dark__pixel8__restoring.png`
- `home__web__dark__1440x900__default.png`
- `media__tv__dark__1920x1080__focused.png`

## Regression principle

A visual snapshot is evidence, not the design authority. If a snapshot conflicts with a brand invariant, the invariant wins and the snapshot must be updated.
