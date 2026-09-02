import { describe, expect, it } from 'vitest';
import { PERMISSIONS, type PermissionKey } from '@nubarca/api-client';
import {
  CLOUD_TOOLS,
  DEFAULT_CLOUD_TOOL,
  cloudToolFromParams,
  cloudToolUrl,
  findCloudTool,
  resolveCloudTool,
  toCloudToolId,
  visibleCloudTools,
} from './cloudTools';

// A permission oracle: `held` is what this notional user carries.
const holding = (...held: PermissionKey[]) => (p: PermissionKey) => held.includes(p);

describe('Cloud Functions tool model', () => {
  it('offers the normal-user tools in order', () => {
    expect(CLOUD_TOOLS.map((tool) => tool.id)).toEqual([
      'upload', 'organize', 'dedupe', 'archive', 'tv-devices', 'print-stations', 'face-cluster',
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
    expect(toCloudToolId('dedupe')).toBe('dedupe');
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
    expect(cloudToolUrl('dedupe')).toBe('/cloud-functions?tool=dedupe');
    expect(cloudToolUrl('archive')).toBe('/cloud-functions?tool=archive');
    expect(cloudToolUrl('tv-devices')).toBe('/cloud-functions?tool=tv-devices');
    expect(cloudToolUrl('print-stations')).toBe('/cloud-functions?tool=print-stations');
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

// A tool may need an authority beyond "may reach the hub". Getting this wrong is
// not a cosmetic bug: the tablist's roving-tabindex arithmetic is computed from
// the list, so an index that belongs to a hidden tool focuses nothing.
describe('Cloud Functions tool visibility', () => {
  it('hides a tool whose permission the user does not hold', () => {
    const withoutIt = visibleCloudTools(holding()).map((tool) => tool.id);
    expect(withoutIt).not.toContain('face-cluster');
    // …and hides nothing else.
    expect(withoutIt).toEqual(['upload', 'organize', 'dedupe', 'archive', 'tv-devices', 'print-stations']);

    const withIt = visibleCloudTools(holding(PERMISSIONS.peopleClusterRebuild)).map((t) => t.id);
    expect(withIt).toContain('face-cluster');
  });

  it('states which permission each gated tool needs', () => {
    expect(findCloudTool('face-cluster').requiredPermission).toBe(PERMISSIONS.peopleClusterRebuild);
    // The ordinary tools are open to anyone who can reach the hub.
    for (const id of ['upload', 'organize', 'dedupe', 'archive', 'tv-devices', 'print-stations'] as const) {
      expect(findCloudTool(id).requiredPermission).toBeUndefined();
    }
  });

  it('falls back rather than opening a tool the user may not use', () => {
    const params = new URLSearchParams('tool=face-cluster');

    // Deep link honoured for somebody who holds it…
    expect(resolveCloudTool(params, visibleCloudTools(holding(PERMISSIONS.peopleClusterRebuild))))
      .toBe('face-cluster');
    // …and falls back to the default for somebody who does not, rather than
    // rendering the protected panel while a check catches up.
    expect(resolveCloudTool(params, visibleCloudTools(holding()))).toBe(DEFAULT_CLOUD_TOOL);
  });

  it('falls back to the first permitted tool when the default itself is gone', () => {
    const onlyFaceCluster = CLOUD_TOOLS.filter((tool) => tool.id === 'face-cluster');
    expect(resolveCloudTool(new URLSearchParams(), onlyFaceCluster)).toBe('face-cluster');
    // Nothing to offer at all is null, not a tool nobody may open.
    expect(resolveCloudTool(new URLSearchParams('tool=upload'), [])).toBeNull();
  });
});
