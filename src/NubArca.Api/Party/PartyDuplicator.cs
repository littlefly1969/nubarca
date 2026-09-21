using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Party;

/// <summary>
/// Copies a party's CONFIGURATION into a new, independent party.
///
/// <para>THE LINE THIS DRAWS is between what a host decided and what an evening
/// produced. Titles, windows, slots, the deck, the slideshow timings, the
/// quotas, the approval modes, the game switches and the print budgets are
/// decisions — a host who ran a good party wants to run it again without typing
/// all of it a second time. Participants, preferences, votes, rounds, uploads,
/// greetings, prints, face searches, televisions, display grants, heartbeats and
/// tokens are what HAPPENED, and none of it is replayed: last year's guests did
/// not attend this year's party, and last year's QR must never open it.</para>
///
/// <para>MEDIA IS SHARED, NOT COPIED. The clone gets its own album with its own
/// membership rows pointing at the SAME <c>FileItem</c>s, which point at the
/// same content-addressed blobs. Nothing is re-uploaded, no byte is duplicated
/// and no SHA changes — album membership is a row, and this writes new rows.
/// Removing a photograph from the clone leaves the original untouched for
/// exactly that reason.</para>
///
/// <para>THE CLONE IS PUBLISHED WHEN ITS CAPABILITY TRAVELS. The new link is
/// minted with new ids and therefore new tokens, which is what carries the
/// settings that live on it — and an active link on a draft party is a state no
/// other path produces: enabling the capability IS publishing the party
/// (<c>PartyLinkService.EnableAsync</c>), and the owner surface shows a link as
/// live because of it. A draft clone therefore handed its host a guest link
/// that answered "not found", which read as a link into the party just copied.
/// Publishing exposes nothing: nobody holds the new tokens until the host
/// shares them. A source with no link yields a draft with no link, exactly as a
/// party created from nothing does.</para>
/// </summary>
public interface IPartyDuplicator
{
    /// <summary>
    /// Duplicates one of the caller's own parties, or refuses.
    ///
    /// <para>Refuses <see cref="PartyMutationOutcome.NotFound"/> for a missing
    /// or foreign party, and <see cref="PartyMutationOutcome.InvalidRequest"/>
    /// for a party with no main album — there is nothing to copy the media
    /// membership of, and a clone whose album the host would have to choose
    /// afterwards is just a new party.</para>
    /// </summary>
    Task<PartyMutationResult> DuplicateAsync(
        Guid ownerUserId, Guid partyId, string? title, CancellationToken cancellationToken = default);
}

