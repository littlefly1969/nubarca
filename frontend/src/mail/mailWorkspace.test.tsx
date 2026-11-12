import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useRef } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { emptyResponse, installFetchMock, jsonResponse, AuthedWrapper } from '../test-utils';
import { AppScrollProvider } from '../components/appScroll';
import { archive, currentLocation, detail, FOLDERS, renderMail, summary } from './mail.test.fixtures';
import { MailPage } from './MailPage';
import {
  DEFAULT_MAIL_LAYOUT, FOLDERS_MIN, LIST_MIN, MAIL_LAYOUT_STORAGE_KEY, parseMailLayout, resolveMailLayout,
  type MailLayoutPrefs,
} from './useMailLayout';

beforeEach(() => { window.localStorage.clear(); });
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

function handlers(count = 3) {
  const items = Array.from({ length: count }, (_, i) => summary(`m${i + 1}`));
  return {
    'GET /api/mail/archives': () => jsonResponse({ items: [archive()] }),
    'GET /api/mail/archives/arc-1/folders': () => jsonResponse({ items: FOLDERS }),
    'GET /api/mail/messages': () => jsonResponse({ items, nextCursor: null }),
    'GET /api/mail/messages/m1': () => jsonResponse(detail('m1')),
    'GET /api/mail/messages/m2': () => jsonResponse(detail('m2', { subject: 'Secondo' })),
    'DELETE /api/mail/messages/m1': () => emptyResponse(204),
  };
}

function stored(): MailLayoutPrefs {
  return JSON.parse(window.localStorage.getItem(MAIL_LAYOUT_STORAGE_KEY) ?? 'null') as MailLayoutPrefs;
}

function workspace() {
  return screen.getByTestId('mail-browser');
}

describe('layout preferences', () => {
  it('reads a valid preference as written', () => {
    const prefs: MailLayoutPrefs = {
      mode: 'bottom', folderWidth: 300, listWidth: 500, listRatio: 0.6,
      folders: { right: false, bottom: true, list: true },
    };
    expect(parseMailLayout(JSON.stringify(prefs))).toEqual(prefs);
  });

  it('falls back to the defaults for missing, corrupt or foreign values', () => {
    expect(parseMailLayout(null)).toEqual(DEFAULT_MAIL_LAYOUT);
    expect(parseMailLayout('{not json')).toEqual(DEFAULT_MAIL_LAYOUT);
    expect(parseMailLayout('[1,2]')).toEqual(DEFAULT_MAIL_LAYOUT);
    expect(parseMailLayout('"right"')).toEqual(DEFAULT_MAIL_LAYOUT);
    const odd = parseMailLayout(JSON.stringify({
      mode: 'sideways', folderWidth: 'wide', listWidth: Number.MAX_SAFE_INTEGER, listRatio: -3,
      folders: { right: 'yes', bottom: true },
    }));
    expect(odd.mode).toBe('right');
    expect(odd.folderWidth).toBe(DEFAULT_MAIL_LAYOUT.folderWidth);
    expect(odd.listWidth).toBe(760);
    expect(odd.listRatio).toBe(0.15);
    expect(odd.folders).toEqual({ right: true, bottom: true, list: false });
  });

  it('keeps the preferred layout where it fits and falls back where it does not', () => {
    const prefs = { ...DEFAULT_MAIL_LAYOUT, listWidth: 700 };
    const wide = resolveMailLayout(prefs, { width: 1400, height: 800 });
    expect(wide).toMatchObject({ mode: 'right', foldersDocked: true, listWidth: 700, table: false, compact: false });

    // The list shrinks before the reader drops under its minimum.
    const medium = resolveMailLayout(prefs, { width: 900, height: 700 });
    expect(medium.mode).toBe('right');
    expect(medium.listWidth).toBe(900 - 220 - 8 - 8 - 360);

    // Too narrow for folders beside two panes: the folders undock (overlay).
    const tablet = resolveMailLayout(prefs, { width: 700, height: 700 });
    expect(tablet).toMatchObject({ mode: 'right', foldersDocked: false, foldersCanDock: false });
    expect(tablet.listWidth).toBe(700 - 8 - 360);

    // A phone: one pane at a time, whatever was preferred.
    const phone = resolveMailLayout({ ...prefs, mode: 'bottom' }, { width: 390, height: 700 });
    expect(phone).toMatchObject({ mode: 'list', compact: true, foldersDocked: false });
    expect(phone.available).toEqual({ right: false, bottom: false, list: true });

    // "Below" needs height; without it "right" takes over.
    const short = resolveMailLayout({ ...prefs, mode: 'bottom' }, { width: 1200, height: 300 });
    expect(short.mode).toBe('right');

    // The preference itself is never rewritten by a fallback.
    expect(prefs.listWidth).toBe(700);
  });

  it('clamps sizes to their limits and lays a wide list out as a table', () => {
    const bottom = resolveMailLayout({ ...DEFAULT_MAIL_LAYOUT, mode: 'bottom', listRatio: 0.85 }, { width: 1200, height: 660, bodyHeight: 600 });
    expect(bottom.mode).toBe('bottom');
    expect(bottom.listHeight).toBe(600 - 8 - 180);
    expect(bottom.table).toBe(true);
    const tight = resolveMailLayout({ ...DEFAULT_MAIL_LAYOUT, folderWidth: 420 }, { width: 1000, height: 600 });
    expect(tight.folderWidth).toBe(1000 - (LIST_MIN + 8 + 360) - 8);
    expect(tight.folderWidth).toBeGreaterThanOrEqual(FOLDERS_MIN);
  });
});

