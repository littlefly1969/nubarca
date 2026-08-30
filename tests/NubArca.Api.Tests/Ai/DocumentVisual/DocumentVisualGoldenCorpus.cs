using NubArca.Api.Ai.DocumentVisual;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

/// THE VISUAL GOLDEN CORPUS, declared once.
///
/// Two lanes measure visual retrieval and they must measure the SAME thing: the
/// fast suite, with deterministic vectors, proving the plumbing; and the Phase-0
/// lane, with real SigLIP2 and a real late-interaction candidate, proving
/// whether the models earn their cost. Two copies of a benchmark are two
/// benchmarks, and the one that drifts is always the one nobody is watching — so
/// the questions and the documents live here and both lanes build from them.
///
/// The corpus is SYNTHETIC and non-secret on purpose: a benchmark made of real
/// private documents could not be committed, and one written to match its own
/// queries would measure nothing. The questions were written first, in the words
/// somebody would type; the documents were written as documents.
internal static class DocumentVisualGoldenCorpus
{
    /// One document, and the questions its PAGES are expected to resemble.
    ///
    /// `LooksLike` means two different things in the two lanes, deliberately. In
    /// the fast suite it SEEDS a deterministic page vector, so the harness
    /// controls which document looks like which question and the test measures
    /// the plumbing. In the Phase-0 lane it is only documentation: there the
    /// real model looks at the real rendered page and decides for itself, which
    /// is the entire thing being measured.
    internal sealed record Document(
        string Name,
        string Heading,
        string Body,
        IReadOnlyList<string> LooksLike,
        bool OwnedByB = false);

    private static Document Doc(
        string name, string heading, string body, params string[] looksLike)
        => new(name, heading, body, looksLike);

