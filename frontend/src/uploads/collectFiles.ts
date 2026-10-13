import type { UploadEntry } from './engine/uploadManager';

// TURNING A DROP OR A PASTE INTO FILES, folders included.
//
// `dataTransfer.files` flattens a dropped folder into one useless entry, so a
// folder dragged from the desktop used to fail. The entry API walks it instead
// and keeps each file's path inside it ("Holiday/2026/IMG_1.jpg"), which the
// server turns into the same folders a folder pick would create.

interface FsEntry {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
}
interface FsFileEntry extends FsEntry {
  file(success: (file: File) => void, failure?: (error: unknown) => void): void;
}
interface FsDirectoryEntry extends FsEntry {
  createReader(): { readEntries(success: (entries: FsEntry[]) => void, failure?: (error: unknown) => void): void };
}

// The dotfiles a desktop drops along with a folder are never what was meant.
function isNoise(name: string): boolean {
  return name === '.DS_Store' || name === 'Thumbs.db' || name === 'desktop.ini' || name.startsWith('._');
}

function readFile(entry: FsFileEntry): Promise<File | null> {
  return new Promise((resolve) => entry.file(resolve, () => resolve(null)));
}

// readEntries returns at most ~100 entries per call; keep calling until empty.
async function readAll(entry: FsDirectoryEntry): Promise<FsEntry[]> {
  const reader = entry.createReader();
  const all: FsEntry[] = [];
  for (;;) {
    const batch = await new Promise<FsEntry[]>((resolve) => reader.readEntries(resolve, () => resolve([])));
    if (batch.length === 0) return all;
    all.push(...batch);
  }
}

async function walk(entry: FsEntry, prefix: string, out: UploadEntry[]): Promise<void> {
  if (isNoise(entry.name)) return;
  if (entry.isFile) {
    const file = await readFile(entry as FsFileEntry);
    if (file) out.push({ file, relativePath: prefix.length > 0 ? `${prefix}/${file.name}` : '' });
    return;
  }
  if (entry.isDirectory) {
    const path = prefix.length > 0 ? `${prefix}/${entry.name}` : entry.name;
    for (const child of await readAll(entry as FsDirectoryEntry)) {
      await walk(child, path, out);
    }
  }
}

/** True while a drag carries files (and not, say, a link or a text selection). */
export function dragHasFiles(dataTransfer: DataTransfer | null): boolean {
  return !!dataTransfer && Array.from(dataTransfer.types ?? []).includes('Files');
}

/** Every file a drop carries, with the folder path of each when folders were dropped. */
export async function filesFromDataTransfer(
  dataTransfer: DataTransfer,
  options: { folders: boolean } = { folders: true },
): Promise<UploadEntry[]> {
  const items = Array.from(dataTransfer.items ?? []);
  // The entries must be taken synchronously, before the first await: the
  // browser empties the DataTransfer once the drop event returns.
  const entries = items
    .filter((item) => item.kind === 'file')
    .map((item) => {
      const getter = (item as DataTransferItem & { webkitGetAsEntry?: () => FsEntry | null }).webkitGetAsEntry;
      return { entry: typeof getter === 'function' ? getter.call(item) : null, file: item.getAsFile() };
    });

  if (entries.length === 0 || entries.every((e) => e.entry === null)) {
    return Array.from(dataTransfer.files ?? []).map((file) => ({ file, relativePath: '' }));
  }

  const out: UploadEntry[] = [];
  for (const { entry, file } of entries) {
    if (entry?.isDirectory) {
      if (options.folders) await walk(entry, '', out);
    } else if (file && !isNoise(file.name)) {
      out.push({ file, relativePath: '' });
    }
  }
  return out;
}
