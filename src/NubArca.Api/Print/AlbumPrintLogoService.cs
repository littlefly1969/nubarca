using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Party;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Print;

/// <summary>
/// An album's own logo for printed sheets, and whether a party's guests may
/// print with no logo at all.
///
/// <para>The logo lives on the ALBUM because every way of printing has one: a
/// party prints from its main album, a share prints its album, and the owner
/// prints from an album. A party therefore inherits its main album's logo with
/// nothing of its own to keep in step.</para>
///
/// <para>The logo is a reference to one of the owner's ordinary image files,
/// judged by the one rule every Party reference is (<see cref="PartyMediaReference"/>):
/// when it is written, and again whenever a sheet or a preview uses it.</para>
/// </summary>
public interface IAlbumPrintLogoService
{
    /// <summary>The album's logo settings, or null for an album that is not the caller's own.</summary>
    Task<AlbumPrintLogoDto?> GetAsync(Guid albumId, Guid ownerUserId, CancellationToken cancellationToken);

    Task<AlbumPrintLogoResult> SetAsync(
        Guid albumId, Guid ownerUserId, Guid? fileItemId, bool optional, CancellationToken cancellationToken);

    /// <summary>
    /// The album's logo, while it is still one the owner may use — the file id
    /// the print reads — or null.
    /// </summary>
    Task<Guid?> UsableLogoAsync(Guid albumId, Guid ownerUserId, CancellationToken cancellationToken);

    /// <summary>
    /// A small PNG of the logo for a preview — never the original: scaled to
    /// at most <see cref="AlbumPrintLogoService.PreviewMaxSide"/>, transparency
    /// kept, every metadata profile dropped. Null when the file is not one the
    /// owner may use, or cannot be read.
    /// </summary>
    Task<byte[]?> PreviewAsync(Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken);
}

/// <summary>The owner's view of an album's logo: which file, how to preview it, and the guests' option.</summary>
public sealed record AlbumPrintLogoDto(Guid? FileItemId, string? PreviewUrl, bool Optional);

public sealed record AlbumPrintLogoResult(AlbumPrintLogoDto? Settings, string? Error)
{
    public const string NotFound = "not_found";
    public const string InvalidMedia = "invalid_media";

    public static AlbumPrintLogoResult Ok(AlbumPrintLogoDto settings) => new(settings, null);
    public static AlbumPrintLogoResult Refuse(string error) => new(null, error);
}

public sealed class AlbumPrintLogoService : IAlbumPrintLogoService
{
    /// <summary>The longest side of a logo preview, in pixels: enough for a sharp mark on a phone.</summary>
    public const int PreviewMaxSide = 512;

    private readonly AppDbContext _db;
    private readonly IPrintPhotoSourceReader _sources;
    private readonly TimeProvider _clock;

    public AlbumPrintLogoService(AppDbContext db, IPrintPhotoSourceReader sources, TimeProvider clock)
    {
        _db = db;
        _sources = sources;
        _clock = clock;
    }

    public async Task<AlbumPrintLogoDto?> GetAsync(Guid albumId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var album = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => new { a.PrintLogoFileItemId, a.PrintLogoOptional })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null) return null;
        return await DtoAsync(albumId, ownerUserId, album.PrintLogoFileItemId, album.PrintLogoOptional, cancellationToken);
    }

    public async Task<AlbumPrintLogoResult> SetAsync(
        Guid albumId, Guid ownerUserId, Guid? fileItemId, bool optional, CancellationToken cancellationToken)
    {
        // Owner only: the logo is a reference to the owner's own files, which
        // an editor of the album has no say over.
        var album = await _db.Albums
            .FirstOrDefaultAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (album is null) return AlbumPrintLogoResult.Refuse(AlbumPrintLogoResult.NotFound);
        if (fileItemId is Guid id && !await PartyMediaReference.IsEligibleAsync(_db, ownerUserId, id, cancellationToken))
            return AlbumPrintLogoResult.Refuse(AlbumPrintLogoResult.InvalidMedia);

        album.PrintLogoFileItemId = fileItemId;
        album.PrintLogoOptional = optional;
        album.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return AlbumPrintLogoResult.Ok(
            await DtoAsync(albumId, ownerUserId, fileItemId, optional, cancellationToken));
    }

    public async Task<Guid?> UsableLogoAsync(Guid albumId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var logo = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => a.PrintLogoFileItemId)
            .FirstOrDefaultAsync(cancellationToken);
        return logo is Guid id && await PartyMediaReference.IsEligibleAsync(_db, ownerUserId, id, cancellationToken)
            ? id
            : null;
    }

    public async Task<byte[]?> PreviewAsync(Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken)
    {
        if (!await PartyMediaReference.IsEligibleAsync(_db, ownerUserId, fileItemId, cancellationToken)) return null;
        byte[]? original;
        await using (var sources = await _sources.OpenAsync(ownerUserId, [fileItemId], cancellationToken))
        {
            original = sources?.Photos[0];
        }
        if (original is null) return null;
        try
        {
            return await RenderPreviewAsync(original, cancellationToken);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            return null;
        }
    }

    /// <summary>The preview's pixels: upright, at most <see cref="PreviewMaxSide"/>, no metadata, PNG.</summary>
    internal static async Task<byte[]> RenderPreviewAsync(byte[] original, CancellationToken cancellationToken)
    {
        using var image = Image.Load<Rgba32>(original);
        image.Mutate(x => x.AutoOrient());
        PrintColourSpace.ToSrgb(image, image.Metadata);
        if (Math.Max(image.Width, image.Height) > PreviewMaxSide)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(PreviewMaxSide, PreviewMaxSide),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.Lanczos3,
            }));
        }
        image.Metadata.ExifProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.IccProfile = null;
        using var output = new MemoryStream();
        await image.SaveAsPngAsync(output, new PngEncoder { ColorType = PngColorType.RgbWithAlpha }, cancellationToken);
        return output.ToArray();
    }

    private async Task<AlbumPrintLogoDto> DtoAsync(
        Guid albumId, Guid ownerUserId, Guid? fileItemId, bool optional, CancellationToken cancellationToken)
    {
        // A reference the owner can no longer use (Trash, Private Vault) is
        // still shown as chosen, without a preview, so the owner sees why the
        // studio stopped offering it.
        var previewable = fileItemId is Guid id
            && await PartyMediaReference.IsEligibleAsync(_db, ownerUserId, id, cancellationToken);
        return new AlbumPrintLogoDto(
            fileItemId,
            previewable ? $"/api/albums/{albumId}/print-logo/image?v={fileItemId:N}" : null,
            optional);
    }
}