describe('mail workspace', () => {
  it('starts in "right": folders, list and an inviting placeholder side by side', async () => {
    installFetchMock(handlers());
    renderMail('/mail?archive=arc-1');
    await screen.findAllByTestId('mail-message-row');
    expect(workspace()).toHaveAttribute('data-layout', 'right');
    expect(screen.getByTestId('mail-folders-pane')).toBeVisible();
    expect(screen.getByTestId('mail-reader-placeholder')).toHaveTextContent('Seleziona un messaggio');
    expect(within(screen.getByTestId('mail-layout-switch')).getByRole('radio', { name: 'Anteprima a destra' })).toBeChecked();
    expect(screen.getByTestId('mail-list-count')).toHaveTextContent('3 messaggi caricati');
  });

  it('switches layout without losing the open message, and remembers it after a reload', async () => {
    installFetchMock(handlers());
    renderMail('/mail/messages/m1?archive=arc-1');
    await screen.findByTestId('mail-reader');

    await userEvent.click(screen.getByRole('radio', { name: 'Anteprima sotto' }));
    expect(workspace()).toHaveAttribute('data-layout', 'bottom');
    expect(currentLocation()).toBe('/mail/messages/m1?archive=arc-1');
    expect(screen.getByTestId('mail-reader')).toBeInTheDocument();
    expect(screen.getByTestId('mail-message-list').closest('[data-presentation]')).toHaveAttribute('data-presentation', 'table');
    expect(stored().mode).toBe('bottom');

    cleanup();
    renderMail('/mail?archive=arc-1');
    await screen.findAllByTestId('mail-message-row');
    expect(workspace()).toHaveAttribute('data-layout', 'bottom');
    expect(screen.getByRole('radio', { name: 'Anteprima sotto' })).toBeChecked();
  });

  it('ignores a corrupt stored layout', async () => {
    window.localStorage.setItem(MAIL_LAYOUT_STORAGE_KEY, '{"mode":');
    installFetchMock(handlers());
    renderMail('/mail?archive=arc-1');
    await screen.findAllByTestId('mail-message-row');
    expect(workspace()).toHaveAttribute('data-layout', 'right');
  });

  it('still works when storage refuses every write', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    try {
      installFetchMock(handlers());
      renderMail('/mail?archive=arc-1');
      await screen.findAllByTestId('mail-message-row');
      await userEvent.click(screen.getByRole('radio', { name: 'Solo elenco' }));
      expect(workspace()).toHaveAttribute('data-layout', 'list');
    } finally {
      setItem.mockRestore();
    }
  });

  it('"list only": the message opens on its own and closing returns to the row, list and filters intact', async () => {
    window.localStorage.setItem(MAIL_LAYOUT_STORAGE_KEY, JSON.stringify({ ...DEFAULT_MAIL_LAYOUT, mode: 'list' }));
    installFetchMock(handlers());
    renderMail('/mail?archive=arc-1&q=oggetto');
    await screen.findAllByTestId('mail-message-row');
    expect(screen.getByTestId('mail-reader-pane')).not.toBeVisible();
    // Folders are tucked away in this layout until asked for.
    expect(screen.getByTestId('mail-folders-pane')).not.toBeVisible();

    await userEvent.click(screen.getByRole('link', { name: /Oggetto m2/ }));
    const reader = await screen.findByTestId('mail-reader');
    expect(screen.getByTestId('mail-list-pane')).not.toBeVisible();
    await waitFor(() => expect(within(reader).getByRole('heading', { level: 2 })).toHaveFocus());

    await userEvent.click(within(reader).getByRole('button', { name: 'Torna all’elenco' }));
    expect(currentLocation()).toBe('/mail?archive=arc-1&q=oggetto');
    expect(screen.getByTestId('mail-list-pane')).toBeVisible();
    await waitFor(() => expect(screen.getByRole('link', { name: /Oggetto m2/ })).toHaveFocus());
    expect(screen.getByLabelText('Cerca nella posta')).toHaveValue('oggetto');
  });

  it('hides and shows the folders, per layout', async () => {
    installFetchMock(handlers());
    renderMail('/mail?archive=arc-1');
    await screen.findAllByTestId('mail-message-row');
    const toggle = screen.getByTestId('mail-folders-toggle');
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getByTestId('mail-folders-pane')).not.toBeVisible();
    expect(screen.queryByTestId('mail-splitter-folders')).not.toBeInTheDocument();
    expect(stored().folders.right).toBe(false);
    expect(stored().folders.bottom).toBe(false);
  });

  it('resizes the list from the keyboard within its limits, restores the default and persists', async () => {
    installFetchMock(handlers());
    renderMail('/mail?archive=arc-1');
    await screen.findAllByTestId('mail-message-row');
    const splitter = screen.getByRole('separator', { name: 'Dimensione dell’elenco dei messaggi' });
    expect(splitter).toHaveAttribute('aria-orientation', 'vertical');
    expect(splitter).toHaveAttribute('aria-valuenow', '380');
    expect(splitter).toHaveAttribute('aria-controls', screen.getByTestId('mail-list-pane').id);

    splitter.focus();
    await userEvent.keyboard('{ArrowRight}');
    expect(splitter).toHaveAttribute('aria-valuenow', '396');
    expect(screen.getByTestId('mail-list-pane').style.width).toBe('396px');
    await userEvent.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    expect(splitter).toHaveAttribute('aria-valuenow', '332');
    await userEvent.keyboard('{Home}');
    expect(splitter).toHaveAttribute('aria-valuenow', String(LIST_MIN));
    await userEvent.keyboard('{ArrowLeft}');
    expect(splitter).toHaveAttribute('aria-valuenow', String(LIST_MIN));
    await userEvent.keyboard('{End}');
    expect(Number(splitter.getAttribute('aria-valuenow'))).toBe(Number(splitter.getAttribute('aria-valuemax')));
    fireEvent.doubleClick(splitter);
    expect(splitter).toHaveAttribute('aria-valuenow', '380');
    await userEvent.keyboard('{ArrowRight}');
    expect(stored().listWidth).toBe(396);
  });

  it('moves between messages with the arrow keys, inside the list', async () => {
    installFetchMock(handlers());
    renderMail('/mail?archive=arc-1');
    const rows = await screen.findAllByTestId('mail-message-row');
    rows[0].focus();
    await userEvent.keyboard('{ArrowDown}');
    expect(rows[1]).toHaveFocus();
    await userEvent.keyboard('{End}');
    expect(rows[2]).toHaveFocus();
    await userEvent.keyboard('{ArrowDown}');
    expect(rows[2]).toHaveFocus();
    await userEvent.keyboard('{Home}');
    expect(rows[0]).toHaveFocus();
    // The rows stay real links: Enter opens one.
    await userEvent.keyboard('{Enter}');
    expect(currentLocation()).toBe('/mail/messages/m1?archive=arc-1');
  });

  it('selecting another message keeps the list where it was and starts the reader at the top', async () => {
    installFetchMock(handlers(40));
    renderMail('/mail/messages/m1?archive=arc-1');
    await screen.findByTestId('mail-reader');
    const listPane = screen.getByTestId('mail-list-pane');
    const readerPane = screen.getByTestId('mail-reader-pane');
    listPane.scrollTop = 600;
    fireEvent.scroll(listPane);
    readerPane.scrollTop = 900;

    await userEvent.click(screen.getByRole('link', { name: /Oggetto m2 / }));
    await waitFor(() => expect(screen.getByTestId('mail-reader')).toHaveTextContent('Secondo'));
    expect(listPane.scrollTop).toBe(600);
    expect(readerPane.scrollTop).toBe(0);
    expect(screen.getByRole('link', { name: /Oggetto m2 / })).toHaveAttribute('aria-current', 'true');
  });

  it('shows how many advanced filters are in force on the Filtri button', async () => {
    installFetchMock(handlers());
    renderMail('/mail?archive=arc-1&from=ada&attachments=1');
    const toggle = await screen.findByTestId('mail-filters-toggle');
    expect(toggle).toHaveAccessibleName('Filtri, 2 filtri attivi');
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    cleanup();
    renderMail('/mail?archive=arc-1&q=solo-testo');
    expect(await screen.findByTestId('mail-filters-toggle')).toHaveAccessibleName('Filtri');
  });

  it('after a deletion the list drops the row and the placeholder returns', async () => {
    installFetchMock(handlers());
    renderMail('/mail/messages/m1?archive=arc-1');
    const reader = await screen.findByTestId('mail-reader');
    await userEvent.click(within(reader).getByRole('button', { name: 'Elimina messaggio' }));
    await userEvent.click(within(await screen.findByTestId('mail-delete-dialog')).getByTestId('mail-delete-confirm'));
    await waitFor(() => expect(screen.getByTestId('mail-reader-placeholder')).toBeInTheDocument());
    expect(screen.getAllByTestId('mail-message-row').map((r) => r.getAttribute('data-message-id'))).toEqual(['m2', 'm3']);
    expect(screen.getByTestId('mail-list-count')).toHaveTextContent('2 messaggi caricati');
  });

  it('missing subject, sender and date read as such', async () => {
    installFetchMock({
      ...handlers(),
      'GET /api/mail/messages': () => jsonResponse({
        items: [summary('m1', { subject: '  ', from: null, date: null, hasAttachments: true })],
        nextCursor: null,
      }),
    });
    renderMail('/mail?archive=arc-1');
    const [row] = await screen.findAllByTestId('mail-message-row');
    expect(row).toHaveTextContent('Mittente sconosciuto');
    expect(row).toHaveTextContent('(senza oggetto)');
    expect(row).toHaveTextContent('Senza data');
    expect(row).toHaveTextContent('Con allegati');
  });
});

