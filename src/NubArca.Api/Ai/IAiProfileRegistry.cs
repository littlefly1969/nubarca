using NubArca.Api.Ai.Backends;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai;

// Lightweight lookup over the Phase 0A model/profile registry tables, plus an
// EXPLICIT dev/test seeding helper. Nothing here runs on startup.
public interface IAiProfileRegistry
{
    Task<IReadOnlyList<AiModel>> ListModelsAsync(
        bool enabledOnly = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiProfile>> ListProfilesAsync(
        bool enabledOnly = false, CancellationToken cancellationToken = default);

    // The single default profile for a capability (the partial unique index
    // guarantees at most one), or null.
    Task<AiProfile?> GetDefaultProfileAsync(
        string capability, CancellationToken cancellationToken = default);

    Task<AiProfile?> GetProfileByKeyAsync(
        string key, CancellationToken cancellationToken = default);

    Task<AiModel?> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default);

    // Validate a profile is internally consistent with its model and (when
    // provided) the backend that would serve it. Pure check; no DB writes.
    AiProfileCompatibility ValidateCompatibility(AiProfile profile, AiModel? model, IAiBackend? backend);

    // DEV/TEST ONLY: idempotently seed the deterministic model + per-capability
    // default profiles. Must be called explicitly (a test helper or, later, a
    // CLI command) — never automatically on startup.
    Task<AiSeedResult> SeedDeterministicProfilesAsync(CancellationToken cancellationToken = default);

    // Phase 2A: idempotently seed the ONNX image-embedding EVALUATION models +
    // profiles (provider "onnx"). NOT made default — these are for the eval
    // harness only and are inert until the model files exist. Explicit-only
    // (CLI `ai onnx image seed-profiles`); never auto-run on startup.
    Task<AiSeedResult> SeedOnnxImageEvalProfilesAsync(CancellationToken cancellationToken = default);

    // Idempotently seed the ONNX face-recognition EVALUATION models + profiles
    // (provider "onnx", capability face-embedding). NOT made default and inert
    // until the model files exist. Explicit-only (CLI `ai face seed-profiles`);
    // never auto-run on startup. This branch is evaluation-only — no clustering,
    // names, or persisted face artifacts.
    Task<AiSeedResult> SeedOnnxFaceEvalProfilesAsync(CancellationToken cancellationToken = default);
}

public sealed record AiProfileCompatibility(bool IsCompatible, string? Reason)
{
    public static readonly AiProfileCompatibility Ok = new(true, null);
    public static AiProfileCompatibility Fail(string reason) => new(false, reason);
}

public sealed record AiSeedResult(int ModelsCreated, int ProfilesCreated);
