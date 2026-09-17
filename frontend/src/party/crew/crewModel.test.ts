import { describe, expect, it } from 'vitest';
import { WORKSPACE_SECTIONS } from '../workspace/partyWorkspaceModel';
import { CREW_CAPABILITIES, crewLandingSection, crewRoleLabelKey, crewSections } from './crewModel';

// What a role is SHOWN, decided as a pure function so two surfaces cannot
// disagree and none of it needs a browser to test.
//
// The presets below are the server's, written out here on purpose: if the
// server's change, these tests should fail rather than quietly follow.

const CO_ORGANIZER = [
  CREW_CAPABILITIES.detailsManage,
  CREW_CAPABILITIES.lifecycleManage,
  CREW_CAPABILITIES.experienceManage,
  CREW_CAPABILITIES.guestsRead,
  CREW_CAPABILITIES.invitationsManage,
  CREW_CAPABILITIES.attendanceManage,
  CREW_CAPABILITIES.contributionsConfigure,
  CREW_CAPABILITIES.contributionsModerate,
  CREW_CAPABILITIES.activitiesManage,
  CREW_CAPABILITIES.activitiesControl,
  CREW_CAPABILITIES.screensManage,
  CREW_CAPABILITIES.printManage,
];

const DIRECTOR = [
  CREW_CAPABILITIES.lifecycleManage,
  CREW_CAPABILITIES.contributionsConfigure,
  CREW_CAPABILITIES.contributionsModerate,
  CREW_CAPABILITIES.activitiesManage,
  CREW_CAPABILITIES.activitiesControl,
  CREW_CAPABILITIES.screensManage,
  CREW_CAPABILITIES.printManage,
];

const sections = (caps: string[], status = 'draft') =>
  crewSections(caps, status, WORKSPACE_SECTIONS);

describe('what a Party Crew role is shown', () => {
  it('gives a co-organizer the whole party except the host’s own settings', () => {
    expect(sections(CO_ORGANIZER)).toEqual([
      'summary', 'experience', 'guests', 'photos', 'activities', 'screens',
    ]);
    // IMPOSTAZIONI is the party's own data, duplicating it, tearing it down and
    // who else is helping. None of that is delegable, at any role.
    expect(sections(CO_ORGANIZER)).not.toContain('settings');
  });

  it('never shows a director the guest list, because they never fetch a name', () => {
    const shown = sections(DIRECTOR);
    expect(shown).toEqual(['summary', 'photos', 'activities', 'screens']);
    expect(shown).not.toContain('guests');
    // Not Esperienza either: writing what the guests read is the host's or a
    // co-organizer's, not the person running the evening's screens.
    expect(shown).not.toContain('experience');
  });

  it('opens Live only while the party is, for whoever can see it at all', () => {
    expect(sections(DIRECTOR)).not.toContain('live');
    expect(sections(DIRECTOR, 'live')).toContain('live');
    expect(sections(CO_ORGANIZER, 'live')).toContain('live');
    expect(sections(CO_ORGANIZER, 'ended')).not.toContain('live');
  });

  it('lands on the console while the party is happening, and the summary otherwise', () => {
    expect(crewLandingSection(DIRECTOR, 'draft', WORKSPACE_SECTIONS)).toBe('summary');
    expect(crewLandingSection(DIRECTOR, 'live', WORKSPACE_SECTIONS)).toBe('live');
    expect(crewLandingSection(CO_ORGANIZER, 'ended', WORKSPACE_SECTIONS)).toBe('summary');
  });

  it('keeps the product’s fixed order rather than the order capabilities arrived in', () => {
    const shuffled = [...CO_ORGANIZER].reverse();
    expect(sections(shuffled)).toEqual(sections(CO_ORGANIZER));
  });

  it('shows somebody with nothing granted the party and no way to touch it', () => {
    // A role whose every capability was taken away by the OWNER's own
    // permissions shrinking. They still see the party; they can act on nothing.
    expect(sections([])).toEqual(['summary']);
    expect(sections([], 'live')).toEqual(['summary', 'live']);
  });

  it('names every role in the product’s words, and an unknown one safely', () => {
    expect(crewRoleLabelKey('co_organizer')).toBe('party.crew.role.coOrganizer');
    expect(crewRoleLabelKey('director')).toBe('party.crew.role.director');
    expect(crewRoleLabelKey('dj')).toBe('party.crew.role.dj');
    expect(crewRoleLabelKey('reception')).toBe('party.crew.role.reception');
    expect(crewRoleLabelKey('honoree')).toBe('party.crew.role.honoree');
    // A role a later release adds must not render a raw key at a party.
    expect(crewRoleLabelKey('something-new')).toBe('party.crew.role.other');
  });
});