describe('the application scroll viewport', () => {
  function Shell({ path }: { path: string }) {
    const ref = useRef<HTMLElement>(null);
    return (
      <AppScrollProvider viewportRef={ref}>
        <main ref={ref} data-testid="app-main">
          <MemoryRouter initialEntries={[path]}>
            <Routes>
              <Route path="/mail" element={<MailPage />} />
            </Routes>
          </MemoryRouter>
        </main>
      </AppScrollProvider>
    );
  }

  it('is laid out to its height only while the mail workspace is on screen', async () => {
    installFetchMock(handlers());
    const view = render(<AuthedWrapper><Shell path="/mail?archive=arc-1" /></AuthedWrapper>);
    await screen.findAllByTestId('mail-message-row');
    expect(screen.getByTestId('app-main')).toHaveClass('app-main--mail');
    view.unmount();

    installFetchMock(handlers());
    render(<AuthedWrapper><Shell path="/mail?view=documents" /></AuthedWrapper>);
    await screen.findAllByTestId('mail-doc-card');
    expect(screen.getByTestId('app-main')).not.toHaveClass('app-main--mail');
  });
});

describe('layout stability', () => {
  it('judges "below" on the workspace, so the toolbar hiding cannot flip the layout', () => {
    const prefs = { ...DEFAULT_MAIL_LAYOUT, mode: 'bottom' as const };
    // Around the threshold, with the toolbar shown (body 72px shorter) and
    // hidden (body = whole workspace), the layout chosen is the same.
    for (const height of [370, 383, 384, 385, 400]) {
      const withToolbar = resolveMailLayout(prefs, { width: 1200, height, bodyHeight: height - 72 });
      const withoutToolbar = resolveMailLayout(prefs, { width: 1200, height, bodyHeight: height });
      expect(withoutToolbar.mode).toBe(withToolbar.mode);
    }
    expect(resolveMailLayout(prefs, { width: 1200, height: 383, bodyHeight: 383 }).mode).toBe('right');
    expect(resolveMailLayout(prefs, { width: 1200, height: 384, bodyHeight: 328 }).mode).toBe('bottom');
    // The panes' own height still sizes the list.
    expect(resolveMailLayout(prefs, { width: 1200, height: 700, bodyHeight: 500 }).listHeightMax).toBe(500 - 8 - 180);
  });
});

