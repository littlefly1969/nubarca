import { tvGet } from './client';
import { personalHeaders } from './personal';

export interface TvPersonalVideoItem {
  id: string;
  name: string;
  width: number | null;
  height: number | null;
  createdAt: string;
  durationSeconds: number | null;
  videoCodec: string | null;
  audioCodec: string | null;
  hasAudio: boolean;
  posterUrl: string;
  videoUrl: string;
  previewStripUrl: string;
  occurrenceCount: number;
}

export interface TvPersonalVideoPage {
  items: TvPersonalVideoItem[];
  nextCursor: string | null;
  hasMore: boolean;
  totalCount: number;
}

export function listPersonalVideos(
  limit: number,
  cursor: string | null,
): Promise<TvPersonalVideoPage> {
  const params = new URLSearchParams({ limit: String(limit) });
  if (cursor) params.set('cursor', cursor);
  return tvGet<TvPersonalVideoPage>(
    `/api/tv/personal/videos?${params.toString()}`,
    personalHeaders(),
  );
}
