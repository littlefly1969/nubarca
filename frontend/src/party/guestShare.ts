// Handing the personal link over — the two browser affordances a share needs,
// and the awkward truths about both.
//
// OPENING WHATSAPP. A browser only opens a window for something the person did,
// and the share has to be recorded on the server first, so the request sits
// between the tap and the window. Chrome and Firefox still count the tap for a
// few seconds; Safari often does not. So the console tries, learns whether it
// worked, and always offers a real link as the way through — a link the host
// taps is also what makes iOS hand the URL to the WhatsApp app rather than to a
// web page.
//
// COPYING. Safari allows a clipboard write only inside the tap itself, but it
// accepts a ClipboardItem whose CONTENT is a promise — so the write is started
// during the tap and resolved when the server answers. Where that is missing we
// write afterwards, and where even that fails the console shows the link to
// copy by hand.

export interface ExternalWindow {
  opener: unknown;
}

/**
 * Opens a URL in a new tab or hands it to the app that claims it. False when
 * the browser refused it — then the console shows the link instead.
 */
export function openExternal(url: string): boolean {
  if (typeof window === 'undefined' || typeof window.open !== 'function') return false;
  let opened: (Window & ExternalWindow) | null = null;
  try {
    opened = window.open(url, '_blank') as (Window & ExternalWindow) | null;
  } catch {
    return false;
  }
  if (!opened) return false;
  // The opened page must not be able to navigate this one.
  try {
    opened.opener = null;
  } catch {
    /* already cross-origin: nothing to drop */
  }
  return true;
}

interface ClipboardItemLike {
  new(items: Record<string, Promise<Blob> | Blob>): unknown;
}

/**
 * Copies what the promise resolves to, beginning the write inside the current
 * tap where the browser supports it. Never throws and never leaves the
 * promise's rejection unhandled: false simply means "tell the host to copy it".
 */
export function copyWhenReady(text: Promise<string>): Promise<boolean> {
  const clipboard = typeof navigator === 'undefined' ? undefined : navigator.clipboard;
  if (!clipboard) return text.then(() => false, () => false);

  const item = (globalThis as unknown as { ClipboardItem?: ClipboardItemLike }).ClipboardItem;
  if (item && typeof clipboard.write === 'function') {
    const blob = text.then((value) => new Blob([value], { type: 'text/plain' }));
    try {
      return clipboard
        .write([new item({ 'text/plain': blob }) as ClipboardItem])
        .then(() => true, () => false);
    } catch {
      /* fall through to the plain write */
    }
  }
  return text.then(
    (value) => (typeof clipboard.writeText === 'function'
      ? clipboard.writeText(value).then(() => true, () => false)
      : Promise.resolve(false)),
    () => false);
}
