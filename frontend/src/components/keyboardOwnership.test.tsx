import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AuthedWrapper, installFetchMock, jsonResponse, type MockHandler } from '../test-utils';
import { FaceContextViewer } from './people/FaceContextViewer';
import { ClusterAssignDialog } from './people/ClusterAssignDialog';
import { MediaViewer, OWNER_FILE_SOURCES, type MediaViewerItem } from './MediaViewer';
import { AlbumPickerModal } from '../gallery/AlbumPickerModal';
import { isEditableKeyboardTarget, isModalOwnedKey, ownsKeyboardEvent } from './keyboardOwnership';

// The regression this file exists for, exactly as the user hit it:
//
//   open a photo viewer → open a modal on top → put the caret in its search
//   field → press ArrowLeft/ArrowRight to fix a typo → the PHOTO UNDERNEATH
//   changed. And one Escape closed the modal AND the viewer behind it.
//
// Both viewers register their shortcuts on `window`, so a modal's keystrokes
// reached them: `stopPropagation` from a sibling listener on the same target
// does not stop them, and a bubble-phase listener cannot tell whose key it is.
//
// The contract now: THE TOPMOST MODAL OWNS THE KEYBOARD — enforced from both
// ends (the modal consumes those keys in the capture phase; the viewer ignores
// anything from a dialog that is not its own root, and never reads arrows from
// an editable target as navigation) and never with preventDefault, so the caret
// keeps behaving exactly as the browser intends.

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const box = (x: number) => ({ x, y: 0.2, width: 0.15, height: 0.15 });

function faceContext(faceId: string): MockHandler {
  return () =>
    jsonResponse({
      fileItemId: 'file-1',
      fileName: 'crowd.jpg',
      selectedFaceId: faceId,
      selectedBox: box(0.2),
      faces: [{ faceId: 'f-1', box: box(0.2) }, { faceId: 'f-2', box: box(0.6) }],
      personId: null,
      personName: null,
      isIgnored: false,
    });
}

describe('face viewer under an assign modal', () => {
  async function openAssignModal() {
    installFetchMock({
      'GET /api/people/faces/f-1/context': faceContext('f-1'),
      'GET /api/people/faces/f-2/context': faceContext('f-2'),
      'GET /api/people': () =>
        jsonResponse([{ personId: 'p-1', name: 'Maria', faceCount: 3, representative: null }]),
    });
    const onIndexChange = vi.fn();
    const onClose = vi.fn();
    render(
      <AuthedWrapper>
        <MemoryRouter>
          <FaceContextViewer
            faceIds={['f-1', 'f-2']}
            index={0}
            onIndexChange={onIndexChange}
            onClose={onClose}
          />
        </MemoryRouter>
      </AuthedWrapper>,
    );
    await screen.findByText('crowd.jpg');
    await userEvent.click(screen.getByRole('button', { name: 'Assegna persona' }));
    const search = await screen.findByLabelText('Cerca persona');
    return { onIndexChange, onClose, search: search as HTMLInputElement };
  }

  it('gives the arrow keys to the modal input, not to the photo behind it', async () => {
    const { onIndexChange, onClose, search } = await openAssignModal();

    await userEvent.type(search, 'Maria');
    expect(search.value).toBe('Maria');
    // Caret in the middle of the text, the way somebody fixing a typo puts it.
    search.setSelectionRange(3, 3);

    await userEvent.keyboard('{ArrowLeft}');
    await userEvent.keyboard('{ArrowLeft}');
    await userEvent.keyboard('{ArrowRight}');

    // The caret moved and the text is untouched: nothing was preventDefault-ed.
    expect(search.value).toBe('Maria');
    expect(search.selectionStart).toBe(2);
    // The viewer underneath did not navigate, and did not close.
    expect(onIndexChange).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    // The modal is still open — an arrow is not a dismissal either.
    expect(screen.getByRole('dialog', { name: 'Assegna a persona' })).toBeTruthy();
  });

  it('Escape closes only the modal, and the viewer navigates again once it is gone', async () => {
    const { onIndexChange, onClose, search } = await openAssignModal();
    await userEvent.type(search, 'Mar');

    await userEvent.keyboard('{Escape}');

    // Only the topmost surface closed.
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Assegna a persona' })).toBeNull());
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByText('crowd.jpg')).toBeTruthy();

    // With the modal gone the viewer owns the keyboard again.
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(onIndexChange).toHaveBeenCalledWith(1);

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });
});

