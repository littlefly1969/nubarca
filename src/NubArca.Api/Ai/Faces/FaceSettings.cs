using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces;

// Central, sanitized view of the face-substrate tunables (similarity thresholds
// + safety caps). This is the SINGLE place clustering/search/diagnostics read
// thresholds from — never hardcoded at a call site.
//
// Effective value = config default (AiOptions.Face) OVERLAID by any persisted,
// admin-set override in the ai_settings table. That gives config-first defaults
// plus admin-editable overrides WITHOUT restarts, and keeps a clean seam. Nothing
// here exposes a raw vector, model internal, or storage identifier; only
// non-secret numeric settings are ever persisted.
public sealed record FaceSettings(
    double ClusterSimilarityThreshold,
    double CandidateSimilarityThreshold,
    double SearchDefaultSimilarityThreshold,
    double SearchMinSimilarity,
    double SearchMaxSimilarity,
    int MaxFacesPerImage,
    double KnnLouvainResolution)
{
    // Clamp an incoming search threshold into the admin-configured [min, max] band.
    public double ClampSearchThreshold(double requested) =>
        Math.Clamp(requested, SearchMinSimilarity, SearchMaxSimilarity);
}

// Partial update (each field optional) applied by the admin PUT.
public sealed record FaceSettingsUpdate(
    double? ClusterSimilarityThreshold = null,
    double? CandidateSimilarityThreshold = null,
    double? SearchDefaultSimilarityThreshold = null,
    double? SearchMinSimilarity = null,
    double? SearchMaxSimilarity = null,
    int? MaxFacesPerImage = null,
    double? KnnLouvainResolution = null);

// Stable ai_settings keys for the persisted face-threshold overrides.
public static class FaceSettingKeys
{
    public const string ClusterSimilarityThreshold = "ai.face.clusterSimilarityThreshold";
    public const string CandidateSimilarityThreshold = "ai.face.candidateSimilarityThreshold";
    public const string SearchDefaultSimilarityThreshold = "ai.face.searchDefaultSimilarityThreshold";
    public const string SearchMinSimilarity = "ai.face.searchMinSimilarity";
    public const string SearchMaxSimilarity = "ai.face.searchMaxSimilarity";
    public const string MaxFacesPerImage = "ai.face.maxFacesPerImage";
    public const string KnnLouvainResolution = "ai.face.knnLouvainResolution";
}

public interface IFaceSettingsProvider
{
    // The currently-active, effective (config + DB override) face settings.
    Task<FaceSettings> GetAsync(CancellationToken cancellationToken = default);
}

// Scoped: reads config defaults + ai_settings overrides, and applies validated
// admin updates. Registered as IFaceSettingsProvider AND concretely (for the
// admin write path).
public sealed class FaceSettingsService : IFaceSettingsProvider
{
    private readonly AppDbContext _db;
    private readonly IOptions<AiOptions> _options;
    private readonly TimeProvider _clock;

