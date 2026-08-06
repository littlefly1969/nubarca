using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Onnx.Face;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai;

public sealed class AiProfileRegistry : IAiProfileRegistry
{
    // Stable identity of the dev/test deterministic model.
    private const string DeterministicModelKey = "deterministic-v1";
    private const int DeterministicDimension = 32;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public AiProfileRegistry(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(
        bool enabledOnly = false, CancellationToken cancellationToken = default)
    {
        var query = _db.AiModels.AsNoTracking();
        if (enabledOnly)
        {
            query = query.Where(m => m.Enabled);
        }

        return await query.OrderBy(m => m.Key).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiProfile>> ListProfilesAsync(
        bool enabledOnly = false, CancellationToken cancellationToken = default)
    {
        var query = _db.AiProfiles.AsNoTracking();
        if (enabledOnly)
        {
            query = query.Where(p => p.Enabled);
        }

        return await query.OrderBy(p => p.Key).ToListAsync(cancellationToken);
    }

    public async Task<AiProfile?> GetDefaultProfileAsync(
        string capability, CancellationToken cancellationToken = default)
    {
        // The partial unique index guarantees at most one default per capability.
        return await _db.AiProfiles.AsNoTracking()
            .Where(p => p.Capability == capability && p.IsDefault)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AiProfile?> GetProfileByKeyAsync(
        string key, CancellationToken cancellationToken = default)
    {
        return await _db.AiProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == key, cancellationToken);
    }

    public async Task<AiModel?> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        return await _db.AiModels.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == modelId, cancellationToken);
    }

    public AiProfileCompatibility ValidateCompatibility(AiProfile profile, AiModel? model, IAiBackend? backend)
    {
        if (model is null)
        {
            return AiProfileCompatibility.Fail("model-unavailable");
        }

        if (backend is not null)
        {
            if (!string.Equals(backend.Provider, model.Provider, StringComparison.Ordinal))
            {
                return AiProfileCompatibility.Fail("provider-mismatch");
            }

            if (!backend.Supports(profile.Capability))
            {
                return AiProfileCompatibility.Fail("capability-unsupported");
            }
        }

        if (profile.Dimension.HasValue && model.Dimension.HasValue
            && profile.Dimension.Value != model.Dimension.Value)
        {
            return AiProfileCompatibility.Fail("dimension-mismatch");
        }

        return AiProfileCompatibility.Ok;
    }

    public async Task<AiSeedResult> SeedDeterministicProfilesAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var modelsCreated = 0;
        var profilesCreated = 0;

        var model = await _db.AiModels.FirstOrDefaultAsync(m => m.Key == DeterministicModelKey, cancellationToken);
        if (model is null)
        {
            model = new AiModel
            {
                Id = Guid.NewGuid(),
                Key = DeterministicModelKey,
                Provider = AiProviders.Deterministic,
                Capability = AiCapabilities.ImageEmbedding, // primary; backend is multi-capability
                Modality = AiModalities.Multimodal,
                Version = 1,
                Dimension = DeterministicDimension,
                DistanceMetric = AiDistanceMetrics.Cosine,
                Enabled = true,
                CreatedAt = now,
            };
            _db.AiModels.Add(model);
            modelsCreated++;
        }

        foreach (var spec in DeterministicProfileSpecs)
        {
            var exists = await _db.AiProfiles.AnyAsync(p => p.Key == spec.Key, cancellationToken);
            if (exists)
            {
                continue;
            }

            _db.AiProfiles.Add(new AiProfile
            {
                Id = Guid.NewGuid(),
                Key = spec.Key,
                AiModelId = model.Id,
                Capability = spec.Capability,
                Modality = spec.Modality,
                Dimension = spec.Dimension,
                DistanceMetric = spec.Dimension.HasValue ? AiDistanceMetrics.Cosine : null,
                IsDefault = true,
                Enabled = true,
                CreatedAt = now,
            });
            profilesCreated++;
        }

        if (modelsCreated > 0 || profilesCreated > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new AiSeedResult(modelsCreated, profilesCreated);
    }

    public async Task<AiSeedResult> SeedOnnxImageEvalProfilesAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var modelsCreated = 0;
        var profilesCreated = 0;

