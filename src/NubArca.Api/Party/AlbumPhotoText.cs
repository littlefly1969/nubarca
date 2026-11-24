using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Party;

public sealed record AlbumPhotoTextDto(string? Text, PhotoTextLayout? Layout, long Version);
public sealed record AlbumPhotoTextWrite(string? Text, PhotoTextLayout? Layout, long ExpectedVersion);

public static class AlbumPhotoText
{
    public const int MaxLength = 300;

    // The same rule drives the badge, the editor and the write authorization.
    // Missing legacy provenance never grants a guest someone else's photograph.
    public static Task<bool> IsUploaderAsync(AppDbContext db, Guid albumId, Guid fileId,
        Guid? participantId, Guid? userId, CancellationToken ct) =>
        db.AlbumItems.AnyAsync(i => i.AlbumId == albumId && i.FileItemId == fileId
            && (db.PartyUploadItems.Any(u => u.AlbumId == albumId && u.FileItemId == fileId
                    && participantId != null && u.PartyParticipantId == participantId)
                || (userId != null && i.AddedByUserId == userId
                    && !db.PartyUploadItems.Any(u => u.FileItemId == fileId))), ct);

    public static async Task<AlbumPhotoTextDto?> ReadAsync(AppDbContext db, Guid albumId, Guid fileId, CancellationToken ct) =>
        await db.AlbumItems.AsNoTracking().Where(i => i.AlbumId == albumId && i.FileItemId == fileId)
            .Select(i => new AlbumPhotoTextDto(i.PhotoText, i.PhotoTextLayout, i.PhotoTextVersion)).FirstOrDefaultAsync(ct);

    public static bool TryText(string? raw, PhotoTextLayout? layout, out string? text)
    {
        text = PartyMessageText.NormalizeMultiline(raw);
        if (text.Length == 0) { text = null; return true; }
        return PartyMessageText.Length(text) <= MaxLength && !text.Any(char.IsSurrogate)
            && layout is { Mode: "overlay" or "bubble" } && layout.IsValid();
    }

    public static async Task<string?> WriteAsync(AppDbContext db, Guid albumId, Guid fileId,
        AlbumPhotoTextWrite write, CancellationToken ct)
    {
        if (write.ExpectedVersion < 0 || !TryText(write.Text, write.Layout, out var text)) return "invalid_text";
        var item = await db.AlbumItems.SingleOrDefaultAsync(i => i.AlbumId == albumId && i.FileItemId == fileId, ct);
        if (item is null) return "not_found";
        if (item.PhotoTextVersion != write.ExpectedVersion) return "photo_changed";
        item.PhotoText = text;
        item.PhotoTextLayout = text is null ? null : write.Layout;
        item.PhotoTextVersion++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return "photo_changed"; }
        return null;
    }

    // Applied BEFORE print framing: an already published bubble is part of the
    // chosen photograph. A personal print bubble is drawn later, over the slot.
    public static async Task<byte[]> RenderAsync(byte[] bytes, AlbumPhotoTextDto? text, CancellationToken ct)
    {
        if (text?.Text is null || text.Layout is null) return bytes;
        using var image = Image.Load<Rgba32>(bytes);
        image.Mutate(x => x.AutoOrient());
        PhotoTextRenderer.Draw(image, new Rectangle(0, 0, image.Width, image.Height), text.Text, text.Layout);
        image.Metadata.ExifProfile = null; image.Metadata.XmpProfile = null;
        image.Metadata.IptcProfile = null; image.Metadata.IccProfile = null;
        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 95 }, ct);
        return output.ToArray();
    }
}
