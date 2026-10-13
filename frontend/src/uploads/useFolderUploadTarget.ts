import { useMemo } from 'react';
import { useI18n } from '../i18n';
import type { FolderUploadTarget } from './engine/targets';

/** The upload destination for a folder, named the way the tray shows it (the root reads as "File"). */
export function useFolderUploadTarget(folderId: string | null, folderName: string): FolderUploadTarget {
  const { t } = useI18n();
  return useMemo(
    () => ({ kind: 'folder', folderId, label: folderId === null ? t('nav.files') : folderName }),
    [folderId, folderName, t],
  );
}