        foreach (var (profileKey, catalogKey) in OnnxImageModels.ProfileToCatalogKey)
        {
            var config = OnnxImageModels.Catalog[catalogKey];

            var model = await _db.AiModels.FirstOrDefaultAsync(m => m.Key == config.Key, cancellationToken);
            if (model is null)
            {
                model = new AiModel
                {
                    Id = Guid.NewGuid(),
                    Key = config.Key,
                    Provider = AiProviders.Onnx,
                    Capability = AiCapabilities.ImageEmbedding,
                    Modality = AiModalities.Multimodal,
                    Version = 1,
                    Dimension = config.Dimension,
                    DistanceMetric = AiDistanceMetrics.Cosine,
                    Enabled = true,
                    CreatedAt = now,
                };
                _db.AiModels.Add(model);
                modelsCreated++;
            }
            else if (model.Key == OnnxImageModels.SiglipSo400mKey)
            {
                // The same checkpoint key existed in the old image-only eval
                // catalog. Promote its metadata to the paired multimodal
                // contract; identity/provider stay stable.
                if (model.Modality != AiModalities.Multimodal
                    || model.Dimension != config.Dimension
                    || model.DistanceMetric != AiDistanceMetrics.Cosine)
                {
                    model.Modality = AiModalities.Multimodal;
                    model.Dimension = config.Dimension;
                    model.DistanceMetric = AiDistanceMetrics.Cosine;
                    model.UpdatedAt = now;
                }
            }

            var exists = await _db.AiProfiles.AnyAsync(p => p.Key == profileKey, cancellationToken);
            if (!exists)
            {
                _db.AiProfiles.Add(new AiProfile
                {
                    Id = Guid.NewGuid(),
                    Key = profileKey,
                    AiModelId = model.Id,
                    Capability = AiCapabilities.ImageEmbedding,
                    Modality = AiModalities.Multimodal,
                    Dimension = config.Dimension,
                    DistanceMetric = AiDistanceMetrics.Cosine,
                    // Eval-only: NEVER the active default (keeps the partial unique
                    // default-per-capability index free for the chosen prod model).
                    IsDefault = false,
                    Enabled = true,
                    // Links the profile to its code-side preprocessing config.
                    ConfigHash = config.Key,
                    CreatedAt = now,
                });
                profilesCreated++;
            }
        }

        if (_db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new AiSeedResult(modelsCreated, profilesCreated);
    }

    public async Task<AiSeedResult> SeedOnnxFaceEvalProfilesAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var modelsCreated = 0;
        var profilesCreated = 0;

        foreach (var (profileKey, catalogKey) in OnnxFaceModels.ProfileToCatalogKey)
        {
            var config = OnnxFaceModels.Catalog[catalogKey];

            var model = await _db.AiModels.FirstOrDefaultAsync(m => m.Key == config.Key, cancellationToken);
            if (model is null)
            {
                model = new AiModel
                {
                    Id = Guid.NewGuid(),
                    Key = config.Key,
                    Provider = AiProviders.Onnx,
                    // The substrate models a face package by its recognition
                    // (embedding) capability; detection is part of the same package.
                    Capability = AiCapabilities.FaceEmbedding,
                    Modality = AiModalities.Face,
                    Version = 1,
                    Dimension = config.Dimension,
                    DistanceMetric = AiDistanceMetrics.Cosine,
                    Enabled = true,
                    CreatedAt = now,
                };
                _db.AiModels.Add(model);
                modelsCreated++;
            }

            var exists = await _db.AiProfiles.AnyAsync(p => p.Key == profileKey, cancellationToken);
            if (!exists)
            {
                _db.AiProfiles.Add(new AiProfile
                {
                    Id = Guid.NewGuid(),
                    Key = profileKey,
                    AiModelId = model.Id,
                    Capability = AiCapabilities.FaceEmbedding,
                    Modality = AiModalities.Face,
                    Dimension = config.Dimension,
                    DistanceMetric = AiDistanceMetrics.Cosine,
                    // Eval-only: NEVER the active default (keeps the partial unique
                    // default-per-capability index free) and never enables face
                    // processing on its own.
                    IsDefault = false,
                    Enabled = true,
                    // Links the profile to its code-side detector/recognition config.
                    ConfigHash = config.Key,
                    CreatedAt = now,
                });
                profilesCreated++;
            }
        }

        if (modelsCreated > 0 || profilesCreated > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new AiSeedResult(modelsCreated, profilesCreated);
    }

    private sealed record ProfileSpec(string Key, string Capability, string Modality, int? Dimension);

    private static readonly ProfileSpec[] DeterministicProfileSpecs =
    {
        new("det-image-embedding-v1", AiCapabilities.ImageEmbedding, AiModalities.Image, DeterministicDimension),
        new("det-document-embedding-v1", AiCapabilities.DocumentEmbedding, AiModalities.Text, DeterministicDimension),
        new("det-face-embedding-v1", AiCapabilities.FaceEmbedding, AiModalities.Face, DeterministicDimension),
        new("det-document-extraction-v1", AiCapabilities.DocumentExtraction, AiModalities.Document, null),
        new("det-face-detection-v1", AiCapabilities.FaceDetection, AiModalities.Image, null),
        new("det-tagging-v1", AiCapabilities.Tagging, AiModalities.Image, null),
        new("det-captioning-v1", AiCapabilities.Captioning, AiModalities.Image, null),
    };
}
