import { describe, expect, it } from 'vitest';
import {
  CLOUD_TOOLS,
  DEFAULT_CLOUD_TOOL,
  cloudToolFromParams,
  cloudToolUrl,
  findCloudTool,
  toCloudToolId,
} from './cloudTools';

describe('Cloud Functions tool model', () => {
  it('offers exactly the four normal-user tools, in order', () => {
    expect(CLOUD_TOOLS.map((tool) => tool.id)).toEqual([
      'upload', 'organize', 'archive', 'tv-devices',
    ]);
  });

  it('does not include Private Vault as a tool', () => {
    const ids = CLOUD_TOOLS.map((tool) => String(tool.id));
    expect(ids).not.toContain('private');
    expect(ids).not.toContain('private-vault');
    expect(ids).not.toContain('vault');
  });

  it('narrows only known tool ids', () => {
    expect(toCloudToolId('upload')).toBe('upload');
    expect(toCloudToolId('tv-devices')).toBe('tv-devices');
    expect(toCloudToolId('private')).toBeNull();
    expect(toCloudToolId('Upload')).toBeNull();
    expect(toCloudToolId('')).toBeNull();
    expect(toCloudToolId(null)).toBeNull();
    expect(toCloudToolId(undefined)).toBeNull();
  });

  it('reads the selected tool from the URL', () => {
    expect(cloudToolFromParams(new URLSearchParams('tool=archive'))).toBe('archive');
    expect(cloudToolFromParams(new URLSearchParams('tool=tv-devices'))).toBe('tv-devices');
  });

  it('falls back safely to the default tool for a missing or invalid value', () => {
    expect(DEFAULT_CLOUD_TOOL).toBe('upload');
    expect(cloudToolFromParams(new URLSearchParams(''))).toBe('upload');
    expect(cloudToolFromParams(new URLSearchParams('tool='))).toBe('upload');
    expect(cloudToolFromParams(new URLSearchParams('tool=nope'))).toBe('upload');
    // A removed tool must not blank the hub either.
    expect(cloudToolFromParams(new URLSearchParams('tool=private'))).toBe('upload');
  });

  it('builds the canonical deep-link URL for every tool', () => {
    expect(cloudToolUrl('upload')).toBe('/cloud-functions?tool=upload');
    expect(cloudToolUrl('organize')).toBe('/cloud-functions?tool=organize');
    expect(cloudToolUrl('archive')).toBe('/cloud-functions?tool=archive');
    expect(cloudToolUrl('tv-devices')).toBe('/cloud-functions?tool=tv-devices');
  });

  it('round-trips every canonical URL back to its tool', () => {
    for (const tool of CLOUD_TOOLS) {
      const query = cloudToolUrl(tool.id).split('?')[1];
      expect(cloudToolFromParams(new URLSearchParams(query))).toBe(tool.id);
    }
  });

  it('gives every tool a title, a description and an icon', () => {
    for (const tool of CLOUD_TOOLS) {
      expect(findCloudTool(tool.id)).toBe(tool);
      expect(tool.titleKey).toBeTruthy();
      expect(tool.descriptionKey).toBeTruthy();
      expect(tool.icon).toBeTruthy();
    }
  });
});
