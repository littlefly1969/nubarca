// The account's CURRENT SELECTION, from the Personal Area: the same
// server-held selection the owner's browsers and phones read. Only the
// selection is reachable from a television, never a named list, and every call
// rides the in-memory Personal grant like the rest of the Personal Area.
import { tvDelete, tvGet, tvPost } from './client';
import { personalHeaders } from './personal';

export interface TvSelectionSummary { id: string; version: number; count: number; stamp?: string | null }
export interface TvSelectionWrite { version: number; count: number; stamp?: string | null }

export function getPersonalSelection(signal?: AbortSignal): Promise<TvSelectionSummary> {
  return tvGet<TvSelectionSummary>('/api/tv/personal/selection', personalHeaders(), signal);
}

export function listPersonalSelectionIds(
  cursor: string | null, signal?: AbortSignal,
): Promise<{ ids: string[]; nextCursor: string | null }> {
  const params = new URLSearchParams({ limit: '1000' });
  if (cursor) params.set('cursor', cursor);
  return tvGet(`/api/tv/personal/selection/ids?${params.toString()}`, personalHeaders(), signal);
}

export function addPersonalSelectionItems(fileItemIds: string[]): Promise<TvSelectionWrite> {
  return tvPost<TvSelectionWrite>('/api/tv/personal/selection/items', { fileItemIds }, personalHeaders());
}

export function removePersonalSelectionItems(fileItemIds: string[]): Promise<TvSelectionWrite> {
  return tvDelete<TvSelectionWrite>('/api/tv/personal/selection/items', personalHeaders(), { fileItemIds });
}

export function clearPersonalSelection(): Promise<TvSelectionWrite> {
  return tvPost<TvSelectionWrite>('/api/tv/personal/selection/clear', undefined, personalHeaders());
}
