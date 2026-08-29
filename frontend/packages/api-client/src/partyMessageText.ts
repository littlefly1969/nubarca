// The browser half of the party-message text contract. Its C# mirror is
// `src/NubArca.Api/Domain/PartyMessageText.cs`, which is the AUTHORITY: this
// file exists so the guest's live character counter says the same number the
// server is about to measure. If the two drift, a guest watches the counter say
// "2 left" and the submit fail, with nothing on screen to explain it.
//
// LENGTH IS COUNTED IN UNICODE CODE POINTS — `[...text].length` here, and
// `EnumerateRunes().Count()` there. Not UTF-16 units (`text.length`), which
// would charge two for every emoji; and not grapheme clusters, which are the
// friendliest count but come from an ICU table that a browser and .NET upgrade
// on their own schedules. Code points are the same number on every runtime.

export const PARTY_MESSAGE_LIMITS = {
  displayName: 40,
  text: 120,
} as const;

// Matches PartyMessageText.Normalize step for step:
//   1. drop format characters (Cf) — bidi overrides, zero-width padding, soft
//      hyphens — but KEEP the two joiners, which hold emoji sequences and much
//      Indic/Persian text together;
//   2. turn every control character and every whitespace character (all the
//      line endings included) into a plain space;
//   3. collapse runs of spaces, then trim.
export function normalizePartyMessageText(value: string | null | undefined): string {
  if (!value) return '';
  return value
    .replace(/\p{Cf}/gu, (c) => (c === '‌' || c === '‍' ? c : ''))
    .replace(/[\p{Cc}\p{White_Space}]/gu, ' ')
    .replace(/ {2,}/g, ' ')
    .trim();
}

// Length in Unicode code points. The spread is deliberate: `value.length` would
// count UTF-16 units and disagree with the server on the first emoji typed.
export function partyMessageLength(value: string | null | undefined): number {
  return value ? [...value].length : 0;
}

// What the counter under the textarea should show, measured the way the server
// will measure it — so a guest padding with spaces sees the number stop moving
// rather than watching it climb into a rejection.
export function partyMessageRemaining(value: string | null | undefined): number {
  return PARTY_MESSAGE_LIMITS.text - partyMessageLength(normalizePartyMessageText(value));
}

export function partyDisplayNameRemaining(value: string | null | undefined): number {
  return PARTY_MESSAGE_LIMITS.displayName - partyMessageLength(normalizePartyMessageText(value));
}

// The client-side answer to "may this be submitted". The server re-decides the
// same question from the same rules; this only spares the guest a round-trip
// and lets the button disable itself.
export function isPartyMessageSubmittable(
  text: string | null | undefined,
  displayName: string | null | undefined,
): boolean {
  const body = normalizePartyMessageText(text);
  if (body.length === 0) return false;
  if (partyMessageLength(body) > PARTY_MESSAGE_LIMITS.text) return false;
  return partyMessageLength(normalizePartyMessageText(displayName)) <= PARTY_MESSAGE_LIMITS.displayName;
}
