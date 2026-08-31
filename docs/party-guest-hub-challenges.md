# Party Guest Hub + “Il festeggiato deve…”

## Gate 0 — integration map

This slice extends the existing Party capability. It does not introduce a
parallel guest identity, media pipeline, TV player, or remote protocol.

| Area | Existing implementation | Extension point |
| --- | --- | --- |
| Album Party settings | `frontend/src/albums/AlbumSettingsPanel.tsx`, `frontend/packages/api-client/src/party.ts`, `src/NubArca.Api/Endpoints/PartyEndpoints.cs` | The current panel and `PartyAlbumLink` settings gain game controls. |
| Current Party links | `PartyLinkService.BuildPartyUrl` and `BuildUploadUrl`; owner `AlbumPartyStatusDto` | The view URL remains the canonical hub. The upload URL remains a legacy deep link. |
| QR rendering | TV `OverlayQrCorners.tsx`; owner settings expose both URLs | The TV overlay and owner settings show one canonical Party QR/link. |
| Guest functions | `PartyPage.tsx` (view/find-face), `PartyUploadPage.tsx` (media/message) | `PartyPage` becomes the Guest Hub and links to the unchanged contribution route plus voting. |
| Party TV runtime | `tv/src/screens/ViewerScreen.tsx`, `partySlideshow.ts`, `partyMessages.ts` | The existing media-boundary handler asks the server for a challenge and enters ChallengeHold. |
| Party synchronized state | Media/message/face state is polled via `TvEndpoints.cs`; no persisted playback state existed | A link-scoped `PartyChallengeSession` extends the same polled TV API. |
| Existing NEXT | Fire remote → `remoteEvent.ts` → `remoteMap.ts` → Viewer `next` | In ChallengeHold the same action completes atomically; otherwise behavior is unchanged. |
| EF/migrations | Party entities/configurations, `AppDbContext`, `Data/Migrations` | Add challenge, vote, session, completion entities and additive settings. |
| Media selection | Existing album membership/cover pipeline and authenticated thumbnails | Challenge media references an existing item validated in the same owner/album; no blob copy. |
| Tests | `tests/NubArca.Api.Tests/Party`, Party frontend tests, TV Viewer/Party/remote tests | Add policy, isolation/concurrency, hub/legacy, boundary/Hold/NEXT/reconnect coverage. |

## Compatibility matrix

| Current function | Current QR/URL | Guest Hub CTA | Legacy compatibility |
| --- | --- | --- | --- |
| Browse Party media and find your face | view QR → `/party/{viewToken}` | “Guarda le foto” / “Trova il tuo volto” | The URL remains canonical and becomes the hub; both capabilities remain in-page. |
| Contribute media or a greeting | upload QR → `/party/{uploadToken}/upload` | “Contribuisci alla festa” | The old upload token/URL still opens contribution directly. |
| Vote on challenges | none | “Vota le sfide” → `/party/{viewToken}/challenges` | Additive; no legacy entry is removed. |

## Constraint rationale

- Challenges are album-scoped and survive Party-link rotation. Optional media
  is a validated reference to an existing member of the same owner album.
- Votes are event-scoped. The unique index on `(PartyAlbumLinkId,
  PartyParticipantId, PartyChallengeId)` is the database invariant for one
  vote per guest/challenge. A conditional participant-counter update claims
  the total budget atomically before insert; unvote decrements in the same
  transaction.
- Runtime state is one row per active Party link. Active challenge, next
  deadline and completion rows make reconnect, idempotent NEXT and no-repeat
  persistent rather than client timing assumptions.

All changes are additive. Existing links default to `GameEnabled=false`, and
both public token contracts/routes remain available.

## Verification record

- Backend Party regression and integration suite: 215 tests passed, including
  legacy view/upload URLs, owner and token isolation, atomic vote budget,
  persisted Hold/reconnect, and idempotent NEXT.
- Focused challenge suite: 15 tests passed, including two independent-connection
  concurrency tests for the conditional vote claim and versioned completion.
- Frontend production build and typecheck passed; the complete frontend suite
  passed 1571/1571 after updating the intentional one-QR contracts.
- Native TV typecheck passed and its complete test command passed 39/39.
- `GameEnabled=false` is both the migration default and covered by an integration
  test that proves the legacy Party endpoints continue to work while the challenge
  endpoint stays unavailable.
- No production deployment is part of this slice.

## Visual review artifacts

These review fixtures are rendered locally with the feature's production CSS at
the specified viewport. They contain no production data.

- [Party Settings](screenshots/party-settings.png)
- [Guest Hub — 390 px](screenshots/party-guest-hub-390.png)
- [ChallengeHold — 16:9](screenshots/party-challenge-hold-16x9.png)

Physical Fire TV/remote acceptance remains a release gate: automated tests prove
the persisted state and command wiring, while only a real device can accept
overscan, long-distance readability and remote feel.