describe('folder overlay from the keyboard', () => {
  // A workspace too narrow to dock the folders beside two panes.
  function narrowWorkspace() {
    const width = vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(650);
    const height = vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(600);
    return () => { width.mockRestore(); height.mockRestore(); };
  }

  it('opens as a modal: focus inside, background inert, Escape closes it and returns focus', async () => {
    const restore = narrowWorkspace();
    try {
      installFetchMock(handlers());
      renderMail('/mail?archive=arc-1');
      await screen.findAllByTestId('mail-message-row');
      const toggle = screen.getByTestId('mail-folders-toggle');
      expect(toggle).toHaveAttribute('aria-expanded', 'false');

      toggle.focus();
      await userEvent.keyboard('{Enter}');
      const pane = screen.getByTestId('mail-folders-pane');
      expect(pane).toBeVisible();
      expect(pane).toHaveAttribute('role', 'dialog');
      expect(pane).toHaveAttribute('aria-modal', 'true');
      await waitFor(() => expect(screen.getByTestId('mail-archive-select')).toHaveFocus());
      expect(screen.getByTestId('mail-toolbar')).toHaveAttribute('inert');
      expect(screen.getByTestId('mail-panes')).toHaveAttribute('inert');
      // Tab and Shift+Tab stay inside the panel.
      await userEvent.keyboard('{Shift>}{Tab}{/Shift}');
      expect(pane).toContainElement(document.activeElement as HTMLElement);
      for (let i = 0; i < 8; i += 1) {
        await userEvent.tab();
        expect(pane).toContainElement(document.activeElement as HTMLElement);
      }
      screen.getByTestId('mail-archive-select').focus();

      await userEvent.keyboard('{Escape}');
      expect(screen.getByTestId('mail-folders-pane')).not.toBeVisible();
      expect(screen.getByTestId('mail-toolbar')).not.toHaveAttribute('inert');
      await waitFor(() => expect(toggle).toHaveFocus());

      // Escape works wherever focus is, not only inside the panel.
      await userEvent.keyboard('{Enter}');
      await waitFor(() => expect(screen.getByTestId('mail-archive-select')).toHaveFocus());
      (document.activeElement as HTMLElement).blur();
      await userEvent.keyboard('{Escape}');
      expect(screen.getByTestId('mail-folders-pane')).not.toBeVisible();
      await waitFor(() => expect(toggle).toHaveFocus());

      // Picking a folder closes it too.
      await userEvent.keyboard('{Enter}');
      await userEvent.click(within(await screen.findByRole('navigation', { name: 'Cartelle' })).getByRole('button', { name: /^Posta inviata/ }));
      await waitFor(() => expect(screen.getByTestId('mail-folders-pane')).not.toBeVisible());
      expect(currentLocation()).toBe('/mail?archive=arc-1&folder=f-sent');
    } finally {
      restore();
    }
  });
});