    /// Thirteen cases, covering every category the design calls out. Each is a
    /// different SHAPE of question, not a variation on one.
    internal static IReadOnlyList<DocumentVisualGoldenCase> Cases { get; } = new[]
    {
        // 1. Ordinary prose, where the visual signal should add little and must
        //    not subtract anything.
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

    internal static IReadOnlyList<Document> Documents { get; } = new[]
    {
        Doc("appunti-viaggio.md", "Documenti e prenotazioni",
            "Il biglietto del treno per Lisbona è prenotato per le sette del mattino e "
            + "l'albergo si trova vicino alla stazione centrale, con colazione inclusa."),

        Doc("manutenzione.md", "Manutenzione › Revisione periodica",
            "Il piano prevede la revisione periodica dell'impianto ogni sei mesi, con "
            + "verifica della pressione e pulizia dei filtri, ed è organizzato per stagione.",
            "come è organizzato il piano di manutenzione",
            "ogni quanto va fatta la revisione periodica dell'impianto"),

        // ---- the recovery cases ---------------------------------------------
        //
        // THE SHAPE THE VISUAL SIGNAL EXISTS FOR, and the only shape in which a
        // recovery is possible at all: a document whose text genuinely ANSWERS
        // the question — otherwise the evidence gate refuses it, correctly —
        // written in ordinary words that several other documents use more
        // heavily. Global text ranks it out of the evidence budget; its LAYOUT
        // is what brings it back.
        Doc("tabella-costi.pdf", "Costi › Prospetto trimestrale",
            "Prospetto dei costi per trimestre: Q1 30.200, Q2 33.900, Q3 35.100, "
            + "Q4 38.400, con il totale annuo in fondo alla tabella.",
            "la tabella con i costi per trimestre", "quarterly cost table"),

        Doc("modulo.pdf", "Modulo › Dati richiedente",
            "Il richiedente compila il modulo indicando cognome, nome e recapito nei "
            + "campi previsti, e lo firma in fondo alla pagina prima di consegnarlo.",
            "il modulo da compilare con i campi da firmare"),

        // The crowd. These repeat the recovery questions' vocabulary while
        // answering neither, which is what pushes the two targets past the
        // evidence budget in the text-only pass.
        Doc("email-moduli.md", "Note",
            "Ho ricevuto il modulo da compilare: nei campi da firmare manca la data, "
            + "quindi rimando il modulo compilato con i campi corretti da firmare."),
        Doc("verbale-moduli.md", "Note",
            "Verbale: si discute il modulo, i campi da compilare e le firme da "
            + "raccogliere; ogni modulo va firmato nei campi indicati."),
        Doc("indice-moduli.md", "Note",
            "Indice: modulo, moduli, compilare, campi, firmare, firme, modulistica, "
            + "campi obbligatori, moduli da firmare."),
        Doc("email-costi.md", "Note",
            "Confermate la tabella dei costi per trimestre? La tabella con i costi "
            + "trimestrali va confrontata con la tabella dei costi precedente."),
        Doc("verbale-costi.md", "Note",
            "Verbale: revisione della tabella dei costi, costi per trimestre, "
            + "tabella trimestrale dei costi e costi fuori tabella."),
        Doc("indice-costi.md", "Note",
            "Indice: costi, tabella, trimestre, trimestrale, cost table, quarterly, "
            + "quarterly cost table, tabelle dei costi per trimestre."),

        // ---- documents the text path already handles -------------------------
        Doc("contratto.docx", "Contratto › Clausole numerate",
            "Il contratto elenca le clausole numerate: 1. Oggetto, 2. Durata, "
            + "3. Corrispettivo, 4. Recesso, 5. Foro competente.",
            "il contratto con le clausole numerate"),

        Doc("budget.xlsx", "Budget › Riepilogo annuale",
            "Il foglio di calcolo del budget annuale riporta entrate, uscite e saldo mese "
            + "per mese, da gennaio a dicembre.",
            "il foglio di calcolo del budget annuale"),

        Doc("piano-lancio.pptx", "Lancio › Fasi",
            "La slide del piano di lancio elenca le tre fasi: prototipo, beta e "
            + "disponibilità generale.",
            "la slide del piano di lancio"),

        Doc("vendite.pdf", "Vendite › Andamento",
            "Il grafico dell'andamento delle vendite mostra la crescita da gennaio a "
            + "giugno, con il picco a maggio.",
            "il grafico dell'andamento delle vendite"),

        Doc("ricevuta-scansione.pdf", "Ricevuta",
            "Ricevuta scansionata del pagamento: importo 128,40 del 12 marzo, causale saldo.",
            "la ricevuta scansionata del pagamento"),

        // A DELIBERATE VISUAL DISTRACTOR: it looks like the sales question and
        // says nothing about sales. The evidence gate is what keeps it out.
        Doc("foto-grafici.pdf", "Allegati",
            "Immagini decorative allegate alla presentazione interna.",
            "il grafico dell'andamento delle vendite"),

        Doc("note-configurazione.md", "Variabili di ambiente",
            "La cartella dei dati è indicata da NUBARCA_STORAGE_ROOT e deve puntare a un "
            + "volume dedicato. La porta predefinita è 8080 e il livello di log è info."),

        Doc("note-progetto.md", "Riunioni",
            "La riunione settimanale si tiene il martedì mattina e le decisioni vengono "
            + "registrate nel verbale."),

        Doc("ricette.md", "Cucina",
            "Sbucciare le mele, mescolare farina, uova e zucchero, infornare quaranta minuti."),

        // OWNER B HOLDS THE SAME DOCUMENT UNDER A DIFFERENT NAME.
        //
        // The content is the strongest match in the installation for two of the
        // questions, so a missing owner filter shows up as owner A retrieving
        // it. The NAME differs deliberately: the golden set names owner A's
        // files, so any non-zero score for owner B could only come from owner
        // A's documents leaking the other way.
        new Document("costi-di-b.pdf", "Costi › Prospetto trimestrale",
            "Prospetto dei costi per trimestre: Q1 30.200, Q2 33.900, totale annuo in fondo.",
            new[] { "la tabella con i costi per trimestre", "quarterly cost table" },
            OwnedByB: true),
    };
}
