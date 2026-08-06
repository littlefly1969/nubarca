import { api } from './client';

// Mirrors NubArca.Api.Storage.UserStorageUsage. Owner-scoped: the backend
// returns ONLY the authenticated user's figures. `quotaBytes` /
// `remainingBytes` are null when no quota is configured (unlimited). No ids,
// names, paths, or storage internals.
export interface UserStorageUsage {
  usedBytes: number;
  fileCount: number;
  quotaBytes: number | null;
  remainingBytes: number | null;
}

export function getMyStorageUsage(signal?: AbortSignal): Promise<UserStorageUsage> {
  return api<UserStorageUsage>('/api/storage/me', { signal });
}
