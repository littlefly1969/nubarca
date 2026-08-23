import { PERMISSIONS, type PermissionKey } from '@nubarca/api-client';
import type { IconName } from '../../components/icons/Icon';
import type { MessageKey } from '../../i18n';
import type { MediaSelectionCapabilities } from './mediaSelectionCapabilities';

// WHAT the contextual selection dock offers, as pure data.
//
// The dock used to be a flat row of buttons, each decided by its own JSX
// conditional, and the two Laboratory destinations were built without asking
// whether the user may reach the Laboratory at all. One model instead: every
// entry declares the capability it needs and the permissions it needs, and this
// generator is the only place that combines them.
//
// The division of labour:
//   * mediaSelectionCapabilities.ts answers "does this action make sense for
//     THIS selection, in this scope, on this surface" — all photos? Excluded?
//     inside an album? It is not extended or duplicated here, only consumed.
//   * this file answers "and may this user do it" — the permission half.
//
// Hiding an entry is UX. The backend authorizes every one of these operations
// independently, so a wrong answer here shows or hides a control; it never
// grants anything. But a door that answers 403 is a bad door, which is why the
// frontend must not open it: see §11 of the slice spec.

export type MediaSelectionActionId =
  | 'restore'
  | 'remove-from-album'
  | 'personal'
  | 'excluded'
  | 'trash'
  | 'album'
  | 'plates'
  | 'beauty-lab';

export interface MediaSelectionAction {
  id: MediaSelectionActionId;
  labelKey: MessageKey;
  icon: IconName;
  // Only Trash. It earns a restrained destructive tint on its menu row — never
  // a large red pill, because it is one entry inside a menu like the others.
  destructive?: boolean;
}

export interface MediaSelectionActionModel {
  /** High-priority actions shown directly on the dock, outside both menus. */
  contextual: MediaSelectionAction[];
  /** Destinations that CHANGE where/what the media is. */
  moveTo: MediaSelectionAction[];
  /** Destinations that ADD an association and leave the media where it is. */
  addTo: MediaSelectionAction[];
}

export interface MediaSelectionActionInput {
  capabilities: MediaSelectionCapabilities;
  /** The caller's effective permissions, straight from `/api/auth/me`. */
  permissions: readonly string[];
}

interface Candidate extends MediaSelectionAction {
  capability(c: MediaSelectionCapabilities): boolean;
  // EVERY listed permission is required, matching the server's composite
  // policies — a Laboratory section needs the Laboratory shell as well.
  permissions?: readonly PermissionKey[];
}

// "Move to Personal" is the PRIVATE VAULT operation, not a second name for the
// ordinary library, so it is gated on private-vault.access exactly as the
// Private destination in the navigation is.
const MOVE_TO: readonly Candidate[] = [
  {
    id: 'personal',
    labelKey: 'gallery.ws.destPersonal',
    icon: 'private',
    capability: (c) => c.canMoveToPersonal,
    permissions: [PERMISSIONS.privateVaultAccess],
  },
  {
    id: 'excluded',
    labelKey: 'gallery.ws.destExcluded',
    icon: 'archive',
    capability: (c) => c.canMoveToExcluded,
  },
  {
    id: 'trash',
    labelKey: 'gallery.ws.destTrash',
    icon: 'trash',
    destructive: true,
    capability: (c) => c.canTrash,
  },
];

// The photo-only Laboratory destinations carry the SAME composite the
// Laboratory itself requires: the shell permission plus the section's own. A
// user holding Plates but not Aesthetics gets exactly one of them here, just as
// they get exactly one tab there.
const ADD_TO: readonly Candidate[] = [
  {
    id: 'album',
    labelKey: 'gallery.ws.destAlbum',
    icon: 'album-add',
    capability: (c) => c.canAddToAlbum,
  },
  {
    id: 'plates',
    labelKey: 'gallery.ws.destPlates',
    icon: 'plates',
    capability: (c) => c.canUsePhotoOnlyDestinations,
    permissions: [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates],
  },
  {
    id: 'beauty-lab',
    labelKey: 'gallery.ws.destAesthetics',
    icon: 'aesthetics',
    capability: (c) => c.canUsePhotoOnlyDestinations,
    permissions: [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryAesthetics],
  },
];

// Neither of these is a "destination" alongside the others.
//
// Restore is the inverse of Excluded, not a peer of it — offering it inside
// "Move to" would read as a fourth place to put the media. Remove-from-album
// takes away a MEMBERSHIP and never touches the file, so filing it beside Trash
// would be a lie about what it does. Both stay on the dock itself.
const CONTEXTUAL: readonly Candidate[] = [
  {
    id: 'restore',
    labelKey: 'moveToExcluded.restore',
    icon: 'restore',
    capability: (c) => c.canRestore,
  },
  {
    id: 'remove-from-album',
    labelKey: 'mediaWs.removeFromAlbum',
    icon: 'album-remove',
    capability: (c) => c.canRemoveFromCurrentAlbum,
  },
];

function resolve(
  candidates: readonly Candidate[],
  { capabilities, permissions }: MediaSelectionActionInput,
): MediaSelectionAction[] {
  return candidates
    .filter((candidate) => {
      if (!candidate.capability(capabilities)) return false;
      return (candidate.permissions ?? []).every((p) => permissions.includes(p));
    })
    .map(({ id, labelKey, icon, destructive }) => (
      destructive ? { id, labelKey, icon, destructive } : { id, labelKey, icon }
    ));
}

export function buildMediaSelectionActions(
  input: MediaSelectionActionInput,
): MediaSelectionActionModel {
  return {
    contextual: resolve(CONTEXTUAL, input),
    moveTo: resolve(MOVE_TO, input),
    addTo: resolve(ADD_TO, input),
  };
}

/** True when the model offers nothing at all — the dock still shows the count. */
export function isEmptyActionModel(model: MediaSelectionActionModel): boolean {
  return model.contextual.length === 0 && model.moveTo.length === 0 && model.addTo.length === 0;
}
