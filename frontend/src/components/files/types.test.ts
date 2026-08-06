import { describe, expect, it } from 'vitest';
import type { FileSummary, FolderSummary } from '@nubarca/api-client';
import {
  entryKey,
  fileKey,
  folderKey,
  isImage,
  mediaKindOf,
  smallThumbnailUrl,
  toEntries,
  typeLabel,
  videoPosterUrl,
} from './types';

function file(over: Partial<FileSummary> = {}): FileSummary {
  return {
    id: 'f1',
    name: 'x',
    mimeType: 'application/octet-stream',
    sizeBytes: 1,
    createdAt: '2026-01-01T00:00:00Z',
    ...over,
  };
}

describe('files/types helpers', () => {
  it('classifies media by MIME and by video name heuristic', () => {
    expect(mediaKindOf(file({ name: 'a.jpg', mimeType: 'image/jpeg' }))).toBe('image');
    expect(mediaKindOf(file({ name: 'a.mp4', mimeType: 'video/mp4' }))).toBe('video');
    // Unknown MIME but video extension still counts as video (server gates it).
    expect(mediaKindOf(file({ name: 'a.mov', mimeType: 'application/octet-stream' }))).toBe('video');
    expect(mediaKindOf(file({ name: 'a.txt', mimeType: 'text/plain' }))).toBeNull();
  });

  it('isImage only matches image/* MIME', () => {
    expect(isImage('image/png')).toBe(true);
    expect(isImage('video/mp4')).toBe(false);
  });

  it('builds derivative URLs that never point at the original', () => {
    expect(smallThumbnailUrl('abc')).toBe('/api/files/abc/thumbnail?size=small');
    expect(videoPosterUrl('abc')).toBe('/api/files/abc/poster');
    expect(smallThumbnailUrl('abc')).not.toContain('/content');
  });

  it('namespaces selection keys by kind', () => {
    expect(folderKey('1')).toBe('folder:1');
    expect(fileKey('1')).toBe('file:1');
    expect(entryKey({ kind: 'folder', id: '1', folder: {} as FolderSummary })).toBe('folder:1');
    expect(entryKey({ kind: 'file', id: '1', file: file() })).toBe('file:1');
  });

  it('orders entries folders-first', () => {
    const folders: FolderSummary[] = [{ id: 'd1', name: 'D', createdAt: '2026-01-01T00:00:00Z' }];
    const files: FileSummary[] = [file({ id: 'x1', name: 'X' })];
    const entries = toEntries(folders, files);
    expect(entries.map((e) => e.kind)).toEqual(['folder', 'file']);
  });

  it('derives a short human type label', () => {
    expect(typeLabel('application/pdf')).toBe('PDF');
    expect(typeLabel('image/jpeg')).toBe('Image');
    expect(typeLabel('video/mp4')).toBe('Video');
    expect(typeLabel('application/vnd.openxmlformats-officedocument.wordprocessingml.document'))
      .toMatch(/[A-Z]/);
  });
});
