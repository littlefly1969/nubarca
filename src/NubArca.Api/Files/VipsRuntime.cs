using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Files;

// Slice 100: one-time probe + global tuning of the libvips native runtime.
// Registered as a singleton; its constructor forces the NetVips native library
// to load and records whether it succeeded. If the native library is missing
// (e.g. an unsupported RID) or VipsEnabled=false, IsAvailable stays false and
// the pipeline uses ImageSharp — nothing throws.
//
// Global libvips settings are process-wide, so they are applied exactly once
// here: the operation cache is disabled (we never re-run an identical op, so it
// is pure memory overhead) and the worker concurrency is optionally capped for
// small/shared hosts.
public sealed class VipsRuntime
{
    public bool IsAvailable { get; }
    public string? Version { get; }

    public VipsRuntime(IOptions<MediaDerivativesOptions> options, ILogger<VipsRuntime> logger)
    {
        var opts = options.Value;
        if (!opts.VipsEnabled)
        {
            logger.LogInformation("libvips backend disabled by configuration (MediaDerivatives:VipsEnabled=false).");
            IsAvailable = false;
            return;
        }

        try
        {
            // Touch the native library: Version() forces the loader to run.
            var major = NetVips.NetVips.Version(0);
            var minor = NetVips.NetVips.Version(1);
            var micro = NetVips.NetVips.Version(2);
            Version = $"{major}.{minor}.{micro}";

            // Bound memory: derivative generation never re-runs an identical op,
            // so the libvips operation cache only costs RAM.
            NetVips.Cache.Max = 0;

            if (opts.VipsConcurrency > 0)
            {
                NetVips.NetVips.Concurrency = opts.VipsConcurrency;
            }

            IsAvailable = true;
            logger.LogInformation(
                "libvips backend available (v{Version}); concurrency={Concurrency}, operation cache disabled.",
                Version, NetVips.NetVips.Concurrency);
        }
        catch (Exception ex)
        {
            // Missing native lib / unsupported platform → graceful fallback.
            IsAvailable = false;
            logger.LogWarning(
                "libvips backend unavailable ({Type}); falling back to ImageSharp for derivatives.",
                ex.GetType().Name);
        }
    }
}
