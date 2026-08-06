import type { MessageKey } from '../i18n';
import type { IconName } from '../components/icons/Icon';

// The Cloud Functions tool model — pure, so the tool list, the canonical URLs
// and the invalid-value fallback are all testable without rendering.
//
// Exactly four normal-user tools live here. Private Vault is deliberately NOT
// one of them: it is a primary-navigation destination (/private), not an
// operational tool, and listing it in both places was the duplication this
// slice removes.

export type CloudToolId = 'upload' | 'organize' | 'archive' | 'tv-devices';

export interface CloudTool {
  id: CloudToolId;
  titleKey: MessageKey;
  descriptionKey: MessageKey;
  icon: IconName;
}

export const CLOUD_TOOLS: readonly CloudTool[] = [
  { id: 'upload', titleKey: 'cloud.bulkUpload', descriptionKey: 'cloud.bulkUploadDesc', icon: 'upload' },
  { id: 'organize', titleKey: 'cloud.organize', descriptionKey: 'cloud.organizeDesc', icon: 'calendar' },
  { id: 'archive', titleKey: 'cloud.downloadArchive', descriptionKey: 'cloud.downloadArchiveDesc', icon: 'archive' },
  { id: 'tv-devices', titleKey: 'cloud.tvDevices', descriptionKey: 'cloud.tvDevicesDesc', icon: 'tv' },
];

export const DEFAULT_CLOUD_TOOL: CloudToolId = 'upload';

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
