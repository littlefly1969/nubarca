// Thin expo-media-library adapter.
//
// Least-privilege rules encoded here:
//   * permissions are READ without prompting anywhere in the sync path;
//   * prompting happens ONLY through requestPermissions(), called from the
//     explicit user enablement flow — never at app startup, never by the
//     engine itself;
//   * granular photo+video access only; location (ACCESS_MEDIA_LOCATION /
//     NSPhotoLibraryUsageDescription with location) is deliberately NOT
//     requested — sync transmits no GPS inventory of its own;
//   * limited/partial library access is a VALID state: whatever subset the
//     OS exposes is what gets synchronized.

import * as MediaLibrary from 'expo-media-library';
import type {
  AssetPage,
  MediaLibraryPort,
  PagedAsset,
  PermissionState,
} from './syncTypes.ts';

type PermissionResponse = Awaited<ReturnType<typeof MediaLibrary.getPermissionsAsync>>;

function mapPermissions(result: PermissionResponse): PermissionState {
  if (!result.granted) return result.accessPrivileges === 'limited' ? 'limited' : 'denied';
  return result.accessPrivileges === 'limited' ? 'limited' : 'granted';
}

function mapAsset(asset: MediaLibrary.Asset): PagedAsset {
  return {
    id: asset.id,
    // Sync handles photos and videos; audio and unknown kinds are invisible.
    mediaType: asset.mediaType === MediaLibrary.MediaType.video ? 'video' : 'photo',
    filename: asset.filename,
    modificationTime: asset.modificationTime,
  };
}

class ExpoMediaLibraryPort implements MediaLibraryPort {
  async getPermissions(): Promise<PermissionState> {
    // writeOnly=false: sync READS the library to upload; it never writes back.
    const result = await MediaLibrary.getPermissionsAsync(false, [
      'photo',
      'video',
    ]);
    return mapPermissions(result);
  }

  async requestPermissions(): Promise<PermissionState> {
    const result = await MediaLibrary.requestPermissionsAsync(false, [
      'photo',
      'video',
    ]);
    return mapPermissions(result);
  }

  async getPage(cursor: string | null, pageSize: number): Promise<AssetPage> {
    // One stable ordering (id), full photo+video scope, metadata only. The
    // page size bounds memory; pagination restarts are harmless because the
    // ledger dedups by asset id.
    const result = await MediaLibrary.getAssetsAsync({
      first: pageSize,
      after: cursor ?? undefined,
      sortBy: [MediaLibrary.SortBy.default],
      mediaType: [MediaLibrary.MediaType.photo, MediaLibrary.MediaType.video],
    });
    return {
      assets: result.assets.map(mapAsset),
      hasNextPage: result.hasNextPage,
      endCursor: result.endCursor,
    };
  }

  async getLocalInfo(assetId: string): Promise<{ uri: string } | null> {
    try {
      const info = await MediaLibrary.getAssetInfoAsync(assetId);
      // localUri falls back to uri; both are platform file URIs suitable for
      // native streaming uploads. Null/empty means the OS lost the asset.
      const uri = info?.localUri ?? info?.uri ?? null;
      return uri ? { uri } : null;
    } catch {
      return null;
    }
  }
}

export const mediaLibraryPort: MediaLibraryPort = new ExpoMediaLibraryPort();
