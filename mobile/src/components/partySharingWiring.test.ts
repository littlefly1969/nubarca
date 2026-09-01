// Party and owner-sharing screen WIRING (§25-§26, §30-§37).
//
// No component renderer here, so what is pinned is the wiring the rules depend
// on: that domain decisions come from the shared contract, and that the
// privacy properties are carried by the code rather than by good intentions.

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const here = dirname(fileURLToPath(import.meta.url));
const read = async (p: string) => code(await readFile(join(here, p), 'utf8'));

// ── Party ───────────────────────────────────────────────────────────────────

test('the ranges come from the contract; the screen defines none (§33, §34)', async () => {
  const sheet = await read('PartySettingsSheet.tsx');
  assert.match(sheet, /PARTY_SLIDESHOW_RANGES/);
  assert.match(sheet, /PARTY_GAME_RANGES/);
  // No literal bounds written here — that is what duplicating them looks like.
  assert.doesNotMatch(sheet, /min: \d+, max: \d+/);
});

test('an out-of-range value is REFUSED, never silently clamped', async () => {
  // Correcting a typed number hides the rule instead of teaching it, and the
  // server would reject it anyway. Clamping is for steppers.
  const sheet = await read('PartySettingsSheet.tsx');
  assert.match(sheet, /invalidSlideshowFields\(slideshow\)/);
  assert.match(sheet, /invalidGameFields\(game\)/);
  assert.match(sheet, /if \(slideshowInvalid\.length > 0\) return Alert\.alert/);
  assert.match(sheet, /if \(gameInvalid\.length > 0\) return Alert\.alert/);
  assert.doesNotMatch(sheet, /clampToRange/);
});

test('message actions come from the shared matrix, not from conditions (§36)', async () => {
  const sheet = await read('PartySettingsSheet.tsx');
  assert.match(sheet, /partyMessageActions\(message\)\.map\(/);
  for (const rule of [
    /message\.status === 'pending' &&/,
    /message\.status === 'visible' && !message\.isHero/,
  ]) {
    assert.doesNotMatch(sheet, rule, 'a transition rule is written in the screen');
  }
});

test('guest media and messages stay two domains (§35)', async () => {
  // Same album, two state machines. One merged queue would make every future
  // change to either a change to both.
  const sheet = await read('PartySettingsSheet.tsx');
  assert.match(sheet, /moderatePartyUpload\(/);
  assert.match(sheet, /moderatePartyMessage\(/);
  assert.ok(
    sheet.indexOf('moderatePartyUpload(') !== sheet.indexOf('moderatePartyMessage('),
    'the two moderation paths must be distinct',
  );
});

test('the guest link is the server URL plus this origin, never minted (§32)', async () => {
  const sheet = await read('PartySettingsSheet.tsx');
  assert.match(sheet, /partyGuestUrl\(getBaseUrl\(\), status\?\.partyUrl \?\? null\)/);
  assert.match(sheet, /Share\.share\(\{ message: guestUrl \}\)/);
  // No token handling and no URL BUILDING on the client: the risk is a client
  // minting its own link, not the word itself. (A bare /token/i would also
  // match the design-tokens import, which is why this targets code shapes.)
  assert.doesNotMatch(sheet, /partyToken|uploadToken/);
  assert.doesNotMatch(sheet, /`\$\{[^`]*\}\/party\//);
});

test('turning Party off is confirmed, because it kills the guest link', async () => {
  const sheet = await read('PartySettingsSheet.tsx');
  assert.match(sheet, /party\.modeOffTitle/);
  assert.match(sheet, /setPartyMode\(albumId, false\)/);
});

test('a guest message is rendered as TEXT', async () => {
  // A guest wrote it. It must never reach a markup or URI interpreter.
  const sheet = await read('PartySettingsSheet.tsx');
  assert.match(sheet, /<Text style=\{styles\.messageText\}>\{message\.text\}<\/Text>/);
});

// ── Owner sharing ───────────────────────────────────────────────────────────

test('inviting is TWO steps: resolve an exact address, then confirm (§26)', async () => {
  // There is no directory and no autocomplete; a prefix lookup would be an
  // account-enumeration oracle. Confirming the resolved NAME also means a
  // mistyped address cannot silently reach a stranger.
  const sheet = await read('AlbumSharingSheet.tsx');
  assert.match(sheet, /resolveAlbumRecipient\(albumId, address\)/);
  assert.match(sheet, /sharing\.resolved/);
  const inviteCall = sheet.indexOf('inviteAlbumMember(');
  const resolveCall = sheet.indexOf('resolveAlbumRecipient(');
  assert.ok(resolveCall !== -1 && inviteCall > resolveCall, 'invite must follow resolve');
});

test('a failed lookup says one thing, never why (§26)', async () => {
  // The server answers identically for "no such account" and anything else it
  // will not disclose; the client must not try to tell them apart.
  const sheet = await read('AlbumSharingSheet.tsx');
  assert.match(sheet, /Alert\.alert\(t\('sharing\.resolveFailed'\)\)/);
  assert.doesNotMatch(sheet, /err\.status === 404|status === 409/);
});

test('a member is addressed by membershipId and shown a MASKED address', async () => {
  const sheet = await read('AlbumSharingSheet.tsx');
  assert.match(sheet, /member\.membershipId/);
  assert.match(sheet, /member\.maskedEmail/);
  // Never a real address or a user id: the type has no such field, and the
  // screen must not invent a way to show one.
  assert.doesNotMatch(sheet, /member\.email\b|member\.userId\b/);
});

test('revoked and declined memberships are shown as history, without controls', async () => {
  const sheet = await read('AlbumSharingSheet.tsx');
  assert.match(sheet, /isHistoricalMembership\(m\.state\)/);
  assert.match(sheet, /history\.map\(\(m\) => memberRow\(m, false\)\)/);
  assert.match(sheet, /active\.map\(\(m\) => memberRow\(m, true\)\)/);
});

test('the message delegation appears only on an ACTIVE membership (§37)', async () => {
  // It is a narrow grant tied to an accepted, non-revoked membership — not a
  // role, and not something to offer on a pending invitation.
  const sheet = await read('AlbumSharingSheet.tsx');
  assert.match(
    sheet,
    /\{isActiveMembership\(member\.state\) && \([\s\S]{0,400}canManagePartyMessages/,
  );
});

test('revoking is confirmed and names who loses access', async () => {
  const sheet = await read('AlbumSharingSheet.tsx');
  assert.match(sheet, /sharing\.revokeBody/);
  assert.match(sheet, /revokeAlbumMember\(albumId, member\.membershipId\)/);
});

test('both screens are reachable from the album', async () => {
  const album = code(await readFile(join(here, '../../app/album/[id].tsx'), 'utf8'));
  assert.match(album, /<AlbumSharingSheet[\s\S]{0,200}albumId=\{albumId\}/);
  assert.match(album, /<PartySettingsSheet[\s\S]{0,200}albumId=\{albumId\}/);
});
