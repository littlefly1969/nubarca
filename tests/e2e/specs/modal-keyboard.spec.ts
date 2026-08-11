// Who owns a keystroke when a modal is stacked on a viewer.
//
// This spec exists because the defect it guards is made of things only a real
// engine has: two listeners on the same `window`, capture and bubble ordering
// between them, and the browser's own caret handling inside an input. The user
// hit it by hand — open a photo, open a dialog over it, put the caret in the
// dialog's search field, press ArrowLeft to fix a typo, and THE PHOTO BEHIND
// CHANGED. One Escape then closed the dialog and the photo with it.
//
// A component test can assert the handler was not called. It cannot assert that
// the caret really moved, that nothing was preventDefault-ed, and that the two
// surfaces are genuinely stacked — which is the whole substance of the bug.
//
// The contract: THE TOPMOST MODAL OWNS THE KEYBOARD, and gives it back intact
// when it closes.

import { expect, test } from '../src/fixtures';
import { settledMedia as settled } from '../src/appShell';

/** Open the media viewer on the first photo, then the album picker over it. */
async function openPickerOverViewer(page: import('@playwright/test').Page) {
  await page.goto('/media');
  await settled(page);

  // Photos, deliberately. A video fills the stage with a `<video controls>` that
  // sits over the chrome and swallows the click on the details toggle — the
  // drawer this spec needs is then unreachable for a reason that has nothing to
  // do with the keyboard.
  await page.getByTestId('media-kind-tab-image').click();
  await expect(page.getByTestId('media-kind-tab-image')).toHaveAttribute('aria-selected', 'true');
  await settled(page);

  await page.getByTestId('media-open').first().click();
  const title = page.getByTestId('media-viewer-title');
  await expect(title).toBeVisible({ timeout: 20_000 });
  const openedOn = (await title.textContent())?.trim() ?? '';
  expect(openedOn.length, 'the viewer opened on a real item').toBeGreaterThan(0);

  // The picker is opened from the viewer's own details drawer — the stacking
  // this spec is about.
  await page.getByTestId('viewer-details-toggle').click();
  await page.getByTestId('add-to-album-btn').click();
  await expect(page.getByTestId('album-picker')).toBeVisible();

  return { title, openedOn };
}

/** The caret position and value of the picker's search field. */
function searchState(page: import('@playwright/test').Page) {
  return page.evaluate(() => {
    const el = document.querySelector<HTMLInputElement>('[data-testid="album-picker-search"]');
    if (!el) throw new Error('the picker search field is not on screen');
    return { value: el.value, caret: el.selectionStart, focused: document.activeElement === el };
  });
}

test.describe('a modal over the media viewer owns the keyboard', () => {
  test('arrows move the caret in the dialog, never the photo behind it', async ({ ownerPage }) => {
    const page = ownerPage;
    const { title, openedOn } = await openPickerOverViewer(page);

    const search = page.getByTestId('album-picker-search');
    await search.click();
    await search.fill('Vacanze');
    await expect(search).toHaveValue('Vacanze');

    // Caret in the middle of the text, where somebody fixing a typo puts it.
    await search.evaluate((el: HTMLInputElement) => el.setSelectionRange(4, 4));
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowRight');

    const after = await searchState(page);
    // The browser's own behaviour is untouched: the caret moved, the text did
    // not, and focus never left the field.
    expect(after.value, 'the arrows edited the text').toBe('Vacanze');
    expect(after.caret, 'the caret did not move as the browser would').toBe(3);
    expect(after.focused, 'focus left the search field').toBe(true);

    // The decisive one. This is what the user saw change.
    await expect(title, 'the photo behind the dialog changed').toHaveText(openedOn);
    await expect(page.getByTestId('album-picker'), 'an arrow dismissed the dialog').toBeVisible();
  });

  test('Escape closes only the dialog, and the viewer navigates again after it', async ({
    ownerPage,
  }) => {
    const page = ownerPage;
    const { title, openedOn } = await openPickerOverViewer(page);

    const search = page.getByTestId('album-picker-search');
    await search.click();
    await search.fill('Vac');

    await page.keyboard.press('Escape');

    // The topmost surface, and only the topmost surface.
    await expect(page.getByTestId('album-picker')).toHaveCount(0);
    await expect(title, 'Escape dropped the user out of the photo as well').toBeVisible();
    await expect(title).toHaveText(openedOn);

    // With the dialog gone the viewer owns the keyboard again — the isolation
    // must be scoped to the modal's lifetime, not a permanent mute.
    await page.keyboard.press('ArrowRight');
    await expect
      .poll(async () => (await title.textContent())?.trim(), {
        message: 'the viewer stopped answering the arrow keys after the dialog closed',
        timeout: 10_000,
      })
      .not.toBe(openedOn);

    // And Escape still closes the viewer itself.
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('media-viewer-title')).toHaveCount(0);
  });
});
