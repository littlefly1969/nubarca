using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Party;
using NubArca.Api.Storage;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Print;

/// <summary>Prints a published memory; never creates an album file or changes its dedication.</summary>
public sealed class GuestbookPrintSource(AppDbContext db, IPartyLinkService links,
    IPartyGuestbookService book, PartyGuestbookPhotoCache previews, IBlobService blobs, IOriginalImageReader originals)
{
    public async Task<PartyGuestbookEntry?> FindAsync(PartyPrintAccess access, Guid? guest, Guid entryId, CancellationToken ct)
    {
        var token = links.DeriveViewToken(access.PartyAlbumLinkId);
        var view = await links.ResolvePublicAsync(token, ct);
        if (view is null || view.OwnerUserId != access.OwnerUserId || view.MainAlbumId != access.PartyAlbumId) return null;
        // Reuse the book's visibility and moderation rules, including "only mine".
        var page = await book.GetPublicPageAsync(view, token, guest, ct);
        if (page is null || !page.Entries.Any(e => e.Id == entryId)) return null;
        return await db.PartyGuestbookEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entryId && e.PartyId == view.PartyId, ct);
    }

    public async Task<byte[]?> RenderAsync(PartyGuestbookEntry entry, bool preview, CancellationToken ct)
    {
        if (preview)
        {
            await using var stream = await previews.OpenAsync(entry.Id, ct);
            if (stream is null) return null;
            using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, ct);
            return await DrawAsync(entry, buffer.ToArray(), 1000, ct);
        }
        await using var source = await OriginalPixels.OpenAsync(blobs, originals, entry.BlobObjectId, ct);
        return await DrawAsync(entry, source.Bytes, 1800, ct);
    }

    internal static async Task<byte[]> DrawAsync(PartyGuestbookEntry entry, byte[] bytes, int width, CancellationToken ct)
    {
        using var photo = Image.Load<Rgba32>(bytes);
        photo.Mutate(x => x.AutoOrient());
        var aspect = (double)photo.Width / photo.Height;
        var frame = PartyGuestbookTemplates.FrameAspect(entry.TemplateKey, entry.TemplateVersion, aspect);
        var light = entry.TemplateKey is PartyGuestbookTemplates.Polaroid or PartyGuestbookTemplates.Editorial;
        var paper = Color.ParseHex(light ? (entry.TemplateKey == PartyGuestbookTemplates.Polaroid ? "f6f4ef" : "f5f7fb") : "0f1e3a");
        var ink = light ? Color.ParseHex("1d2433") : Color.ParseHex("f5f7fb");
        var margin = entry.TemplateKey == PartyGuestbookTemplates.Polaroid ? width * 3 / 100 : 0;
        var inset = width * .045f;
        var photoWidth = width - margin * 2;
        var photoHeight = (int)Math.Round(photoWidth / frame);
        var overlay = entry.TextLayout is { Mode: not "below" };
        var font = TextFonts.Family(null).CreateFont(width * .032f);
        var words = new RichTextOptions(font) { WrappingLength = width - inset * 2, LineSpacing = 1.55f };
        var measured = TextMeasurer.MeasureSize(entry.Body, words);
        var author = new RichTextOptions(font) { WrappingLength = width - inset * 2 };
        var authorHeight = TextMeasurer.MeasureSize("— " + entry.AuthorDisplayName, author).Height;
        var bottom = overlay ? margin : (int)Math.Ceiling(measured.Height + authorHeight + inset * 3);
        using var card = new Image<Rgba32>(width, margin + photoHeight + bottom, paper.ToPixel<Rgba32>());
        var slot = new Rectangle(margin, margin, photoWidth, photoHeight);
        PartyPrintComposer.DrawPlaced(card, photo, new PhotoPlacement(entry.CropCenterX, entry.CropCenterY, entry.CropZoom), slot,
            Color.ParseHex(light && entry.TemplateKey == PartyGuestbookTemplates.Polaroid ? "d9d6cf" : "0a0f1a").ToPixel<Rgba32>());
        if (overlay)
            PhotoTextRenderer.Draw(card, slot, entry.Body + "\n— " + entry.AuthorDisplayName, entry.TextLayout);
        else
        {
            words.Origin = new PointF(inset, margin + photoHeight + inset);
            author.Origin = new PointF(inset, words.Origin.Y + measured.Height + inset * .65f);
            card.Mutate(x => x.DrawText(words, entry.Body, ink).DrawText(author, "— " + entry.AuthorDisplayName, ink));
        }
        await using var output = new MemoryStream();
        await card.SaveAsJpegAsync(output, new JpegEncoder { Quality = 95 }, ct);
        return output.ToArray();
    }
}
