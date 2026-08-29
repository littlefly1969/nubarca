namespace NubArca.Api.Ai.DocumentVisual;

/// Configuration for visual document retrieval.
///
/// OFF BY DEFAULT, like every AI capability in NubArca and for a sharper reason
/// than usual: switching this on re-renders and re-embeds every document in
/// every library, which is real CPU and real storage on somebody else's server.
/// An introduction commit that silently changed the cost of every installation
/// would be a decision made on the operator's behalf.
///
/// EVERY BOUND IS FINITE AND HARD-CLAMPED. None of these has a value meaning
/// "unlimited": an operator can tune within a range the code chose, and cannot
/// remove a limit. The defaults below are starting points measured against the
/// evaluation corpus, not received wisdom — `documents visual-evaluate` reports
/// what they actually cost.
public sealed class DocumentVisualOptions
{
    public const string SectionName = "Ai:DocumentVisual";

    /// Whether visual indexing and visual retrieval run at all.
    public bool Enabled { get; set; }

    /// The dense visual profile key. Dense is the MANDATORY production baseline:
    /// there is no configuration in which visual retrieval runs without it.
    public string DenseProfileKey { get; set; } = DocumentVisualProfiles.DenseSiglip2So400m;

    /// Whether DOCX/XLSX/PPTX are laid out through the isolated Office renderer
    /// worker. Separate from `Enabled` because it depends on a worker being
    /// deployed: an installation that enables visual retrieval without the
    /// worker gets PDF and text visual search, and its Office documents stay
    /// text-only rather than the whole feature refusing to start.
    public bool RenderOfficeEnabled { get; set; }

    /// Whether the late-interaction reranker runs. Off unless a candidate model
    /// cleared the promotion gate AND an operator turned it on.
    public bool LateInteractionEnabled { get; set; }

    /// The late-interaction profile key. Empty means none is promoted, which is
    /// this release's shipped state.
    public string? LateProfileKey { get; set; }

    /// Unix socket the Office renderer worker listens on. A socket rather than a
    /// TCP port so the worker can run with no network stack at all — see
    /// docker-compose.document-renderer.yml.
    public string? OfficeRendererSocketPath { get; set; }

    /// Unix socket for the optional late-interaction model worker. Absent
    /// unless a late profile was promoted and deployed.
    public string? LateInteractionSocketPath { get; set; }

    // ---- rendering bounds ---------------------------------------------------

    /// Completeness-critical: a document with more units than this is REFUSED,
    /// never rendered up to the bound. Slice 4's rule, restated for pages.
    public int MaxVisualUnitsPerDocument { get; set; } = 400;

    public int MaxVisualPixelsPerUnit { get; set; } = 4_000_000;

    /// The whole-document pixel budget, checked as pages accumulate. A document
    /// of many small pages and one of few enormous ones cost the same here.
    public long MaxVisualTotalPixelsPerDocument { get; set; } = 400_000_000;

    /// Encoded bytes of a single rendered unit.
    public int MaxVisualImageBytesPerUnit { get; set; } = 16 * 1024 * 1024;

    /// Bound on the temporary PDF the Office renderer produces. A 2 KiB DOCX can
    /// lay out into something enormous, and the worker's disk is small.
    public int MaxRenderedPdfBytes { get; set; } = 128 * 1024 * 1024;

    /// Wall clock for one whole Office document conversion, after which the
    /// worker kills the process group. LibreOffice is the hostile-input native
    /// code in this system; a hang is an expected outcome, not an exception.
    public int MaxOfficeRenderSeconds { get; set; } = 120;

    /// Wall clock for rasterising one page.
    public int MaxPageRenderSeconds { get; set; } = 30;

    /// The rasterisation DPI for PDF and Office pages. SigLIP2 resizes to 384²
    /// anyway, so this decides how much detail survives the downsample, not the
    /// output size.
    public int RenderDpi { get; set; } = 150;

    // ---- text canvas --------------------------------------------------------

    public int TextCanvasWidth { get; set; } = 1_240;
    public int TextCanvasHeight { get; set; } = 1_754;

    /// Where the canvas font FILE lives, for a host that keeps its fonts
    /// somewhere the standard search does not look. It cannot change WHICH font
    /// is used — the renderer verifies the loaded face's family name — so the
    /// render profile key stays a true statement about the pixels.
    public string? TextCanvasFontDir { get; set; }

    // ---- concurrency --------------------------------------------------------

    public int MaxConcurrentVisualDocuments { get; set; } = 1;
    public int MaxConcurrentVisualPageEmbeddings { get; set; } = 1;

    // ---- multi-vector ceilings ---------------------------------------------

    /// The declared ceiling on a late-interaction model's output. A model that
    /// produces more FAILS THE PROFILE — vectors are never truncated to fit,
    /// because a MaxSim over a truncated page is a score for a document that
    /// does not exist.
    public int MaxVectorsPerVisualUnit { get; set; } = 1_030;

    public int MaxLateInteractionDimension { get; set; } = 256;

    public int MaxMultiVectorBytesPerUnit { get; set; } = 2 * 1024 * 1024;

    /// How many dense candidates the exact MaxSim reranker will load and score.
    public int MaxMultiVectorCandidateUnits { get; set; } = 100;

    /// The ceiling on an owner's visual corpus when there is no pgvector
    /// accelerator and dense search is exact. Past it, visual retrieval reports
    /// itself UNAVAILABLE — it never ranks an arbitrary prefix of somebody's
    /// library and presents the result as their documents.
    public int MaxVisualUnitsPerOwnerExactFallback { get; set; } = 50_000;

