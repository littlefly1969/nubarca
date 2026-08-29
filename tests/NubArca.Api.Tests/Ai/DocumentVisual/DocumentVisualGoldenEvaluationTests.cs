using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Storage;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// DOES THE VISUAL SIGNAL EARN ITS COST — measured, and reported per category.
//
// The isolation tests prove nothing of anybody else's comes back, which a corpus
// returning nothing satisfies perfectly. This is the other half: a synthetic
// library of one person's documents, thirteen questions of the shapes section 66
// of the specification names, and the same pipeline the Assistant runs, three
// times — text only, dense-visual-expanded, and (when a profile is promoted)
// late-interaction-expanded.
//
// WHAT THIS MEASURES AND WHAT IT DOES NOT. The embedding providers here are
// DETERMINISTIC — they hash their input into a vector — so this is not a
// statement about SigLIP2's semantics. The page vectors are seeded by the
// fixture, which means the harness controls exactly which document "looks like"
// which question, and what is measured is the PLUMBING: that a visually-found
// document reaches the top, that a text-only strength is not lost to it, and
// that the recovered/regressed accounting is honest. Model quality is a
// different question, asked in `DocumentVisualRealOnnxTests` and by
// `documents visual-evaluate` against a real library.
//
// The floors are deliberately LOOSE. They are a regression tripwire, not a
// target: thirteen questions is a small set, and tuning weights until a small
// set scores well moves the number and not the product.
public sealed class DocumentVisualGoldenEvaluationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly DocumentVisualHarness _harness = new();
    private AiProfile _extraction = null!;
    private AiProfile _visualProfile = null!;

    public DocumentVisualGoldenEvaluationTests(ITestOutputHelper output)
    {
        _output = output;
        Seed();
    }

    public void Dispose() => _harness.Dispose();

    // ---- the golden set ------------------------------------------------------

    /// Thirteen cases, covering every category section 66 names. Each one is a
    /// different SHAPE of question, not a variation on one.
    private static IReadOnlyList<DocumentVisualGoldenCase> Cases =>
        new[]
        {
            // 1. Ordinary prose, where the visual signal should add little and
            //    must not subtract anything.
            new DocumentVisualGoldenCase(
                "quando parte il treno per Lisbona", new[] { "appunti-viaggio.md" },
                Note: "prose; text should already win"),

            // 2. A Markdown heading hierarchy.
            new DocumentVisualGoldenCase(
                "come è organizzato il piano di manutenzione", new[] { "manutenzione.md" },
                Visual: true, Note: "heading hierarchy"),

            // 3. A PDF table.
            new DocumentVisualGoldenCase(
                "la tabella con i costi per trimestre", new[] { "tabella-costi.pdf" },
                Visual: true, Note: "PDF table"),

            // 4. A PDF form.
            new DocumentVisualGoldenCase(
                "il modulo da compilare con i campi da firmare", new[] { "modulo.pdf" },
                Visual: true, Note: "PDF form"),

            // 5. A scanned PDF — text recovered by OCR, layout intact.
            new DocumentVisualGoldenCase(
                "la ricevuta scansionata del pagamento", new[] { "ricevuta-scansione.pdf" },
                Visual: true, Note: "scanned page"),

            // 6. A DOCX with structural layout.
            new DocumentVisualGoldenCase(
                "il contratto con le clausole numerate", new[] { "contratto.docx" },
                Visual: true, Note: "DOCX structure"),

            // 7. An XLSX workbook.
            new DocumentVisualGoldenCase(
                "il foglio di calcolo del budget annuale", new[] { "budget.xlsx" },
                Visual: true, Note: "XLSX grid"),

            // 8. A PPTX slide.
            new DocumentVisualGoldenCase(
                "la slide del piano di lancio", new[] { "piano-lancio.pptx" },
                Visual: true, Note: "PPTX slide"),

            // 9. A visually similar distractor exists; the right one must win.
            new DocumentVisualGoldenCase(
                "il grafico dell'andamento delle vendite", new[] { "vendite.pdf" },
                Visual: true, Note: "visual distractor present"),

            // 10. An exact identifier, where TEXT must win and visual must not
            //     displace it.
            new DocumentVisualGoldenCase(
                "NUBARCA_STORAGE_ROOT", new[] { "note-configurazione.md" },
                Note: "exact identifier; lexical must win"),

            // 11. Unanswerable by this corpus.
            new DocumentVisualGoldenCase(
                "quali sono gli orari del museo egizio di torino", Array.Empty<string>(),
                Note: "unanswerable"),

            // 12. An Italian paraphrase sharing little vocabulary.
            new DocumentVisualGoldenCase(
                "ogni quanto va fatta la revisione periodica dell'impianto",
                new[] { "manutenzione.md" }, Note: "Italian paraphrase"),

            // 13. An English paraphrase of an Italian document.
            new DocumentVisualGoldenCase(
                "quarterly cost table", new[] { "tabella-costi.pdf" },
                Visual: true, Note: "English paraphrase"),
        };

    // ---- measurement ----------------------------------------------------------

    [Fact]
    public async Task Text_Plus_Visual_Is_At_Least_As_Good_As_Text_Alone()
    {
        var comparison = await new DocumentVisualEvaluator(Pipeline())
            .CompareAsync(_harness.OwnerA, Cases, maxEvidence: 3);

        Report("text-only", comparison.Baseline);
        Report("visual-expanded", comparison.Candidate);
        _output.WriteLine($"recovered={comparison.Recovered.Count} regressed={comparison.Regressed.Count}");
        foreach (var query in comparison.Recovered) _output.WriteLine($"  + {query}");
        foreach (var query in comparison.Regressed) _output.WriteLine($"  - {query}");

        // THE CLAIM OF THIS SLICE, as a tripwire. Fusion must not LOSE what the
        // text path already found: a drop here means the visual half is
        // displacing correct results rather than adding to them.
        Assert.True(
            comparison.Candidate.RecallAtFive >= comparison.Baseline.RecallAtFive - 0.001,
            $"visual-expanded Recall@5 {comparison.Candidate.RecallAtFive:F3} fell below "
            + $"text-only {comparison.Baseline.RecallAtFive:F3}");

        Assert.True(
            comparison.Candidate.MeanReciprocalRank
                >= comparison.Baseline.MeanReciprocalRank - 0.001,
            $"visual-expanded MRR {comparison.Candidate.MeanReciprocalRank:F3} fell below "
            + $"text-only {comparison.Baseline.MeanReciprocalRank:F3}");

        // AND IT MUST ACTUALLY DO SOMETHING. A visual pass that changes nothing
        // is a visual pass nobody should pay to run.
        Assert.True(
            comparison.Recovered.Count > 0,
            "the visual pass recovered no query at all; it is not contributing");
    }

    [Fact]
    public async Task An_Exact_Identifier_Is_Not_Displaced_By_The_Visual_Pass()
    {
        // The regression this slice is most likely to cause. Vectors are worse
        // at exact identifiers than BM25 is, and a visual pass that promoted a
        // similar-LOOKING page over the file that literally contains the string
        // would be a downgrade dressed as a feature.
        var identifier = Cases.Single(c => c.Query == "NUBARCA_STORAGE_ROOT");
        var evaluator = new DocumentVisualEvaluator(Pipeline());

        var withVisual = await evaluator.EvaluateAsync(
            _harness.OwnerA, new[] { identifier }, useVisual: true, maxEvidence: 3);

        var outcome = Assert.Single(withVisual.Outcomes);
        Assert.Equal(1, outcome.FirstExpectedRank);
    }

    [Fact]
    public async Task An_Unanswerable_Question_Stays_Unanswerable_With_Visual_Retrieval_On()
    {
        // A visually similar page is not permission to improvise. The corpus has
        // documents that LOOK like anything; none of them answers this.
        var unanswerable = Cases.Single(c => !c.Answerable);

        var result = await Pipeline().RetrieveAsync(
            _harness.OwnerA, unanswerable.Query, 3, 8_000, useVisual: true);

        Assert.NotEqual(RagRetrievalOutcome.Strong, result.Outcome);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task Another_Owner_Scores_Nothing_On_This_Set()
    {
        // The same thirteen questions asked by somebody who owns none of these
        // documents. Every metric must be zero — which is also a check that the
        // measurement itself is owner-scoped rather than reading a shared
        // corpus.
        var report = await new DocumentVisualEvaluator(Pipeline())
            .EvaluateAsync(_harness.OwnerB, Cases, useVisual: true, maxEvidence: 3);

        Assert.Equal(0.0, report.RecallAtFive);
        Assert.Equal(0.0, report.MeanReciprocalRank);
        Assert.Equal(0, report.TopThreePassed);
    }

    [Fact]
    public async Task Every_Expected_Document_Actually_Exists()
    {
        // A rename must not silently invalidate half the set: a case expecting a
        // document nobody has scores zero forever and looks like a regression.
        var names = await _harness.Db.FileItems
            .Where(f => f.OwnerUserId == _harness.OwnerA)
            .Select(f => f.Name)
            .ToListAsync();

        foreach (var expected in Cases.SelectMany(c => c.ExpectedDocuments))
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void The_Golden_Set_Covers_Every_Declared_Category()
    {
        // Thirteen cases, and at least one of each shape the specification
        // names. A set that quietly lost its scanned-PDF case would still score
        // well and would stop measuring the thing it exists for.
        Assert.Equal(13, Cases.Count);
        Assert.Single(Cases.Where(c => !c.Answerable));
        Assert.True(Cases.Count(c => c.Visual) >= 7);
        Assert.Contains(Cases, c => c.Note == "exact identifier; lexical must win");
        Assert.Contains(Cases, c => c.Note == "Italian paraphrase");
        Assert.Contains(Cases, c => c.Note == "English paraphrase");
    }

    private void Report(string label, DocumentVisualModeReport report)
    {
        _output.WriteLine(
            $"user-documents {label} [{report.Mode}]: Recall@5 {report.RecallAtFive:F3} "
            + $"MRR {report.MeanReciprocalRank:F3} top-3 {report.TopThreePassed}/{report.Queries} "
            + $"visual-nDCG@5 {report.VisualNdcgAtFive:F3} "
            + $"p50 {report.MedianLatencyMs}ms p95 {report.P95LatencyMs}ms");

        foreach (var outcome in report.Outcomes)
        {
            _output.WriteLine(
                $"  rank={outcome.FirstExpectedRank?.ToString() ?? "-"} "
                + $"\"{outcome.Case.Query}\" → [{string.Join(", ", outcome.TopDocuments.Take(3))}]");
        }
    }

    // ---- the pipeline under measurement ---------------------------------------

    private OwnerDocumentRetrievalPipeline Pipeline()
    {
        var ragOptions = Options.Create(new RagOptions());
        var semantic = new RagSemanticProfileResolver(RagDomainRegistry.Instance, ragOptions);
        var embeddings = new TextEmbeddingResolver(
            _harness.Db,
            new ITextEmbeddingProvider[] { new DeterministicTextEmbeddingProvider() },
            semantic);
        var serializer = new AiVectorSerializer();
        var corpus = new OwnerDocumentCorpusSource(_harness.Db);

        var retriever = new RagRetriever(
            RagDomainRegistry.Instance,
            new RagDatabaseServices(
                new DatabaseRagCorpusSource(_harness.Db),
                new RagVectorRetriever(
                    embeddings,
                    new RagVectorIndexService(_harness.Db, serializer, TimeProvider.System),
                    ragOptions),
                embeddings,
                new RagVectorIndexService(_harness.Db, serializer, TimeProvider.System),
                corpus,
                new OwnerDocumentVectorRetriever(_harness.Db, corpus, embeddings, serializer)),
            new BundledProductHelpCorpusSource(ProductHelpCorpus.Empty),
            new RagLexicalIndexCache(),
            ragOptions,
            semantic,
            NullLogger<RagRetriever>.Instance);

        var visualOptions = Options.Create(new DocumentVisualOptions { Enabled = true });
        var backends = new AiBackendResolver(
            Options.Create(new AiOptions { Enabled = true, Provider = AiProviders.Deterministic }),
            new AiProfileRegistry(_harness.Db, TimeProvider.System),
            new IAiBackend[] { new DeterministicAiBackend() });

        var visual = new OwnerDocumentVisualRetriever(
            _harness.Db,
            new DocumentVisualProfileResolver(
                backends, new AiProfileRegistry(_harness.Db, TimeProvider.System), visualOptions),
            _harness.Renderers,
            new DocumentVisualVectorIndexService(_harness.Db, serializer),
            serializer,
            visualOptions,
            new VisualLateInteractionReranker(
                _harness.Db,
                new AiProfileRegistry(_harness.Db, TimeProvider.System),
                serializer,
                visualOptions,
                NullLogger<VisualLateInteractionReranker>.Instance),
            NullLogger<OwnerDocumentVisualRetriever>.Instance);

        return new OwnerDocumentRetrievalPipeline(retriever, visual);
    }

    // ---- the synthetic corpus ---------------------------------------------------

    private void Seed()
    {
        _extraction = _harness.SeedExtractionProfile();
        _visualProfile = SeedDeterministicVisualProfile();

        // The documents are written as documents; the questions were written
        // first, in the words somebody would type. A corpus written to match its
        // own queries measures nothing.
        //
        // Each entry says whether its PAGES should look like a given question,
        // and the fixture seeds that page vector by embedding the question with
        // the same deterministic function the retriever will use. That is what
        // makes "this document looks like that question" a controlled fact
        // rather than a hope about a checkpoint.
        Add("appunti-viaggio.md", "Documenti e prenotazioni",
            "Il biglietto del treno per Lisbona è prenotato per le sette del mattino e "
            + "l'albergo si trova vicino alla stazione centrale, con colazione inclusa.");

        Add("manutenzione.md", "Manutenzione › Revisione periodica",
            "Il piano prevede la revisione periodica dell'impianto ogni sei mesi, con "
            + "verifica della pressione e pulizia dei filtri, ed è organizzato per stagione.",
            looksLike: new[]
            {
                "come è organizzato il piano di manutenzione",
                "ogni quanto va fatta la revisione periodica dell'impianto",
            });

        // ---- the recovery cases -------------------------------------------------
        //
        // THE SHAPE THE VISUAL SIGNAL EXISTS FOR, and the only shape in which a
        // recovery is possible at all: a document whose text genuinely ANSWERS
        // the question — otherwise the evidence gate refuses it, correctly —
        // written in ordinary words that several other documents use more
        // heavily. Global text ranks it out of the evidence budget; its LAYOUT
        // is what brings it back.
        Add("tabella-costi.pdf", "Costi › Prospetto trimestrale",
            "Prospetto dei costi per trimestre: Q1 30.200, Q2 33.900, Q3 35.100, "
            + "Q4 38.400, con il totale annuo in fondo alla tabella.",
            looksLike: new[] { "la tabella con i costi per trimestre", "quarterly cost table" });

        Add("modulo.pdf", "Modulo › Dati richiedente",
            "Il richiedente compila il modulo indicando cognome, nome e recapito nei "
            + "campi previsti, e lo firma in fondo alla pagina prima di consegnarlo.",
            looksLike: new[] { "il modulo da compilare con i campi da firmare" });

        // The crowd. These repeat the recovery questions' vocabulary while
        // answering neither, which is what pushes the two targets past the
        // evidence budget in the text-only pass.
        foreach (var (name, body) in new[]
                 {
                     ("email-moduli.md",
                      "Ho ricevuto il modulo da compilare: nei campi da firmare manca la data, "
                      + "quindi rimando il modulo compilato con i campi corretti da firmare."),
                     ("verbale-moduli.md",
                      "Verbale: si discute il modulo, i campi da compilare e le firme da "
                      + "raccogliere; ogni modulo va firmato nei campi indicati."),
                     ("indice-moduli.md",
                      "Indice: modulo, moduli, compilare, campi, firmare, firme, modulistica, "
                      + "campi obbligatori, moduli da firmare."),
                     ("email-costi.md",
                      "Confermate la tabella dei costi per trimestre? La tabella con i costi "
                      + "trimestrali va confrontata con la tabella dei costi precedente."),
                     ("verbale-costi.md",
                      "Verbale: revisione della tabella dei costi, costi per trimestre, "
                      + "tabella trimestrale dei costi e costi fuori tabella."),
                     ("indice-costi.md",
                      "Indice: costi, tabella, trimestre, trimestrale, cost table, quarterly, "
                      + "quarterly cost table, tabelle dei costi per trimestre."),
                 })
        {
            Add(name, "Note", body);
        }

        // ---- documents the text path already handles ---------------------------
        Add("contratto.docx", "Contratto › Clausole numerate",
            "Il contratto elenca le clausole numerate: 1. Oggetto, 2. Durata, "
            + "3. Corrispettivo, 4. Recesso, 5. Foro competente.",
            looksLike: new[] { "il contratto con le clausole numerate" });

        Add("budget.xlsx", "Budget › Riepilogo annuale",
            "Il foglio di calcolo del budget annuale riporta entrate, uscite e saldo mese "
            + "per mese, da gennaio a dicembre.",
            looksLike: new[] { "il foglio di calcolo del budget annuale" });

        Add("piano-lancio.pptx", "Lancio › Fasi",
            "La slide del piano di lancio elenca le tre fasi: prototipo, beta e "
            + "disponibilità generale.",
            looksLike: new[] { "la slide del piano di lancio" });

        Add("vendite.pdf", "Vendite › Andamento",
            "Il grafico dell'andamento delle vendite mostra la crescita da gennaio a "
            + "giugno, con il picco a maggio.",
            looksLike: new[] { "il grafico dell'andamento delle vendite" });

        Add("ricevuta-scansione.pdf", "Ricevuta",
            "Ricevuta scansionata del pagamento: importo 128,40 del 12 marzo, causale saldo.",
            looksLike: new[] { "la ricevuta scansionata del pagamento" });

        // A DELIBERATE VISUAL DISTRACTOR: it looks like the sales question and
        // says nothing about sales. The evidence gate is what keeps it out.
        Add("foto-grafici.pdf", "Allegati",
            "Immagini decorative allegate alla presentazione interna.",
            looksLike: new[] { "il grafico dell'andamento delle vendite" });

        Add("note-configurazione.md", "Variabili di ambiente",
            "La cartella dei dati è indicata da NUBARCA_STORAGE_ROOT e deve puntare a un "
            + "volume dedicato. La porta predefinita è 8080 e il livello di log è info.");

        Add("note-progetto.md", "Riunioni",
            "La riunione settimanale si tiene il martedì mattina e le decisioni vengono "
            + "registrate nel verbale.");

        Add("ricette.md", "Cucina",
            "Sbucciare le mele, mescolare farina, uova e zucchero, infornare quaranta minuti.");

        // OWNER B HOLDS THE SAME DOCUMENT UNDER A DIFFERENT NAME.
        //
        // The content and the page vectors are the strongest match in the
        // installation for two of the questions, so a missing owner filter shows
        // up as owner A retrieving it. The NAME differs deliberately: the golden
        // set names owner A's files, so any non-zero score for owner B could
        // only come from owner A's documents leaking the other way — which is
        // what `Another_Owner_Scores_Nothing_On_This_Set` measures.
        AddFor(_harness.OwnerB, "costi-di-b.pdf", "Costi › Prospetto trimestrale",
            "Prospetto dei costi per trimestre: Q1 30.200, Q2 33.900, totale annuo in fondo.",
            looksLike: new[] { "la tabella con i costi per trimestre", "quarterly cost table" });

        _harness.Db.SaveChanges();
    }

    private AiProfile SeedDeterministicVisualProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = "document-visual-deterministic",
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = DocumentVisualProfiles.DenseDimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = DocumentVisualProfiles.DenseSiglip2So400m,
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = DocumentVisualProfiles.DenseDimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.AiModels.Add(model);
        _harness.Db.AiProfiles.Add(profile);
        _harness.Db.SaveChanges();
        return profile;
    }

    private void Add(
        string name, string heading, string body, IReadOnlyList<string>? looksLike = null)
        => AddFor(_harness.OwnerA, name, heading, body, looksLike);

    private void AddFor(
        Guid owner, string name, string heading, string body, IReadOnlyList<string>? looksLike)
    {
        var file = _harness.SeedFile(owner, name);

        var document = new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = owner,
            ProfileId = _extraction.Id,
            SourceBlobObjectId = file.BlobObjectId,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            IsCurrent = true,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            Text = body,
            CharCount = body.Length,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.DocumentTexts.Add(document);
        _harness.Db.DocumentChunks.Add(new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentTextId = document.Id,
            OwnerUserId = owner,
            ProfileId = _extraction.Id,
            Ordinal = 0,
            Heading = heading,
            Text = body,
            CreatedAt = DateTime.UtcNow,
        });
        _harness.Db.SaveChanges();

        if (looksLike is null || looksLike.Count == 0) return;

        // ONE PAGE PER QUESTION IT RESEMBLES, embedded with the same
        // deterministic function the retriever's text tower uses — so the page
        // and the query land at the same point, exactly as a real multimodal
        // model would put a table page near "a table of costs".
        var vectors = looksLike
            .Select(question => new DeterministicAiBackend()
                .EmbedTextAsync(question, _visualProfile).GetAwaiter().GetResult().Vector)
            .ToArray();

        _harness.SeedVisualIndex(
            file, vectors, renderProfileKey: DocumentVisualRenderProfiles.TextCanvas,
            profileOverride: _visualProfile.Id);
    }
}
