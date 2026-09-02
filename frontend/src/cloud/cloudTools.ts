import { PERMISSIONS, type PermissionKey } from '@nubarca/api-client';
import type { MessageKey } from '../i18n';
import type { IconName } from '../components/icons/Icon';

// The Cloud Functions tool model — pure, so the tool list, the canonical URLs
// and the invalid-value fallback are all testable without rendering.
//
// Private Vault is deliberately NOT
// one of them: it is a primary-navigation destination (/private), not an
// operational tool, and listing it in both places was the duplication this
// slice removes.
//
// A tool may name a `requiredPermission`. Reaching the hub is one authority
// (cloud-functions.access); what a given tool DOES can be another, and a tool
// whose endpoint would answer 403 must not be offered. The filtering here is
// UX only — the server remains the authority on every call the tool makes.

export type CloudToolId = 'upload' | 'organize' | 'dedupe' | 'archive' | 'tv-devices' | 'print-stations' | 'face-cluster';

export interface CloudTool {
  id: CloudToolId;
  titleKey: MessageKey;
  descriptionKey: MessageKey;
  icon: IconName;
  // Absent = available to anyone who can reach the hub at all.
  requiredPermission?: PermissionKey;
}

export const CLOUD_TOOLS: readonly CloudTool[] = [
  { id: 'upload', titleKey: 'cloud.bulkUpload', descriptionKey: 'cloud.bulkUploadDesc', icon: 'upload' },
  { id: 'organize', titleKey: 'cloud.organize', descriptionKey: 'cloud.organizeDesc', icon: 'calendar' },
  { id: 'dedupe', titleKey: 'cloud.dedupe', descriptionKey: 'cloud.dedupeDesc', icon: 'trash' },
  { id: 'archive', titleKey: 'cloud.downloadArchive', descriptionKey: 'cloud.downloadArchiveDesc', icon: 'archive' },
  { id: 'tv-devices', titleKey: 'cloud.tvDevices', descriptionKey: 'cloud.tvDevicesDesc', icon: 'tv' },
  { id: 'print-stations', titleKey: 'cloud.printStations', descriptionKey: 'cloud.printStationsDesc', icon: 'print' },
  {
    id: 'face-cluster',
    titleKey: 'cloud.faceCluster',
    descriptionKey: 'cloud.faceClusterDesc',
    icon: 'people',
    requiredPermission: PERMISSIONS.peopleClusterRebuild,
  },
];

export const DEFAULT_CLOUD_TOOL: CloudToolId = 'upload';

// The tools this user may actually use, in declaration order.
export function visibleCloudTools(has: (permission: PermissionKey) => boolean): readonly CloudTool[] {
  return CLOUD_TOOLS.filter((tool) => tool.requiredPermission === undefined || has(tool.requiredPermission));
}

// Which tool to show, given the URL and what this user may use.
//
// A deep link to a tool the user cannot use falls back rather than rendering —
// so `?tool=face-cluster` without the permission shows the default tool, and
// never the protected panel for a frame while a check catches up. The fallback
// is the default tool when that is permitted, else the first permitted one, and
// null when the hub has nothing to offer at all.
export function resolveCloudTool(
  params: URLSearchParams,
  visible: readonly CloudTool[],
): CloudToolId | null {
  const requested = toCloudToolId(params.get(CLOUD_TOOL_PARAM));
  if (requested !== null && visible.some((tool) => tool.id === requested)) {
    return requested;
  }
  if (visible.some((tool) => tool.id === DEFAULT_CLOUD_TOOL)) {
    return DEFAULT_CLOUD_TOOL;
  }
  return visible[0]?.id ?? null;
}

// The query-string parameter that makes a tool deep-linkable.
export const CLOUD_TOOL_PARAM = 'tool';

export function toCloudToolId(raw: string | null | undefined): CloudToolId | null {
  return CLOUD_TOOLS.some((tool) => tool.id === raw) ? (raw as CloudToolId) : null;
}

// Read the selected tool from the URL. Anything unrecognised — a typo, a
// removed tool, a hand-edited link — falls back safely to the default rather
// than rendering an empty hub.
export function cloudToolFromParams(params: URLSearchParams): CloudToolId {
  return toCloudToolId(params.get(CLOUD_TOOL_PARAM)) ?? DEFAULT_CLOUD_TOOL;
}

// The canonical URL for a tool. Legacy routes redirect here.
export function cloudToolUrl(id: CloudToolId): string {
  return `/cloud-functions?${CLOUD_TOOL_PARAM}=${id}`;
}

export function findCloudTool(id: CloudToolId): CloudTool {
  // `id` is already narrowed to a known tool, so this cannot miss.
  return CLOUD_TOOLS.find((tool) => tool.id === id)!;
}