    // ---- retrieval ----------------------------------------------------------

    /// Visual units returned by the dense pass before aggregation to files.
    public int VisualUnitCandidates { get; set; } = 60;

    /// How many FILES a visual pass may introduce as candidates for the scoped
    /// text retrieval that follows.
    public int VisualCandidateFiles { get; set; } = 8;

    /// HOW FAR BEHIND THE BEST MATCH a unit may be and still count as a hit.
    ///
    /// A bare top-K over cosine is not a set of matches, it is a sorted copy of
    /// the corpus: in a small library it returns every document the owner has,
    /// which turns "scope the text pass to what looks relevant" into "scope it
    /// to everything" and quietly deletes the entire point of the visual pass.
    ///
    /// A RELATIVE floor rather than an absolute one, and that is deliberate.
    /// Cross-modal cosine is not calibrated across checkpoints — SigLIP2's
    /// image/text similarities live in a narrow band that has nothing to do with
    /// where another model's do — so "0.2 is a match" is a statement about one
    /// set of weights, and hard-coding it would be a calibration claim this
    /// slice has not measured. "Within this much of the best thing found" needs
    /// no calibration and survives a model swap.
    ///
    /// `MinimumVisualScore` is the absolute companion, defaulting to just above
    /// zero: a NEGATIVE cosine is not a weak match, it is the opposite of one,
    /// and no floor should ever let one through. An operator who has measured
    /// their own corpus can raise it.
    public double VisualRelativeFloor { get; set; } = 0.5;

    public double MinimumVisualScore { get; set; } = 1e-6;

    // ---- clamps -------------------------------------------------------------

    public int EffectiveMaxVisualUnitsPerDocument => Math.Clamp(MaxVisualUnitsPerDocument, 1, 5_000);
    public int EffectiveMaxVisualPixelsPerUnit => Math.Clamp(MaxVisualPixelsPerUnit, 10_000, 40_000_000);
    public long EffectiveMaxVisualTotalPixelsPerDocument
        => Math.Clamp(MaxVisualTotalPixelsPerDocument, 10_000L, 4_000_000_000L);
    public int EffectiveMaxVisualImageBytesPerUnit
        => Math.Clamp(MaxVisualImageBytesPerUnit, 4_096, 64 * 1024 * 1024);
    public int EffectiveMaxRenderedPdfBytes
        => Math.Clamp(MaxRenderedPdfBytes, 4_096, 512 * 1024 * 1024);
    public int EffectiveMaxOfficeRenderSeconds => Math.Clamp(MaxOfficeRenderSeconds, 1, 900);
    public int EffectiveMaxPageRenderSeconds => Math.Clamp(MaxPageRenderSeconds, 1, 300);
    public int EffectiveRenderDpi => Math.Clamp(RenderDpi, 72, 400);
    public int EffectiveTextCanvasWidth => Math.Clamp(TextCanvasWidth, 320, 4_096);
    public int EffectiveTextCanvasHeight => Math.Clamp(TextCanvasHeight, 320, 4_096);
    public int EffectiveMaxConcurrentVisualDocuments => Math.Clamp(MaxConcurrentVisualDocuments, 1, 16);
    public int EffectiveMaxConcurrentVisualPageEmbeddings
        => Math.Clamp(MaxConcurrentVisualPageEmbeddings, 1, 16);
    public int EffectiveMaxVectorsPerVisualUnit => Math.Clamp(MaxVectorsPerVisualUnit, 1, 8_192);
    public int EffectiveMaxLateInteractionDimension => Math.Clamp(MaxLateInteractionDimension, 1, 4_096);
    public int EffectiveMaxMultiVectorBytesPerUnit
        => Math.Clamp(MaxMultiVectorBytesPerUnit, 1_024, 32 * 1024 * 1024);
    public int EffectiveMaxMultiVectorCandidateUnits
        => Math.Clamp(MaxMultiVectorCandidateUnits, 1, 1_000);
    public int EffectiveMaxVisualUnitsPerOwnerExactFallback
        => Math.Clamp(MaxVisualUnitsPerOwnerExactFallback, 1, 1_000_000);
    public int EffectiveVisualUnitCandidates => Math.Clamp(VisualUnitCandidates, 1, 500);
    public int EffectiveVisualCandidateFiles => Math.Clamp(VisualCandidateFiles, 1, 50);
    public double EffectiveVisualRelativeFloor => Math.Clamp(VisualRelativeFloor, 0.0, 1.0);
    public double EffectiveMinimumVisualScore => Math.Clamp(MinimumVisualScore, 1e-6, 1.0);
}

/// The visual profile identities this release knows.
///
/// DOCUMENT-SPECIFIC, sharing the SigLIP2 checkpoint with photos and sharing
/// nothing else. A separate profile key means a separate `AiProfile` row,
/// separate storage and separate status — so a future document-visual model
/// swap cannot reindex the photo library, and a photo profile change cannot
/// silently reinterpret somebody's documents.
public static class DocumentVisualProfiles
{
    public const string DenseSiglip2So400m = "document-visual-siglip2-so400m-patch14-384-v1";

    /// The dimension the dense profile must report. Asserted rather than read
    /// from the model: a checkpoint that returns something else is a different
    /// model wearing this profile's name.
    public const int DenseDimension = 1_152;
}