describe('media viewer under the album picker', () => {
  const photo: MediaViewerItem = {
    id: 'f1', sources: OWNER_FILE_SOURCES, name: 'IMG_1248.JPG',
    displayName: 'IMG_1248.JPG', kind: 'image', sizeBytes: 1000,
  };

  function metadataDoc() {
    return {
      id: 'f1', name: 'IMG_1248.JPG', mimeType: 'image/jpeg', sizeBytes: 1000,
      createdAt: '2026-02-02T09:00:00Z', updatedAt: null,
      blob: { width: 4000, height: 3000, detectedContentType: 'image/jpeg', embedded: null, video: null },
      user: {
        title: null, description: null, tags: [], rating: null, favorite: false,
        dateTakenOverride: null, locationOverride: null,
      },
      effective: {
        displayName: 'IMG_1248.JPG', dateTaken: null, dateTakenSource: 'upload', location: null,
      },
    };
  }

  it('leaves the media index alone while the picker search field has the caret', async () => {
    installFetchMock({
      'GET /api/files/f1/metadata': () => jsonResponse(metadataDoc()),
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse([]),
    });
    const onIndexChange = vi.fn();
    const onClose = vi.fn();
    const onPickerClose = vi.fn();

    render(
      <AuthedWrapper>
        <MediaViewer
          items={[photo, { ...photo, id: 'f2', name: 'IMG_1249.JPG', displayName: 'IMG_1249.JPG' }]}
          index={0}
          onClose={onClose}
          onIndexChange={onIndexChange}
          // The picker is opened FROM the viewer's details drawer in the real
          // application; rendering it as drawer content is that same stacking.
          renderDetails={() => (
            <AlbumPickerModal fileItemIds={['f1']} onClose={onPickerClose} />
          )}
        />
      </AuthedWrapper>,
    );

    // The drawer is where the picker lives, exactly as in the application.
    await userEvent.click(await screen.findByTestId('viewer-details-toggle'));

    const search = (await screen.findByLabelText('Cerca album…')) as HTMLInputElement;
    await userEvent.type(search, 'Vacanze');
    search.setSelectionRange(4, 4);

    await userEvent.keyboard('{ArrowLeft}');
    await userEvent.keyboard('{ArrowRight}');
    await userEvent.keyboard('{ArrowRight}');

    expect(search.value).toBe('Vacanze');
    expect(search.selectionStart).toBe(5);
    expect(onIndexChange).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    // Escape dismisses the picker only — it must not drop the user out of the
    // photo, which is the whole reason this overlay owns the keyboard.
    await userEvent.keyboard('{Escape}');
    expect(onPickerClose).toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });
});

describe('cluster assign dialog', () => {
  it('does not let its own keystrokes reach a global shortcut underneath', async () => {
    installFetchMock({});
    const underlying = vi.fn();
    window.addEventListener('keydown', underlying);
    try {
      const onClose = vi.fn();
      render(
        <AuthedWrapper>
          <ClusterAssignDialog
            clusterId="c-1"
            faceCount={4}
            people={[{ personId: 'p-1', name: 'Maria', faceCount: 2, representative: null }]}
            onDone={vi.fn()}
            onClose={onClose}
          />
        </AuthedWrapper>,
      );

      const search = (await screen.findByLabelText('Cerca persona')) as HTMLInputElement;
      await userEvent.type(search, 'Mar');
      await userEvent.keyboard('{ArrowLeft}');
      await userEvent.keyboard('{ArrowRight}');

      // Typing still reached the field; the navigation keys did not reach a
      // bubble-phase window listener standing in for a viewer's shortcuts.
      const seen = () => underlying.mock.calls.map((args) => (args[0] as KeyboardEvent).key);
      expect(search.value).toBe('Mar');
      expect(seen()).not.toContain('ArrowLeft');
      expect(seen()).not.toContain('ArrowRight');

      await userEvent.keyboard('{Escape}');
      expect(onClose).toHaveBeenCalled();
      expect(seen()).not.toContain('Escape');
    } finally {
      window.removeEventListener('keydown', underlying);
    }
  });
});

describe('ownership helpers', () => {
  it('recognises the targets whose arrows are caret moves', () => {
    const host = document.createElement('div');
    host.innerHTML =
      '<input><textarea></textarea><select></select><div contenteditable="true"><b id="deep">x</b></div>' +
      '<div contenteditable="false"><span id="off">x</span></div><button id="btn">b</button>';
    document.body.append(host);
    try {
      expect(isEditableKeyboardTarget(host.querySelector('input'))).toBe(true);
      expect(isEditableKeyboardTarget(host.querySelector('textarea'))).toBe(true);
      expect(isEditableKeyboardTarget(host.querySelector('select'))).toBe(true);
      // A caret can sit in a DESCENDANT of the editable host.
      expect(isEditableKeyboardTarget(host.querySelector('#deep'))).toBe(true);
      expect(isEditableKeyboardTarget(host.querySelector('#off'))).toBe(false);
      expect(isEditableKeyboardTarget(host.querySelector('#btn'))).toBe(false);
      expect(isEditableKeyboardTarget(null)).toBe(false);
      expect(isEditableKeyboardTarget(window)).toBe(false);
    } finally {
      host.remove();
    }
  });

  it('gives a viewer its own keys and nothing from another dialog', () => {
    const host = document.createElement('div');
    host.innerHTML =
      '<div id="viewer" role="dialog" aria-modal="true"><button id="own">x</button></div>' +
      '<div id="modal" role="dialog" aria-modal="true"><input id="theirs"></div>' +
      '<span id="loose">x</span>';
    document.body.append(host);
    const viewer = host.querySelector('#viewer');
    try {
      // Its own subtree, and the viewer element itself.
      expect(ownsKeyboardEvent(viewer, host.querySelector('#own'))).toBe(true);
      expect(ownsKeyboardEvent(viewer, viewer)).toBe(true);
      // A DIFFERENT modal owns its keys — the check is against this root, not
      // merely "is there an aria-modal", which the viewer would match itself.
      expect(ownsKeyboardEvent(viewer, host.querySelector('#theirs'))).toBe(false);
      // Nothing modal in the way (focus on body, a stray element) → the viewer's.
      expect(ownsKeyboardEvent(viewer, host.querySelector('#loose'))).toBe(true);
      expect(ownsKeyboardEvent(viewer, window)).toBe(true);
      expect(ownsKeyboardEvent(null, host.querySelector('#theirs'))).toBe(true);
    } finally {
      host.remove();
    }
  });

  it('claims only the keys a surface underneath could act on', () => {
    for (const key of ['Escape', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End']) {
      expect(isModalOwnedKey(key)).toBe(true);
    }
    // Tab stays free for focus traps; typing is never touched.
    for (const key of ['Tab', 'a', 'Enter', ' ', 'Backspace']) {
      expect(isModalOwnedKey(key)).toBe(false);
    }
  });
});