    public FaceSettingsService(AppDbContext db, IOptions<AiOptions> options, TimeProvider clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    public async Task<FaceSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var overrides = await _db.AiSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("ai.face."))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        var f = _options.Value.Face;
        return new FaceSettings(
            Dbl(overrides, FaceSettingKeys.ClusterSimilarityThreshold, f.ClusterSimilarityThreshold),
            Dbl(overrides, FaceSettingKeys.CandidateSimilarityThreshold, f.CandidateSimilarityThreshold),
            Dbl(overrides, FaceSettingKeys.SearchDefaultSimilarityThreshold, f.SearchDefaultSimilarityThreshold),
            Dbl(overrides, FaceSettingKeys.SearchMinSimilarity, f.SearchMinSimilarity),
            Dbl(overrides, FaceSettingKeys.SearchMaxSimilarity, f.SearchMaxSimilarity),
            Int(overrides, FaceSettingKeys.MaxFacesPerImage, f.MaxFacesPerImage),
            Dbl(overrides, FaceSettingKeys.KnnLouvainResolution, f.KnnLouvainResolution));
    }

    // Validate + persist an admin update. Returns the new effective settings, or
    // throws FaceSettingsValidationException on a range violation (nothing is
    // written on failure).
    public async Task<FaceSettings> UpdateAsync(
        FaceSettingsUpdate update, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(cancellationToken);
        var merged = new FaceSettings(
            update.ClusterSimilarityThreshold ?? current.ClusterSimilarityThreshold,
            update.CandidateSimilarityThreshold ?? current.CandidateSimilarityThreshold,
            update.SearchDefaultSimilarityThreshold ?? current.SearchDefaultSimilarityThreshold,
            update.SearchMinSimilarity ?? current.SearchMinSimilarity,
            update.SearchMaxSimilarity ?? current.SearchMaxSimilarity,
            update.MaxFacesPerImage ?? current.MaxFacesPerImage,
            update.KnnLouvainResolution ?? current.KnnLouvainResolution);

        var errors = FaceSettingsValidation.Validate(merged);
        if (errors.Count > 0)
        {
            throw new FaceSettingsValidationException(errors);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        await UpsertAsync(FaceSettingKeys.ClusterSimilarityThreshold, Str(merged.ClusterSimilarityThreshold), adminUserId, now, cancellationToken);
        await UpsertAsync(FaceSettingKeys.CandidateSimilarityThreshold, Str(merged.CandidateSimilarityThreshold), adminUserId, now, cancellationToken);
        await UpsertAsync(FaceSettingKeys.SearchDefaultSimilarityThreshold, Str(merged.SearchDefaultSimilarityThreshold), adminUserId, now, cancellationToken);
        await UpsertAsync(FaceSettingKeys.SearchMinSimilarity, Str(merged.SearchMinSimilarity), adminUserId, now, cancellationToken);
        await UpsertAsync(FaceSettingKeys.SearchMaxSimilarity, Str(merged.SearchMaxSimilarity), adminUserId, now, cancellationToken);
        await UpsertAsync(FaceSettingKeys.MaxFacesPerImage, merged.MaxFacesPerImage.ToString(CultureInfo.InvariantCulture), adminUserId, now, cancellationToken);
        await UpsertAsync(FaceSettingKeys.KnnLouvainResolution, Str(merged.KnnLouvainResolution), adminUserId, now, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return merged;
    }

    private async Task UpsertAsync(string key, string value, Guid adminUserId, DateTime now, CancellationToken cancellationToken)
    {
        var row = await _db.AiSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            _db.AiSettings.Add(new AiSetting { Key = key, Value = value, UpdatedAt = now, UpdatedByUserId = adminUserId });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = now;
            row.UpdatedByUserId = adminUserId;
        }
    }

    private static double Dbl(IReadOnlyDictionary<string, string> o, string key, double fallback)
        => o.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : fallback;

    private static int Int(IReadOnlyDictionary<string, string> o, string key, int fallback)
        => o.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    private static string Str(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

// Shared range validation for the face thresholds (used by the admin write path
// AND the config-level options validator). Ranges: every similarity in [0, 1],
// min < max, default within [min, max], MaxFacesPerImage in [1, 1000], all finite.
public static class FaceSettingsValidation
{
    public static IReadOnlyList<string> Validate(FaceSettings s)
    {
        var errors = new List<string>();

        void Unit(string label, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0d || v > 1d)
            {
                errors.Add($"{label} must be a finite value within [0, 1] (was {v}).");
            }
        }

        Unit(nameof(s.ClusterSimilarityThreshold), s.ClusterSimilarityThreshold);
        Unit(nameof(s.CandidateSimilarityThreshold), s.CandidateSimilarityThreshold);
        Unit(nameof(s.SearchDefaultSimilarityThreshold), s.SearchDefaultSimilarityThreshold);
        Unit(nameof(s.SearchMinSimilarity), s.SearchMinSimilarity);
        Unit(nameof(s.SearchMaxSimilarity), s.SearchMaxSimilarity);

        if (s.SearchMinSimilarity >= s.SearchMaxSimilarity)
        {
            errors.Add($"SearchMinSimilarity ({s.SearchMinSimilarity}) must be strictly less than SearchMaxSimilarity ({s.SearchMaxSimilarity}).");
        }
        else if (s.SearchDefaultSimilarityThreshold < s.SearchMinSimilarity
            || s.SearchDefaultSimilarityThreshold > s.SearchMaxSimilarity)
        {
            errors.Add($"SearchDefaultSimilarityThreshold ({s.SearchDefaultSimilarityThreshold}) must be within [SearchMinSimilarity, SearchMaxSimilarity].");
        }

        if (s.MaxFacesPerImage < 1 || s.MaxFacesPerImage > 1000)
        {
            errors.Add($"MaxFacesPerImage ({s.MaxFacesPerImage}) must be within [1, 1000].");
        }

        if (double.IsNaN(s.KnnLouvainResolution) || double.IsInfinity(s.KnnLouvainResolution)
            || s.KnnLouvainResolution < 0.1 || s.KnnLouvainResolution > 5.0)
        {
            errors.Add($"KnnLouvainResolution ({s.KnnLouvainResolution}) must be a finite value within [0.1, 5.0].");
        }

        return errors;
    }
}

public sealed class FaceSettingsValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public FaceSettingsValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors))
    {
        Errors = errors;
    }
}

// Fail-fast validation of the CONFIG-level face thresholds so a bad appsettings/
// env value can never silently distort clustering/search at startup.
public sealed class AiFaceOptionsValidator : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var f = options.Face;
        var settings = new FaceSettings(
            f.ClusterSimilarityThreshold,
            f.CandidateSimilarityThreshold,
            f.SearchDefaultSimilarityThreshold,
            f.SearchMinSimilarity,
            f.SearchMaxSimilarity,
            f.MaxFacesPerImage,
            f.KnnLouvainResolution);

        var errors = FaceSettingsValidation.Validate(settings);
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors.Select(e => "Ai:Face:" + e));
    }
}