public sealed class PartyDuplicator : IPartyDuplicator
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyService _parties;
    private readonly IPartyLinkService _links;
    private readonly ILogger<PartyDuplicator> _logger;

    public PartyDuplicator(
        AppDbContext db, TimeProvider clock, IPartyService parties, IPartyLinkService links,
        ILogger<PartyDuplicator> logger)
    {
        _db = db;
        _clock = clock;
        _parties = parties;
        _links = links;
        _logger = logger;
    }

    public async Task<PartyMutationResult> DuplicateAsync(
        Guid ownerUserId, Guid partyId, string? title, CancellationToken cancellationToken = default)
    {
        var source = await _db.Parties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, cancellationToken);
        if (source is null) return PartyMutationResult.Refused(PartyMutationOutcome.NotFound);

        var normalized = PartyTextLimits.Normalize(title) ?? source.Title;
        if (!PartyTextLimits.IsValidTitle(normalized))
            return PartyMutationResult.Refused(PartyMutationOutcome.InvalidRequest);

        var sourceAlbumId = await _db.PartyMediaSources.AsNoTracking()
            .Where(s => s.PartyId == partyId && s.Role == PartyMediaSourceRoles.Main)
            .Select(s => (Guid?)s.AlbumId)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceAlbumId is not Guid albumId)
            return PartyMutationResult.Refused(PartyMutationOutcome.InvalidRequest);

        var sourceAlbum = await _db.Albums.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (sourceAlbum is null) return PartyMutationResult.Refused(PartyMutationOutcome.NotFound);

        var now = _clock.GetUtcNow().UtcDateTime;

        // ONE UNIT OF WORK. A clone is a graph — party, album, membership,
        // sources, slots, deck, capability, print profile — and a failure
        // partway through would leave an album nobody asked for, or a party with
        // a deck and no way to reach it.
        var owned = _db.Database.CurrentTransaction is null;
        var transaction = owned
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var party = new Domain.Party
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                Title = normalized!,
                Description = source.Description,
                // The scheduling the host configured travels; the two timestamps
                // that RECORD an evening do not, because this evening has not
                // happened.
                EventStartsAt = source.EventStartsAt,
                GuestAccessExpiresAt = source.GuestAccessExpiresAt,
                LibraryAccessExpiresAt = source.LibraryAccessExpiresAt,
                // Both covers are decisions about how the evening LOOKS, so they
                // travel — as references to the same files, never as copies.
                InvitationCoverFileItemId = source.InvitationCoverFileItemId,
                LiveCoverFileItemId = source.LiveCoverFileItemId,
                // Published below if, and only if, the capability travels.
                Status = PartyStatuses.Draft,
                LiveStartedAt = null,
                LiveEndedAt = null,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Parties.Add(party);

            // THE ALBUM NEEDS ITS OWN NAME. An album name is unique per owner in
            // the database, and the party's title is not — so a copy that took
            // the title verbatim would fail on the constraint the second time a
            // host duplicated anything. The party keeps the title it was given;
            // the album is numbered until it is free, which is what every other
            // "make me another one of these" affordance does.
            var album = new Album
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                Name = await FreeAlbumNameAsync(ownerUserId, sourceAlbum.Name, cancellationToken),
                Description = sourceAlbum.Description,
                // Show-on-TV is a PUBLICATION decision about one album, not a
                // party setting, and the two have been independent since Party
                // became its own root. A copy starts unpublished.
                ShowOnTv = false,
                CoverFileItemId = sourceAlbum.CoverFileItemId,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Albums.Add(album);

            // The membership, row for row. Same files, same order, same
            // provenance timestamps are deliberately NOT carried: these rows were
            // created now, by this owner, and saying otherwise would put a date
            // on the copy that no one performed.
            var items = await _db.AlbumItems.AsNoTracking()
                .Where(i => i.AlbumId == albumId)
                .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
                .Select(i => new { i.FileItemId, i.SortOrder })
                .ToListAsync(cancellationToken);
            foreach (var item in items)
            {
                _db.AlbumItems.Add(new AlbumItem
                {
                    Id = Guid.NewGuid(),
                    AlbumId = album.Id,
                    FileItemId = item.FileItemId,
                    SortOrder = item.SortOrder,
                    AddedAt = now,
                    AddedByUserId = ownerUserId,
                });
            }

            _db.PartyMediaSources.Add(new PartyMediaSource
            {
                PartyId = party.Id,
                AlbumId = album.Id,
                Role = PartyMediaSourceRoles.Main,
                SortOrder = 0,
                CreatedAt = now,
            });

            // What the party TELLS its guests. The photograph reference travels
            // with the slot because it is a reference to the owner's own file,
            // not album membership — the same file, reachable through the clone's
            // own slot route and judged by the same eligibility rule.
            var slots = await _db.PartyGuestContents.AsNoTracking()
                .Where(c => c.PartyId == partyId)
                .ToListAsync(cancellationToken);
            foreach (var slot in slots)
            {
                _db.PartyGuestContents.Add(new PartyGuestContent
                {
                    PartyId = party.Id,
                    Kind = slot.Kind,
                    Enabled = slot.Enabled,
                    VisibleBefore = slot.VisibleBefore,
                    VisibleLive = slot.VisibleLive,
                    VisibleAfter = slot.VisibleAfter,
                    ContentJson = slot.ContentJson,
                    MediaFileItemId = slot.MediaFileItemId,
                    MediaPresentation = slot.MediaPresentation,
                    TextAlign = slot.TextAlign,
                    MediaOrientation = slot.MediaOrientation,
                    MediaCropZoom = slot.MediaCropZoom,
                    MediaCropCenterX = slot.MediaCropCenterX,
                    MediaCropCenterY = slot.MediaCropCenterY,
                    TextPlacement = slot.TextPlacement,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            // The DECK, with new ids and every rule the host wrote — including
            // `Kind`, which the composer no longer asks for but which the row
            // still carries. Preferences and rounds are not copied and could not
            // be: they name the ids that were just replaced.
            var deck = await _db.PartyChallenges.AsNoTracking()
                .Where(c => c.AlbumId == albumId)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .ToListAsync(cancellationToken);
            // One query for every ballot in the deck, so a copy costs the same
            // whether the host wrote one choice round or twenty.
            var deckBallots = (await _db.PartyChallengeOptions.AsNoTracking()
                    .Where(o => deck.Select(c => c.Id).Contains(o.PartyChallengeId))
                    .OrderBy(o => o.Position).ToListAsync(cancellationToken))
                .GroupBy(o => o.PartyChallengeId)
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var challenge in deck)
            {
                var copyId = Guid.NewGuid();
                // The ANSWERS are part of the question, so a re-run that copied
                // a choice round without them would arrive with a ballot nobody
                // could vote on. New ids, because the votes that named the old
                // ones belong to the evening that was.
                foreach (var option in deckBallots.GetValueOrDefault(challenge.Id, []))
                {
                    _db.PartyChallengeOptions.Add(new PartyChallengeOption
                    {
                        Id = Guid.NewGuid(),
                        PartyChallengeId = copyId,
                        Position = option.Position,
                        Label = option.Label,
                        Outcome = option.Outcome,
                    });
                }
                _db.PartyChallenges.Add(new PartyChallenge
                {
                    Id = copyId,
                    AlbumId = album.Id,
                    Title = challenge.Title,
                    Body = challenge.Body,
                    Kind = challenge.Kind,
                    MediaFileItemId = challenge.MediaFileItemId,
                    IsEnabled = challenge.IsEnabled,
                    SortOrder = challenge.SortOrder,
                    DurationSeconds = challenge.DurationSeconds,
                    VotingMode = challenge.VotingMode,
                    VoteQuestion = challenge.VoteQuestion,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            // The questions the host ASKS — active ones only, with new ids and no
            // answers. They are a decision about the invitation, like its slots.
            // The guest list itself is what an evening produced and none of it
            // travels: no group, no name, address or phone, no +1, no RSVP, no
            // dietary note, no answer, no personal link and no email. Last
            // year's guests were not invited to this party — and nobody has
            // arrived at it, so no attendance travels either, neither a guest's
            // arrival nor any other person recorded at the door.
            var questions = await _db.PartyRsvpQuestions.AsNoTracking()
                .Where(q => q.PartyId == partyId && q.IsActive)
                .OrderBy(q => q.SortOrder).ThenBy(q => q.CreatedAt)
                .ToListAsync(cancellationToken);
            foreach (var question in questions)
            {
                _db.PartyRsvpQuestions.Add(new PartyRsvpQuestion
                {
                    Id = Guid.NewGuid(),
                    PartyId = party.Id,
                    Prompt = question.Prompt,
                    Kind = question.Kind,
                    Required = question.Required,
                    OptionsJson = question.OptionsJson,
                    IsActive = true,
                    SortOrder = question.SortOrder,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            // THE CAPABILITY, and every setting that lives on it: slideshow
            // timings, per-guest quotas, both approval modes, the game switches
            // and the pre-game preference budget. New id, therefore new tokens —
            // the view, upload and print tokens are all derived from it, so a
            // printed QR from the party being copied opens nothing here.
            //
            // The heartbeat is deliberately absent: no screen has looked at this
            // party, and claiming one had would make the control room lie on its
            // first render.
            var sourceLink = await _db.PartyAlbumLinks.AsNoTracking()
                .Where(l => l.PartyId == partyId)
                .OrderByDescending(l => l.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (sourceLink is not null)
            {
                var link = new PartyAlbumLink
                {
                    Id = Guid.NewGuid(),
                    PartyId = party.Id,
                    OwnerUserId = ownerUserId,
                    AlbumId = album.Id,
                    Enabled = true,
                    UploadEnabled = sourceLink.UploadEnabled,
                    RequireUploadApproval = sourceLink.RequireUploadApproval,
                    RequireMessageApproval = sourceLink.RequireMessageApproval,
                    // WHICH CONTRIBUTIONS THIS PARTY TAKES is part of how the
                    // evening is run, so it is copied like every other switch
                    // above. What is NOT copied is the guest book's CONTENTS:
                    // a dedication is written to one person at one party, and
                    // duplicating the party must never duplicate what somebody
                    // wrote at it.
                    SlideshowMessagesEnabled = sourceLink.SlideshowMessagesEnabled,
                    GuestbookEnabled = sourceLink.GuestbookEnabled,
                    RequireGuestbookApproval = sourceLink.RequireGuestbookApproval,
                    PhotoSlideSeconds = sourceLink.PhotoSlideSeconds,
                    MaxVideoSlideSeconds = sourceLink.MaxVideoSlideSeconds,
                    MaxPhotoUploadsPerParticipant = sourceLink.MaxPhotoUploadsPerParticipant,
                    MaxVideoUploadsPerParticipant = sourceLink.MaxVideoUploadsPerParticipant,
                    MaxMessagesPerParticipant = sourceLink.MaxMessagesPerParticipant,
                    MaxGuestbookEntriesPerParticipant = sourceLink.MaxGuestbookEntriesPerParticipant,
                    GameEnabled = sourceLink.GameEnabled,
                    PriorityVotingEnabled = sourceLink.PriorityVotingEnabled,
                    MinChallengeIntervalSeconds = sourceLink.MinChallengeIntervalSeconds,
                    MaxChallengeIntervalSeconds = sourceLink.MaxChallengeIntervalSeconds,
                    VotesPerGuest = sourceLink.VotesPerGuest,
                    MaxChallengesPerSession = sourceLink.MaxChallengesPerSession,
                    LastDisplaySeenAt = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                    RevokedAt = null,
                    ExpiresAt = null,
                    CreatedByUserId = ownerUserId,
                };
                var (viewHash, uploadHash) = _links.MintTokenHashes(link.Id);
                link.TokenHash = viewHash;
                link.UploadTokenHash = uploadHash;
                _db.PartyAlbumLinks.Add(link);

                // An active link IS a published party. Through the domain
                // transition, never by assigning the word, exactly as
                // EnableAsync does it.
                if (PartyLifecycle.Target(party.Status, PartyLifecycleAction.Publish) is string published)
                {
                    party.Status = published;
                }
            }

            // The print SETTINGS — which station, which printer, which budgets,
            // what the footer says. The counters and the public sequence start
            // again from zero, because paper spent at another party is not this
            // party's history; the jobs and the reservations that spent it stay
            // where they are.
            var profile = await _db.PartyPrintProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PartyAlbumId == albumId, cancellationToken);
            if (profile is not null)
            {
                _db.PartyPrintProfiles.Add(new PartyPrintProfile
                {
                    Id = Guid.NewGuid(),
                    PartyAlbumId = album.Id,
                    OwnerUserId = ownerUserId,
                    Enabled = profile.Enabled,
                    PrintStationId = profile.PrintStationId,
                    PrinterDeviceId = profile.PrinterDeviceId,
                    PhotoEnabled = profile.PhotoEnabled,
                    PhotoMaxPrints = profile.PhotoMaxPrints,
                    PhotoAcceptedCount = 0,
                    PhotoPrintsPerGuest = profile.PhotoPrintsPerGuest,
                    StripEnabled = profile.StripEnabled,
                    StripMaxPrints = profile.StripMaxPrints,
                    StripAcceptedCount = 0,
                    StripPrintsPerGuest = profile.StripPrintsPerGuest,
                    FooterText = profile.FooterText,
                    PublicSequenceNext = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (owned) await transaction!.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "party.duplicated SourcePartyId={SourcePartyId} PartyId={PartyId} AlbumId={AlbumId} Items={Items} Deck={Deck}",
                partyId, party.Id, album.Id, items.Count, deck.Count);

            _db.ChangeTracker.Clear();
            return PartyMutationResult.Ok(
                (await _parties.GetAsync(ownerUserId, party.Id, cancellationToken))!);
        }
        catch
        {
            if (owned) await transaction!.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// The source album's name with the smallest suffix that is free for this
    /// owner — "Festa di Anna (2)", then "(3)", and so on.
    ///
    /// <para>It searches rather than guessing, because a host duplicating the
    /// same party three times must get three albums and not two failures. The
    /// unique index remains the authority: this only avoids the collision it
    /// would otherwise report as a 500, and a concurrent duplicate that beat us
    /// to a name still loses there.</para>
    /// </summary>
    private async Task<string> FreeAlbumNameAsync(
        Guid ownerUserId, string baseName, CancellationToken cancellationToken)
    {
        var taken = await _db.Albums.AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId && a.Name.StartsWith(baseName))
            .Select(a => a.Name)
            .ToListAsync(cancellationToken);
        var used = taken.ToHashSet(StringComparer.Ordinal);
        // The bound is a courtesy, not a rule: nobody duplicates one party a
        // thousand times, and the index catches whatever this does not.
        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (!used.Contains(candidate)) return Truncate(candidate);
        }
        return Truncate($"{baseName} ({Guid.NewGuid():N})");
    }

    /// <summary>
    /// Album names are bounded at 255 by the schema, and a suffix must not push
    /// one over it. The TAIL is kept rather than the head, because the suffix is
    /// the part that makes the name free.
    /// </summary>
    private const int MaxAlbumNameLength = 255;

    private static string Truncate(string value) =>
        value.Length <= MaxAlbumNameLength ? value : value[^MaxAlbumNameLength..];
}
