using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Plates.Alpr;

// Deterministic, dependency-free ALPR pipeline for DEV/TEST ONLY. It produces
// stable, reproducible, NON-SEMANTIC plate detections from the input bytes
// (SHA-256 → fixed boxes + synthesized text), mirroring the AI substrate's
// deterministic backend rule: it is not a real recognizer and must never be
// enabled in production as a substitute for a trained model. No model file, no
// network, no People/Face dependency.

public sealed class DeterministicPlateDetector : IPlateDetector
{
    public string Name => "deterministic-plate-detector";
    public string Version => "v1";

    // Two fixed candidate regions (normalized), so the pipeline exercises
    // multi-detection handling deterministically.
    private static readonly (PlateBox Box, double Confidence)[] Candidates =
    {
        (new PlateBox(0.10, 0.20, 0.30, 0.08), 0.95),
        (new PlateBox(0.55, 0.62, 0.25, 0.07), 0.90),
    };

    public Task<IReadOnlyList<PlateDetectionCandidate>> DetectAsync(
        PlateImageInput image, CancellationToken cancellationToken)
    {
        IReadOnlyList<PlateDetectionCandidate> result = Candidates
            .Select(c => new PlateDetectionCandidate(c.Box, c.Confidence))
            .ToList();
        return Task.FromResult(result);
    }
}

public sealed class DeterministicPlateOcrReader : IPlateOcrReader
{
    public string Name => "deterministic-plate-ocr";
    public string Version => "v1";

    private const string Letters = "ABCDEFGHJKLMNPRSTUVWXYZ";
    private const string Digits = "0123456789";

    public Task<PlateOcrResult> ReadAsync(PlateCropInput crop, CancellationToken cancellationToken)
    {
        // Stable per (image bytes + source box): hash → 2 letters + 3 digits +
        // 2 letters, with separators so the normalizer has something to strip.
        var seed = new byte[crop.Bytes.Length + 32];
        crop.Bytes.CopyTo(seed, 0);
        BitConverter.GetBytes(crop.SourceBox.X).CopyTo(seed, crop.Bytes.Length);
        BitConverter.GetBytes(crop.SourceBox.Y).CopyTo(seed, crop.Bytes.Length + 8);
        BitConverter.GetBytes(crop.SourceBox.Width).CopyTo(seed, crop.Bytes.Length + 16);
        BitConverter.GetBytes(crop.SourceBox.Height).CopyTo(seed, crop.Bytes.Length + 24);
        var hash = SHA256.HashData(seed);

        var raw = new StringBuilder();
        raw.Append(Letters[hash[0] % Letters.Length]);
        raw.Append(Letters[hash[1] % Letters.Length]);
        raw.Append(' ');
        raw.Append(Digits[hash[2] % Digits.Length]);
        raw.Append(Digits[hash[3] % Digits.Length]);
        raw.Append(Digits[hash[4] % Digits.Length]);
        raw.Append(' ');
        raw.Append(Letters[hash[5] % Letters.Length]);
        raw.Append(Letters[hash[6] % Letters.Length]);

        var text = raw.ToString();
        var normalized = PlateTextNormalizer.Normalize(text) ?? text;
        return Task.FromResult(new PlateOcrResult(text, normalized, 0.87));
    }
}

public sealed class DeterministicPlateAnalysisPipeline : IPlateAnalysisPipeline
{
    private readonly IPlateDetector _detector;
    private readonly IPlateOcrReader _ocr;
    private readonly IOptions<PlatesAlprOptions> _options;

    public DeterministicPlateAnalysisPipeline(
        IPlateDetector detector,
        IPlateOcrReader ocr,
        IOptions<PlatesAlprOptions> options)
    {
        _detector = detector;
        _ocr = ocr;
        _options = options;
    }

    // Always runnable when selected: provider routing (the selector) decides
    // whether the deterministic backend is used.
    public bool IsAvailable => true;

    public async Task<PlateAnalysisResult> AnalyzeAsync(
        PlateImageInput image, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var started = Stopwatch.GetTimestamp();

        var candidates = await _detector.DetectAsync(image, cancellationToken);

        var accepted = new List<PlateAnalysisDetection>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Confidence < options.MinPlateConfidence)
            {
                continue;
            }

            var cropWidth = Math.Max(1, (int)Math.Round(candidate.BoundingBox.Width * image.Width));
            var cropHeight = Math.Max(1, (int)Math.Round(candidate.BoundingBox.Height * image.Height));
            var crop = new PlateCropInput(image.Bytes, cropWidth, cropHeight, candidate.BoundingBox);

            var ocr = await _ocr.ReadAsync(crop, cancellationToken);
            var normalized = PlateTextNormalizer.Normalize(ocr.Text);
            if (normalized is null || ocr.Confidence < options.MinOcrConfidence)
            {
                continue;
            }

            var combined = Math.Round((candidate.Confidence + ocr.Confidence) / 2.0, 4);
            accepted.Add(new PlateAnalysisDetection(
                candidate.BoundingBox,
                ocr.Text.Trim(),
                normalized,
                candidate.Confidence,
                ocr.Confidence,
                combined,
                ocr.CountryHint,
                ocr.RegionHint,
                candidate.Polygon));

            if (accepted.Count >= options.MaxDetectionsPerImage)
            {
                break;
            }
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new PlateAnalysisResult(
            accepted,
            durationMs,
            options.ProfileKey,
            _detector.Name, _detector.Version,
            _ocr.Name, _ocr.Version);
    }
}
