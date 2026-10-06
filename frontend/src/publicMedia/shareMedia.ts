// "CONDIVIDI": the phone's own share sheet — Android's, iOS's — with the
// PHOTOGRAPH in it, so a guest sends it to WhatsApp or any other app the way
// they would share from their gallery.
//
// What is shared is the FILE the download hands over (Web Share, level 2), never
// the page's address: the address is the party's or the album's key, and
// forwarding it would let in whoever receives it. So the button exists only
// where a download does, and only where the browser can share files at all —
// a computer usually cannot, and is offered nothing.
//
// A share must be started by the tap itself, and fetching the file takes time.
// A browser that judges the tap too old by then refuses (NotAllowedError): the
// file is kept, and the next tap shares it at once.

const EXTENSIONS: Record<string, string> = {
  'image/jpeg': 'jpg',
  'image/png': 'png',
  'image/webp': 'webp',
  'image/heic': 'heic',
  'image/heif': 'heif',
  'image/gif': 'gif',
  'video/mp4': 'mp4',
  'video/quicktime': 'mov',
};

type ShareNavigator = Navigator & {
  canShare?: (data: { files?: File[] }) => boolean;
  share?: (data: { files?: File[] }) => Promise<void>;
};

/** Whether this browser can hand files to the system share sheet. */
export function canShareFiles(): boolean {
  const nav = (typeof navigator === 'undefined' ? undefined : navigator) as ShareNavigator | undefined;
  if (!nav?.canShare || !nav.share) return false;
  try {
    return nav.canShare({ files: [new File([''], 'probe.jpg', { type: 'image/jpeg' })] });
  } catch {
    return false;
  }
}

/** The file a download address hands over, named for the product — never for the original file. */
export async function fetchShareFile(url: string, id: string, signal?: AbortSignal): Promise<File> {
  const response = await fetch(url, { credentials: 'same-origin', signal });
  if (!response.ok) throw new Error(`share fetch ${response.status}`);
  const blob = await response.blob();
  const type = blob.type.split(';')[0].trim();
  const extension = EXTENSIONS[type];
  return new File([blob], `NubArca-${id.slice(0, 8)}${extension ? `.${extension}` : ''}`, { type });
}

export type ShareOutcome = 'shared' | 'cancelled' | 'blocked' | 'failed';

/** Opens the share sheet. "blocked": the tap was too old by the time the file arrived. */
export async function shareFile(file: File): Promise<ShareOutcome> {
  const nav = navigator as ShareNavigator;
  try {
    await nav.share!({ files: [file] });
    return 'shared';
  } catch (error) {
    const name = error instanceof DOMException ? error.name : '';
    if (name === 'AbortError') return 'cancelled';
    if (name === 'NotAllowedError') return 'blocked';
    return 'failed';
  }
}
