import type { MessageKey } from '../../i18n';
import type { WorkspaceSection } from '../workspace/partyWorkspaceModel';

// WHAT A PARTY CREW DEVICE SEES, as pure functions.
//
// The server decides what a request may DO; this decides what a person is
// SHOWN, from the capabilities the server sent back. The two must agree, and
// they do because there is one table below rather than a condition scattered
// through seven components — but the UI is never the authority: a section
// rendered by mistake still gets a 404 from every route behind it.
//
// A DIRECTOR NEVER FETCHES A NAME. `guests.read` is not in their preset, so
// Ospiti is not in their sections, so the guest directory is never called. That
// is the privacy property, and it is one line here.

/** The capability vocabulary, exactly as the server spells it. */
export const CREW_CAPABILITIES = {
  detailsManage: 'party-crew.details.manage',
  lifecycleManage: 'party-crew.lifecycle.manage',
  experienceManage: 'party-crew.experience.manage',
  guestsRead: 'party-crew.guests.read',
  invitationsManage: 'party-crew.invitations.manage',
  attendanceManage: 'party-crew.attendance.manage',
  contributionsConfigure: 'party-crew.contributions.configure',
  contributionsModerate: 'party-crew.contributions.moderate',
  activitiesManage: 'party-crew.activities.manage',
  activitiesControl: 'party-crew.activities.control',
  screensManage: 'party-crew.screens.manage',
  printManage: 'party-crew.print.manage',
} as const;

/**
 * What each workspace section needs to be worth opening.
 *
 * Riepilogo and Live are always there: they are the party, and a crew member
 * who can do anything at all can look at it. Everything else needs at least
 * one of the capabilities that make its panels do something.
 */
const SECTION_NEEDS: Record<WorkspaceSection, readonly string[]> = {
  summary: [],
  live: [],
  experience: [CREW_CAPABILITIES.detailsManage, CREW_CAPABILITIES.experienceManage],
  guests: [CREW_CAPABILITIES.guestsRead, CREW_CAPABILITIES.invitationsManage,
    CREW_CAPABILITIES.attendanceManage],
  photos: [CREW_CAPABILITIES.contributionsModerate, CREW_CAPABILITIES.contributionsConfigure],
  activities: [CREW_CAPABILITIES.activitiesManage, CREW_CAPABILITIES.activitiesControl],
  screens: [CREW_CAPABILITIES.screensManage, CREW_CAPABILITIES.printManage],
  // IMPOSTAZIONI is the host's: the party's own data, duplicating it, tearing
  // it down, and who else is helping. A collaborator edits the party's details
  // from Esperienza and never sees this section at all.
  settings: ['never'],
};

export function crewCan(capabilities: readonly string[], capability: string): boolean {
  return capabilities.includes(capability);
}

/** The sections this crew member is shown, in the product's fixed order. */
export function crewSections(
  capabilities: readonly string[],
  status: string,
  all: readonly WorkspaceSection[],
): WorkspaceSection[] {
  return all.filter((section) => {
    const needs = SECTION_NEEDS[section];
    if (needs.length === 0) return true;
    return needs.some((capability) => capabilities.includes(capability));
  }).filter((section) => section !== 'live' || status === 'live');
}

/** Where a crew member lands: the console while it is happening, the summary otherwise. */
export function crewLandingSection(
  capabilities: readonly string[],
  status: string,
  all: readonly WorkspaceSection[],
): WorkspaceSection {
  const sections = crewSections(capabilities, status, all);
  if (status === 'live' && sections.includes('live')) return 'live';
  return sections[0] ?? 'summary';
}

/** The role, in the product's words. The server sends a key; a person reads a name. */
export function crewRoleLabelKey(roleKey: string): MessageKey {
  switch (roleKey) {
    case 'co_organizer': return 'party.crew.role.coOrganizer';
    case 'director': return 'party.crew.role.director';
    case 'dj': return 'party.crew.role.dj';
    case 'reception': return 'party.crew.role.reception';
    case 'honoree': return 'party.crew.role.honoree';
    default: return 'party.crew.role.other';
  }
}
