import { useEffect, useRef, useState } from 'react';
import { useI18n } from '../i18n';
import { useUploads } from './UploadProvider';
import { dragHasFiles, filesFromDataTransfer } from './collectFiles';
import type { UploadEntry } from './engine/uploadManager';
import type { UploadTarget } from './engine/targets';

// WHILE A FOLDER OR AN ALBUM IS ON SCREEN, the whole page takes files: drag
// them anywhere over it, or paste them (a screenshot, files copied in the
// desktop file manager). No need to open the upload panel first.

function isEditable(element: Element | null): boolean {
  if (!(element instanceof HTMLElement)) return false;
  if (element.isContentEditable) return true;
  return element.tagName === 'INPUT' || element.tagName === 'TEXTAREA' || element.tagName === 'SELECT';
}

export function UploadDropTarget({ target, enabled = true }: { target: UploadTarget; enabled?: boolean }) {
  const { t } = useI18n();
  const { enqueue } = useUploads();
  const [dragging, setDragging] = useState(false);
  const targetRef = useRef(target);
  targetRef.current = target;

  useEffect(() => {
    if (!enabled) {
      setDragging(false);
      return undefined;
    }
    // dragenter/dragleave fire for every child crossed; a depth counter tells
    // when the drag really left the window.
    let depth = 0;
    const onEnter = (event: DragEvent) => {
      if (!dragHasFiles(event.dataTransfer)) return;
      event.preventDefault();
      depth += 1;
      setDragging(true);
    };
    const onOver = (event: DragEvent) => {
      if (!dragHasFiles(event.dataTransfer)) return;
      // Without this the browser opens the file in place of the app.
      event.preventDefault();
      if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
    };
    const onLeave = (event: DragEvent) => {
      if (!dragHasFiles(event.dataTransfer)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0) setDragging(false);
    };
    const onDrop = (event: DragEvent) => {
      depth = 0;
      setDragging(false);
      if (!event.dataTransfer || !dragHasFiles(event.dataTransfer)) return;
      event.preventDefault();
      void filesFromDataTransfer(event.dataTransfer).then((entries) => enqueue(entries, targetRef.current));
    };
    const onPaste = (event: ClipboardEvent) => {
      if (isEditable(document.activeElement)) return;
      const files = Array.from(event.clipboardData?.files ?? []);
      if (files.length === 0) return;
      event.preventDefault();
      const entries: UploadEntry[] = files.map((file) => ({ file, relativePath: '' }));
      enqueue(entries, targetRef.current);
    };
    window.addEventListener('dragenter', onEnter);
    window.addEventListener('dragover', onOver);
    window.addEventListener('dragleave', onLeave);
    window.addEventListener('drop', onDrop);
    window.addEventListener('paste', onPaste);
    return () => {
      window.removeEventListener('dragenter', onEnter);
      window.removeEventListener('dragover', onOver);
      window.removeEventListener('dragleave', onLeave);
      window.removeEventListener('drop', onDrop);
      window.removeEventListener('paste', onPaste);
    };
  }, [enabled, enqueue]);

  if (!dragging) return null;
  return (
    <div className="upload-drop-overlay" data-testid="upload-drop-overlay">
      <p className="upload-drop-overlay__card">{t('upload.dropOverlay', { target: target.label })}</p>
    </div>
  );
}
