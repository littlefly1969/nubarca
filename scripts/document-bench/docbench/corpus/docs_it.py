"""Italian golden documents. Every name, code, amount and person is FICTIONAL.

Codes that look real (partita IVA, IBAN, POD, codice fiscale) are invented and
deliberately fail their check digits where a check digit exists; they are here
because "find the invoice with P.IVA ..." is a real question shape, not because
any of them identifies anyone.
"""

from __future__ import annotations

from .model import C, Cell, Doc, F, H, L, N, P, Page, T

HDR_LOC = "Contratto di locazione ad uso abitativo — Rep. n. 4471/2025"

LEASE = Doc(
    doc_id="it-lease-residential",
    title="Contratto di locazione ad uso abitativo",
    filename="contratto_locazione_via_dei_mille.pdf",
    lang="it",
    category="contract",
    format="pdf",
    header=HDR_LOC,
    footer="Pagina {n} di {total}",
    pages=(
        Page((
            H("CONTRATTO DI LOCAZIONE AD USO ABITATIVO (L. 431/1998, art. 2 comma 1)"),
            P("Tra il sig. Marco Bellandi, nato a Pistoia il 12/04/1971, codice fiscale "
              "BLLMRC71D12G713X, di seguito «Locatore», e la sig.ra Giulia Ferraris, nata a "
              "Novara il 03/09/1990, codice fiscale FRRGLI90P43F952K, di seguito «Conduttrice», "
              "si conviene e si stipula quanto segue."),
            H("Art. 1 — Oggetto", 2),
            P("Il Locatore concede in locazione alla Conduttrice l'unità immobiliare sita in "
              "Firenze, via dei Mille 27, piano terzo, interno 9, composta da due vani oltre "
              "servizi e cantina, identificata al Catasto Fabbricati al foglio 88, particella "
              "412, subalterno 17, categoria A/2, classe 4, rendita catastale euro 1.084,56."),
            H("Art. 2 — Durata", 2),
            P("La locazione ha durata di quattro anni, con decorrenza dal 1° marzo 2025 e "
              "scadenza al 28 febbraio 2029, e si rinnova automaticamente per ulteriori quattro "
              "anni salvo disdetta da comunicarsi con lettera raccomandata almeno sei mesi prima "
              "della scadenza¹."),
            H("Art. 3 — Canone", 2),
            P("Il canone annuo è convenuto in euro 11.400,00 (undicimilaquattrocento/00), da "
              "corrispondersi in dodici rate mensili anticipate di euro 950,00 ciascuna entro il "
              "giorno 5 di ogni mese mediante bonifico bancario sul conto IBAN "
              "IT60 X054 2811 1010 0000 0123 456 intestato al Locatore."),
            P("Il canone sarà aggiornato annualmente nella misura del 75% della variazione "
              "assoluta dell'indice ISTAT dei prezzi al consumo per le famiglie di operai e "
              "impiegati (FOI) verificatasi nell'anno precedente."),
            N("¹ La disdetta del Locatore alla prima scadenza è ammessa solo per i motivi "
              "previsti dall'art. 3 della L. 431/1998."),
        )),
        Page((
            H("Art. 4 — Deposito cauzionale", 2),
            P("A garanzia delle obbligazioni assunte la Conduttrice versa al Locatore, che con "
              "la firma del presente contratto ne rilascia quietanza, la somma di euro 2.850,00 "
              "pari a tre mensilità del canone, non imputabile in conto canoni e produttiva di "
              "interessi legali che saranno corrisposti al termine di ogni anno di locazione."),
            H("Art. 5 — Oneri accessori", 2),
            P("Sono interamente a carico della Conduttrice le spese relative al servizio di "
              "pulizia delle parti comuni, al funzionamento e all'ordinaria manutenzione "
              "dell'ascensore, alla fornitura dell'acqua, dell'energia elettrica e del "
              "riscaldamento, secondo la tabella oneri accessori allegata (Allegato B)."),
            T("lease-oneri", (
                ("Voce di spesa", "Ripartizione", "Acconto mensile"),
                ("Pulizia scale e androne", "Conduttrice 100%", "€ 18,00"),
                ("Ascensore — manutenzione ordinaria", "Conduttrice 100%", "€ 12,50"),
                ("Ascensore — sostituzione funi", "Locatore 100%", "—"),
                ("Riscaldamento centralizzato", "Conduttrice 100%", "€ 64,00"),
                ("Amministrazione condominiale", "Locatore 50% / Conduttrice 50%", "€ 9,00"),
            ), caption="Tabella 1 — Ripartizione degli oneri accessori"),
            H("Art. 6 — Manutenzione e migliorie", 2),
            P("La Conduttrice non potrà apportare alcuna modifica, innovazione o miglioria ai "
              "locali senza il preventivo consenso scritto del Locatore. Le riparazioni di "
              "piccola manutenzione di cui all'art. 1609 c.c. sono a carico della Conduttrice; "
              "restano a carico del Locatore le riparazioni straordinarie, inclusa la "
              "sostituzione della caldaia qualora non più riparabile."),
        )),
        Page((
            H("Art. 7 — Recesso della Conduttrice", 2),
            P("È facoltà della Conduttrice recedere anticipatamente dal contratto per gravi "
              "motivi, dandone avviso al Locatore mediante lettera raccomandata con preavviso "
              "di almeno tre mesi rispetto alla data in cui il recesso deve avere esecuzione."),
            H("Art. 8 — Animali domestici", 2),
            P("È consentita la detenzione di animali domestici di piccola taglia, purché non "
              "arrechino disturbo agli altri condomini; eventuali danni ai pavimenti in parquet "
              "saranno addebitati alla Conduttrice in sede di riconsegna."),
            H("Art. 9 — Cedolare secca", 2),
            P("Il Locatore dichiara di optare per il regime della cedolare secca di cui "
              "all'art. 3 del D.Lgs. 23/2011 e rinuncia, per tutta la durata dell'opzione, "
              "alla facoltà di chiedere l'aggiornamento del canone a qualsiasi titolo, anche "
              "in deroga a quanto previsto dall'art. 3 del presente contratto."),
            H("Art. 10 — Registrazione", 2),
            P("Il contratto è registrato a cura del Locatore presso l'Agenzia delle Entrate, "
              "Ufficio Territoriale di Firenze, entro trenta giorni dalla stipula. L'imposta "
              "di bollo è a carico delle parti in misura uguale."),
        )),
        Page((
            H("Art. 11 — Elezione di domicilio e foro competente", 2),
            P("Per ogni controversia relativa al presente contratto è competente in via "
              "esclusiva il Foro di Firenze. Le parti eleggono domicilio presso gli indirizzi "
              "indicati in epigrafe."),
            P("Letto, approvato e sottoscritto in Firenze, il 14 febbraio 2025."),
            P("Il Locatore: Marco Bellandi            La Conduttrice: Giulia Ferraris"),
            P("Ai sensi degli artt. 1341 e 1342 c.c. le parti approvano specificamente le "
              "clausole di cui agli artt. 4, 6, 7, 9 e 11."),
            T("lease-consegna", (
                ("Lettura contatori alla consegna", Cell("Valore", colspan=2)),
                ("", "Matricola", "Lettura"),
                ("Energia elettrica", "EL-55120934", "18.442 kWh"),
                ("Gas", "GS-0071882", "6.105 Smc"),
                ("Acqua fredda", "AQ-993410", "1.207 m³"),
            ), header_rows=2, caption="Allegato A — Verbale di consegna dell'immobile"),
        )),
    ),
)

LEASE_COMMERCIAL = Doc(
    doc_id="it-lease-commercial",
    title="Contratto di locazione ad uso commerciale",
    filename="locazione_negozio_scansione.pdf",
    lang="it",
    category="contract",
    format="scan",
    header="Contratto di locazione ad uso diverso dall'abitativo — Rep. n. 4502/2025",
    footer="Pagina {n} di {total}",
    distractor_of="it-lease-residential",
    pages=(
        Page((
            H("CONTRATTO DI LOCAZIONE AD USO COMMERCIALE (L. 392/1978, artt. 27 e ss.)"),
            P("Tra la società Immobiliare Arno S.r.l., con sede in Firenze, via Pisana 140, "
              "P.IVA 05512870481, in persona dell'amministratore unico sig. Marco Bellandi, di "
              "seguito «Locatrice», e la ditta individuale Forno Ferraris di Luca Ferraris, "
              "di seguito «Conduttore», si conviene quanto segue."),
            H("Art. 1 — Oggetto", 2),
            P("La Locatrice concede in locazione al Conduttore il locale commerciale sito in "
              "Firenze, via dei Mille 27, piano terra, con vetrina su strada, di superficie "
              "pari a 74 mq, da adibire esclusivamente a panetteria e vendita di prodotti da "
              "forno."),
            H("Art. 2 — Durata", 2),
            P("La locazione ha durata di sei anni, con decorrenza dal 1° aprile 2025 e "
              "scadenza al 31 marzo 2031, rinnovabile di ulteriori sei anni ai sensi dell'art. "
              "28 della L. 392/1978."),
            H("Art. 3 — Canone", 2),
            P("Il canone annuo è convenuto in euro 21.600,00, da corrispondersi in rate "
              "trimestrali anticipate di euro 5.400,00 ciascuna, oltre IVA se dovuta."),
        )),
        Page((
            H("Art. 4 — Deposito cauzionale", 2),
            P("A garanzia delle obbligazioni assunte il Conduttore versa la somma di euro "
              "5.400,00 pari a una rata trimestrale, restituibile al termine della locazione "
              "previa verifica dello stato dei locali."),
            H("Art. 5 — Indennità di avviamento", 2),
            P("Alla cessazione del rapporto, per causa non imputabile al Conduttore, spetta "
              "a quest'ultimo l'indennità per la perdita dell'avviamento commerciale pari a "
              "diciotto mensilità dell'ultimo canone corrisposto, ai sensi dell'art. 34 della "
              "L. 392/1978."),
            H("Art. 6 — Recesso del Conduttore", 2),
            P("Il Conduttore può recedere in qualsiasi momento dal contratto per gravi motivi "
              "con preavviso di almeno sei mesi da comunicarsi a mezzo PEC."),
            H("Art. 7 — Foro competente", 2),
            P("Per ogni controversia è competente in via esclusiva il Foro di Firenze."),
            P("Firenze, 20 marzo 2025."),
        )),
    ),
)

INVOICE_187 = Doc(
    doc_id="it-invoice-0187",
    title="Fattura n. 2025/0187",
    filename="fattura_2025_0187_idraulica_ferretti.pdf",
    lang="it",
    category="invoice",
    format="pdf",
    pages=(
        Page((
            H("IDRAULICA FERRETTI S.n.c. di Ferretti Paolo & C."),
            P("Via Bolognese 212 — 50139 Firenze — P.IVA 06120440487 — Tel. 055 0000 412"),
            H("FATTURA N. 2025/0187 del 18/06/2025", 2),
            P("Spett.le Giulia Ferraris, via dei Mille 27, 50131 Firenze — C.F. FRRGLI90P43F952K"),
            P("Riferimento intervento: rapporto di lavoro RL-7731 del 16/06/2025 — sostituzione "
              "gruppo di sicurezza e pulizia scambiatore caldaia Termoviva CX 24."),
            T("inv187-items", (
                ("Codice", "Descrizione", "Q.tà", "Prezzo unit.", "Importo"),
                ("TV-GS24", "Gruppo di sicurezza 3 bar per Termoviva CX", "1", "€ 86,00", "€ 86,00"),
                ("TV-SC24", "Kit guarnizioni scambiatore primario", "1", "€ 34,50", "€ 34,50"),
                ("MAN-ORA", "Manodopera tecnico specializzato (ore)", "2,5", "€ 42,00", "€ 105,00"),
                ("USC-FI", "Diritto di uscita zona Firenze città", "1", "€ 35,00", "€ 35,00"),
            ), widths=(0.14, 0.44, 0.08, 0.16, 0.18)),
            T("inv187-totals", (
                ("Imponibile", "IVA 22%", "Totale fattura"),
                ("€ 260,50", "€ 57,31", "€ 317,81"),
            )),
            P("Modalità di pagamento: bonifico bancario a 30 giorni data fattura su IBAN "
              "IT21 Q030 6902 8871 0000 0004 410. Scadenza: 18/07/2025."),
            P("Documento emesso in formato elettronico e trasmesso tramite SdI; la presente "
              "copia è priva di valore fiscale. Garanzia sui ricambi: 24 mesi."),
        )),
    ),
)

INVOICE_193 = Doc(
    doc_id="it-invoice-0193",
    title="Fattura n. 2025/0193",
    filename="IMG_20250704_fattura_ferretti.jpg",
    lang="it",
    category="invoice",
    format="photo",
    distractor_of="it-invoice-0187",
    pages=(
        Page((
            H("IDRAULICA FERRETTI S.n.c. di Ferretti Paolo & C."),
            P("Via Bolognese 212 — 50139 Firenze — P.IVA 06120440487 — Tel. 055 0000 412"),
            H("FATTURA N. 2025/0193 del 04/07/2025", 2),
            P("Spett.le Condominio Via dei Mille 27, 50131 Firenze — C.F. 94077120486"),
            P("Riferimento intervento: rapporto di lavoro RL-7802 del 01/07/2025 — riparazione "
              "perdita colonna montante acqua fredda nel vano scala."),
            T("inv193-items", (
                ("Codice", "Descrizione", "Q.tà", "Prezzo unit.", "Importo"),
                ("TB-MT32", "Tubo multistrato 32 mm (metri)", "6", "€ 9,80", "€ 58,80"),
                ("RC-PR32", "Raccordi a pressare 32 mm", "4", "€ 11,20", "€ 44,80"),
                ("MAN-ORA", "Manodopera tecnico specializzato (ore)", "4", "€ 42,00", "€ 168,00"),
            ), widths=(0.14, 0.44, 0.08, 0.16, 0.18)),
            T("inv193-totals", (
                ("Imponibile", "IVA 10%", "Totale fattura"),
                ("€ 271,60", "€ 27,16", "€ 298,76"),
            )),
            P("Modalità di pagamento: bonifico bancario vista fattura. IVA agevolata 10% per "
              "intervento di manutenzione su edificio residenziale."),
        )),
    ),
)

STATEMENT_HDR = "Banca Valdarno — Estratto conto n. 2/2025 — Conto 000041207733"

BANK_STATEMENT = Doc(
    doc_id="it-bank-statement",
    title="Estratto conto corrente secondo trimestre 2025",
    filename="estratto_conto_Q2_2025.pdf",
    lang="it",
    category="statement",
    format="scan",
    header=STATEMENT_HDR,
    footer="Pag. {n}/{total}",
    pages=(
        Page((
            H("ESTRATTO CONTO CORRENTE — PERIODO 01/04/2025 – 30/06/2025"),
            P("Intestatario: Giulia Ferraris — Filiale di Firenze Campo di Marte — "
              "IBAN IT48 K086 7302 8000 0004 1207 733"),
            T("stmt-movimenti", (
                ("Data", "Valuta", "Descrizione", "Dare", "Avere"),
                ("01/04", "01/04", "Saldo iniziale", "", "4.218,37"),
                ("03/04", "03/04", "Bonifico a Bellandi Marco — canone aprile", "950,00", ""),
                ("07/04", "07/04", "Accredito stipendio Arcadia Logistics", "", "2.640,00"),
                ("11/04", "11/04", "Addebito SDD Lumen Energia bolletta 04/25", "88,40", ""),
                ("15/04", "15/04", "Pagamento POS Esselunga Firenze", "64,12", ""),
                ("22/04", "22/04", "Prelievo ATM sportello Campo di Marte", "200,00", ""),
                ("05/05", "05/05", "Bonifico a Bellandi Marco — canone maggio", "950,00", ""),
                ("07/05", "07/05", "Accredito stipendio Arcadia Logistics", "", "2.640,00"),
                ("12/05", "12/05", "Addebito SDD Lumen Energia bolletta 05/25", "76,15", ""),
                ("19/05", "19/05", "Bonifico istantaneo da Ferraris Luca", "", "300,00"),
            ), widths=(0.09, 0.09, 0.52, 0.15, 0.15)),
        )),
        Page((
            T("stmt-movimenti", (
                ("Data", "Valuta", "Descrizione", "Dare", "Avere"),
                ("27/05", "27/05", "Commissione bonifico istantaneo", "1,50", ""),
                ("04/06", "04/06", "Bonifico a Bellandi Marco — canone giugno", "950,00", ""),
                ("06/06", "06/06", "Accredito stipendio Arcadia Logistics", "", "2.640,00"),
                ("10/06", "10/06", "Addebito SDD Lumen Energia bolletta 06/25", "71,03", ""),
                ("18/06", "18/06", "Pagamento F24 imposta di registro", "57,00", ""),
                ("25/06", "25/06", "Pagamento POS Farmacia Comunale 12", "23,80", ""),
                ("30/06", "30/06", "Competenze di chiusura trimestre", "6,90", ""),
            ), widths=(0.09, 0.09, 0.52, 0.15, 0.15), caption="(segue dalla pagina precedente)"),
            T("stmt-riepilogo", (
                ("Saldo iniziale", "Totale dare", "Totale avere", "Saldo finale"),
                ("4.218,37", "4.433,90", "8.220,00", "8.004,47"),
            )),
            P("Tasso creditore annuo nominale 0,05%. Le competenze di chiusura comprendono "
              "l'imposta di bollo di euro 8,55 rapportata al periodo e il canone mensile."),
            N("Eventuali contestazioni devono pervenire entro 60 giorni dalla data di ricezione "
              "del presente estratto conto; trascorso tale termine l'estratto si intende "
              "approvato."),
        )),
    ),
)

UTILITY_BILL = Doc(
    doc_id="it-utility-bill",
    title="Bolletta energia elettrica giugno 2025",
    filename="bolletta_lumen_energia_2025_06.pdf",
    lang="it",
    category="utility",
    format="pdf",
    header="Lumen Energia S.p.A. — Bolletta n. 25-0617733 — Cliente 7720115",
    footer="Pagina {n} di {total}",
    pages=(
        Page((
            H("BOLLETTA LUCE — MERCATO LIBERO — Offerta «Lumen Casa Fissa 24»"),
            P("Intestatario: Giulia Ferraris — Indirizzo di fornitura: via dei Mille 27, "
              "50131 Firenze (FI)"),
            P("Codice POD: IT001E71240093 — Potenza impegnata 3,0 kW — Tensione 230 V — "
              "Periodo di fatturazione: 01/05/2025 – 31/05/2025"),
            T("bill-sintesi", (
                ("Sintesi degli importi", "Euro"),
                ("Spesa per la materia energia", "41,18"),
                ("Spesa per il trasporto e la gestione del contatore", "17,06"),
                ("Spesa per oneri di sistema", "6,92"),
                ("Imposte (accise e IVA 10%)", "11,00"),
                ("Totale da pagare", "76,16"),
            )),
            P("Scadenza: 12/06/2025. Pagamento con addebito diretto SEPA sul conto "
              "IT48 K086 7302 8000 0004 1207 733."),
            T("bill-fasce", (
                ("Consumi rilevati per fascia", Cell("Maggio 2025", colspan=2)),
                ("Fascia", "kWh", "Quota"),
                ("F1 (lun-ven 8-19)", "61", "37%"),
                ("F2 (lun-ven 7-8 e 19-23, sab 7-23)", "52", "32%"),
                ("F3 (notte, domenica e festivi)", "51", "31%"),
                ("Totale", "164", "100%"),
            ), header_rows=2),
        )),
        Page((
            H("Andamento dei consumi negli ultimi 12 mesi"),
            C("bill-chart-12m", "bar", "Consumi mensili (kWh)",
              ("giu", "lug", "ago", "set", "ott", "nov", "dic", "gen", "feb", "mar", "apr", "mag"),
              (("kWh", (171, 236, 254, 188, 152, 167, 205, 212, 190, 176, 158, 164)),),
              caption="Figura 1 — Consumi mensili fatturati, giugno 2024 – maggio 2025"),
            P("Il consumo annuo complessivo degli ultimi dodici mesi è stato di 2.273 kWh. Il "
              "contatore è telegestito: le letture sono rilevate automaticamente dal "
              "distributore e-distribuzione."),
            P("Per segnalazioni guasti contattare il numero verde del distributore indicato "
              "sul contatore. Bonus sociale: per verificare i requisiti consultare il sito "
              "dell'ARERA o rivolgersi al proprio CAF."),
        )),
    ),
)

MANUAL_HDR = "Termoviva CX 24 — Manuale di installazione, uso e manutenzione — Rev. 03"

BOILER_MANUAL = Doc(
    doc_id="it-boiler-manual",
    title="Termoviva CX 24 — Manuale di installazione, uso e manutenzione",
    filename="manuale_caldaia_termoviva_cx24.pdf",
    lang="it",
    category="manual",
    format="pdf",
    header=MANUAL_HDR,
    footer="{n}",
    pages=(
        Page((
            H("1. Avvertenze generali"),
            P("La caldaia Termoviva CX 24 è un generatore a condensazione murale per "
              "riscaldamento e produzione istantanea di acqua calda sanitaria, alimentato a "
              "gas metano (G20) o GPL (G31) previa trasformazione con il kit KT-31."),
            P("L'installazione, la prima accensione e la manutenzione devono essere eseguite "
              "esclusivamente da personale abilitato ai sensi del D.M. 37/2008. Prima di "
              "qualsiasi intervento togliere l'alimentazione elettrica e chiudere il rubinetto "
              "del gas."),
            H("2. Dati tecnici"),
            T("boiler-specs", (
                ("Parametro", Cell("Valore", colspan=2), "Unità"),
                ("", "Metano G20", "GPL G31", ""),
                ("Portata termica nominale", "24,0", "24,0", "kW"),
                ("Potenza utile 80/60 °C", "23,4", "23,3", "kW"),
                ("Potenza utile 50/30 °C", "25,6", "25,4", "kW"),
                ("Rendimento stagionale ηs", Cell("93 %", colspan=2), "%"),
                ("Pressione max circuito riscaldamento", Cell("3", colspan=2), "bar"),
                ("Classe NOx", Cell("6", colspan=2), "—"),
            ), header_rows=2, caption="Tabella 2.1 — Caratteristiche tecniche principali"),
        )),
        Page((
            H("3. Uso"),
            H("3.1 Pressione dell'impianto", 2),
            P("A impianto freddo la pressione letta sul display deve essere compresa tra 1,0 e "
              "1,5 bar. Se la pressione scende sotto 0,6 bar la caldaia si blocca e visualizza "
              "il codice E10: aprire il rubinetto di carico posto sotto la caldaia fino a "
              "riportare la pressione a 1,2 bar, quindi richiuderlo."),
            H("3.2 Funzione antigelo", 2),
            P("La caldaia è dotata di protezione antigelo che si attiva quando la temperatura "
              "dell'acqua di mandata scende sotto 5 °C, purché la caldaia sia alimentata "
              "elettricamente e il gas sia aperto."),
            H("3.3 Programmazione oraria", 2),
            L((
                "Premere il tasto MENU per 3 secondi fino a visualizzare P01.",
                "Ruotare la manopola per scegliere il giorno, confermare con OK.",
                "Impostare fino a tre fasce di accensione giornaliere.",
                "Uscire dal menu premendo ESC o attendere 30 secondi.",
            )),
            C("boiler-diagram", "diagram", "Schema idraulico semplificato",
              ("Scambiatore", "Pompa", "Valvola 3 vie", "Vaso espansione", "Mandata", "Ritorno"),
              (),
              caption="Figura 3 — Schema idraulico semplificato del circuito primario"),
        )),
        Page((
            H("4. Codici di errore"),
            P("In caso di anomalia il display visualizza un codice di errore lampeggiante. "
              "Gli errori di tipo «blocco» si resettano con il tasto RESET; gli errori di tipo "
              "«arresto» scompaiono automaticamente al cessare della causa."),
            T("boiler-errors", (
                ("Codice", "Tipo", "Descrizione", "Azione consigliata"),
                ("E01", "Blocco", "Mancata accensione del bruciatore",
                 "Verificare apertura gas; premere RESET"),
                ("E02", "Blocco", "Intervento termostato di sicurezza",
                 "Premere RESET; se persiste chiamare l'assistenza"),
                ("E03", "Arresto", "Intervento pressostato fumi",
                 "Controllare lo scarico fumi e il terminale"),
                ("E05", "Arresto", "Sonda di mandata guasta", "Chiamare l'assistenza"),
                ("E06", "Arresto", "Sonda sanitario guasta", "Chiamare l'assistenza"),
                ("E10", "Arresto", "Pressione impianto insufficiente",
                 "Ripristinare la pressione a 1,2 bar"),
                ("E12", "Blocco", "Ostruzione scarico condensa",
                 "Pulire il sifone di scarico condensa"),
                ("E25", "Arresto", "Circolazione insufficiente (pompa bloccata)",
                 "Sbloccare la pompa; chiamare l'assistenza"),
            ), widths=(0.1, 0.12, 0.38, 0.4), caption="Tabella 4.1 — Elenco codici di errore"),
        )),
        Page((
            H("5. Manutenzione"),
            P("Per garantire la sicurezza e l'efficienza dell'apparecchio è obbligatorio far "
              "eseguire un controllo annuale da un tecnico abilitato e la verifica "
              "dell'efficienza energetica (prova fumi) con la periodicità prevista dalla "
              "normativa regionale, di norma ogni due anni per potenze inferiori a 100 kW."),
            T("boiler-maint", (
                ("Operazione", "Periodicità"),
                ("Pulizia scambiatore primario lato fumi", "Annuale"),
                ("Pulizia sifone condensa", "Annuale"),
                ("Controllo precarica vaso di espansione (1 bar)", "Annuale"),
                ("Sostituzione elettrodo di accensione", "Ogni 3 anni o se usurato"),
                ("Analisi di combustione", "Ogni 2 anni"),
            ), caption="Tabella 5.1 — Piano di manutenzione"),
            P("Il libretto di impianto deve essere aggiornato a ogni intervento. La garanzia "
              "convenzionale di 5 anni sullo scambiatore è valida solo se la manutenzione "
              "annuale risulta regolarmente registrata."),
        )),
        Page((
            H("6. Smaltimento"),
            P("Al termine della vita utile l'apparecchio non deve essere smaltito come rifiuto "
              "urbano ma conferito a un centro di raccolta RAEE autorizzato."),
            H("7. Dichiarazione di conformità"),
            P("Il costruttore Termoviva Italia S.r.l., Brescia, dichiara che la caldaia "
              "Termoviva CX 24 è conforme al Regolamento (UE) 2016/426 sugli apparecchi a gas "
              "e alla Direttiva 2009/125/CE (Ecodesign)."),
            P("Numero di identificazione dell'organismo notificato: 0051. Certificato di esame "
              "UE del tipo n. 0051CU4471."),
        )),
    ),
)

LAB_REPORT = Doc(
    doc_id="it-lab-report",
    title="Referto esami di laboratorio (dati fittizi)",
    filename="referto_laboratorio_scan.pdf",
    lang="it",
    category="medical",
    format="scan",
    header="Laboratorio Analisi San Gallo — Referto n. LAB-25-118204 — DATI FITTIZI",
    footer="Pagina {n} di {total}",
    pages=(
        Page((
            H("REFERTO DI LABORATORIO — DOCUMENTO DI ESEMPIO CON DATI FITTIZI"),
            P("Paziente: Ferraris Giulia — Data prelievo: 09/05/2025 ore 07:42 — Medico "
              "richiedente: dott. Enrico Valli"),
            T("lab-ematologia", (
                (Cell("EMATOLOGIA", colspan=4),),
                ("Esame", "Risultato", "Unità", "Valori di riferimento"),
                ("Emoglobina", "12,1", "g/dL", "12,0 – 15,5"),
                ("Ematocrito", "36,8", "%", "36,0 – 46,0"),
                ("Globuli bianchi", "6,4", "10^3/µL", "4,0 – 10,0"),
                ("Piastrine", "251", "10^3/µL", "150 – 400"),
                ("Ferritina", "* 11", "ng/mL", "15 – 150"),
            ), header_rows=2),
            T("lab-chimica", (
                (Cell("CHIMICA CLINICA", colspan=4),),
                ("Esame", "Risultato", "Unità", "Valori di riferimento"),
                ("Glicemia a digiuno", "88", "mg/dL", "70 – 99"),
                ("Colesterolo totale", "* 214", "mg/dL", "< 200"),
                ("Colesterolo HDL", "62", "mg/dL", "> 50"),
                ("Colesterolo LDL (calcolato)", "* 131", "mg/dL", "< 115"),
                ("Trigliceridi", "104", "mg/dL", "< 150"),
                ("TSH reflex", "2,31", "µUI/mL", "0,27 – 4,20"),
            ), header_rows=2),
            P("I valori contrassegnati con asterisco (*) sono al di fuori dell'intervallo di "
              "riferimento."),
        )),
        Page((
            H("NOTE INTERPRETATIVE"),
            P("Il quadro evidenzia una ferritina ridotta in presenza di emoglobina ai limiti "
              "inferiori della norma, compatibile con una carenza marziale iniziale senza "
              "anemia conclamata. Si suggerisce valutazione clinica e controllo a tre mesi."),
            P("Il colesterolo LDL è calcolato con la formula di Friedewald, valida per "
              "trigliceridi inferiori a 400 mg/dL:"),
            F("lab-friedewald", r"LDL = CT - HDL - \frac{TG}{5}", "LDL = CT − HDL − TG/5"),
            P("Validato da: dott.ssa Carla Morandi, biologa — 09/05/2025 ore 14:10. Il referto "
              "firmato digitalmente è disponibile sul portale con il codice di ritiro "
              "R7-CX44-PQ."),
        )),
    ),
)

CONDO_MINUTES = Doc(
    doc_id="it-condo-minutes",
    title="Verbale di assemblea condominiale straordinaria",
    filename="verbale_assemblea_condominio_2025-05-21.docx",
    lang="it",
    category="minutes",
    format="docx",
    pages=(
        Page((
            H("VERBALE DI ASSEMBLEA STRAORDINARIA — CONDOMINIO VIA DEI MILLE 27, FIRENZE"),
            P("Il giorno 21 maggio 2025 alle ore 18:30, presso la sala riunioni della "
              "parrocchia di San Gervasio, si è riunita in seconda convocazione l'assemblea "
              "straordinaria dei condomini. Presiede l'avv. Silvia Landucci; funge da "
              "segretario l'amministratore rag. Pietro Gonnelli."),
            P("Sono presenti, in proprio o per delega, 14 condomini su 22, rappresentanti "
              "684,50 millesimi di proprietà. L'assemblea è regolarmente costituita."),
            H("Punto 1 — Rifacimento della facciata", 2),
            P("L'assemblea esamina i tre preventivi pervenuti per il rifacimento della "
              "facciata principale e dei frontalini dei balconi."),
            T("condo-preventivi", (
                ("Impresa", "Importo lavori (IVA esclusa)", "Durata cantiere"),
                ("Edil Rifredi S.r.l.", "€ 48.300,00", "75 giorni"),
                ("Costruzioni Mugello S.p.A.", "€ 52.900,00", "60 giorni"),
                ("F.lli Baldini Restauri", "€ 45.700,00", "110 giorni"),
            )),
            P("Dopo ampia discussione l'assemblea approva il preventivo di Edil Rifredi S.r.l. "
              "con 11 voti favorevoli pari a 561,20 millesimi, 2 contrari e 1 astenuto. Si "
              "delibera la costituzione di un fondo speciale di pari importo, da versare in "
              "quattro rate trimestrali a partire dal 1° luglio 2025."),
        )),
        Page((
            H("Punto 2 — Bonus fiscali", 2),
            P("L'amministratore illustra che per i lavori sulle parti comuni è possibile "
              "fruire della detrazione per il recupero del patrimonio edilizio nella misura "
              "vigente al momento del pagamento; ciascun condomino riceverà la certificazione "
              "della propria quota entro il mese di febbraio dell'anno successivo."),
            H("Punto 3 — Sostituzione dell'ascensore", 2),
            P("Il punto è rinviato a una successiva assemblea in attesa della relazione "
              "tecnica dell'ing. Davide Marchi sullo stato delle funi e del quadro di "
              "manovra."),
            H("Punto 4 — Varie ed eventuali", 2),
            P("La condomina Giulia Ferraris segnala la perdita dalla colonna montante "
              "dell'acqua fredda nel vano scala; l'amministratore comunica che l'intervento è "
              "già stato commissionato all'impresa Idraulica Ferretti."),
            P("Non essendovi altro da deliberare, la seduta è tolta alle ore 20:15."),
            P("Il Presidente: avv. Silvia Landucci — Il Segretario: rag. Pietro Gonnelli"),
            N("Allegati: foglio presenze con deleghe; preventivi delle tre imprese; "
              "prospetto di ripartizione millesimale della spesa."),
        )),
    ),
)

BOARD_MINUTES = Doc(
    doc_id="it-board-minutes",
    title="Verbale del consiglio di amministrazione Arcadia Logistics",
    filename="verbale_cda_arcadia_2025-03-12.pdf",
    lang="it",
    category="minutes",
    format="pdf",
    header="Arcadia Logistics S.r.l. — Libro delle adunanze e delle deliberazioni del CdA",
    footer="Foglio {n}",
    distractor_of="it-condo-minutes",
    pages=(
        Page((
            H("VERBALE DELLA RIUNIONE DEL CONSIGLIO DI AMMINISTRAZIONE DEL 12 MARZO 2025"),
            P("Il giorno 12 marzo 2025 alle ore 10:00, presso la sede legale di Arcadia "
              "Logistics S.r.l. in Prato, via del Purgatorio 88, si è riunito il Consiglio di "
              "Amministrazione. Presiede il dott. Alessandro Rinaldi; sono presenti i "
              "consiglieri Chiara Bonini e Tommaso Gualtieri. Assiste il sindaco unico dott. "
              "Federico Nesti."),
            H("1. Approvazione del progetto di bilancio 2024", 2),
            P("Il Presidente illustra il progetto di bilancio dell'esercizio 2024, che chiude "
              "con ricavi delle vendite pari a euro 18,4 milioni e un utile netto di euro "
              "1.212.000. Il Consiglio approva all'unanimità e convoca l'assemblea dei soci "
              "per il giorno 28 aprile 2025."),
            H("2. Contratto con Northwind Data Ltd", 2),
            P("Il consigliere Gualtieri riferisce sulla negoziazione del contratto di servizi "
              "cloud con Northwind Data Ltd. Il Consiglio autorizza la sottoscrizione per un "
              "importo massimo di euro 96.000 annui e una durata di tre anni."),
            H("3. Nomina del responsabile della protezione dei dati", 2),
            P("Il Consiglio nomina quale responsabile della protezione dei dati (DPO) l'avv. "
              "Marta Sileoni, con decorrenza dal 1° aprile 2025."),
            P("La riunione termina alle ore 12:20."),
        )),
        Page((
            H("Allegato 1 — Sintesi dei dati di bilancio"),
            T("board-bilancio", (
                ("Voce (migliaia di euro)", "2023", "2024", "Var. %"),
                ("Ricavi delle vendite", "16.950", "18.402", "+8,6%"),
                ("EBITDA", "2.410", "2.785", "+15,6%"),
                ("Utile netto", "1.034", "1.212", "+17,2%"),
                ("Posizione finanziaria netta", "-3.880", "-3.120", "—"),
                ("Dipendenti (unità)", "142", "151", "+6,3%"),
            )),
            P("Il Presidente: dott. Alessandro Rinaldi — Il Segretario: Chiara Bonini"),
        )),
    ),
)

PROJECT_SLIDES = Doc(
    doc_id="it-project-kickoff",
    title="Progetto Magazzino Unico — Kick-off",
    filename="kickoff_progetto_magazzino_unico.pptx",
    lang="it",
    category="presentation",
    format="pptx",
    layout="slide",
    pages=(
        Page((
            H("Progetto «Magazzino Unico»"),
            P("Kick-off meeting — Arcadia Logistics S.r.l. — 7 aprile 2025"),
            P("Sponsor: Alessandro Rinaldi — Project manager: Chiara Bonini"),
        ), notes="Benvenuto ai partecipanti; presentare il team e gli obiettivi della giornata."),
        Page((
            H("Obiettivi"),
            L((
                "Consolidare i magazzini di Prato e Campi Bisenzio in un unico polo",
                "Ridurre i tempi medi di evasione ordini da 38 a 24 ore",
                "Adottare il nuovo WMS con lettura RFID dei pallet",
                "Ridurre del 15% i costi di movimentazione interna",
            )),
        ), notes="Sottolineare che l'obiettivo delle 24 ore è vincolante per il bonus di progetto."),
        Page((
            H("Milestone"),
            T("kickoff-milestones", (
                ("Milestone", "Data prevista", "Responsabile"),
                ("M1 — Firma contratto WMS", "30/04/2025", "T. Gualtieri"),
                ("M2 — Layout nuovo polo approvato", "15/06/2025", "C. Bonini"),
                ("M3 — Migrazione dati anagrafiche", "30/09/2025", "Northwind Data"),
                ("M4 — Go-live polo unico", "12/01/2026", "C. Bonini"),
            )),
        ), notes="Il go-live di gennaio evita il picco natalizio; non anticipare."),
        Page((
            H("Rischi principali"),
            L((
                "Ritardo nella consegna delle scaffalature (fornitore unico)",
                "Resistenza al cambiamento del personale di Campi Bisenzio",
                "Qualità dei dati anagrafici articoli (duplicati, codici EAN mancanti)",
            )),
        ), notes="Per il rischio scaffalature è stato chiesto un secondo preventivo a Scaffal Tosca."),
        Page((
            H("Budget"),
            C("kickoff-budget", "pie", "Ripartizione budget di progetto",
              ("WMS e licenze", "Scaffalature", "Formazione", "Consulenza"),
              (("Quota", (42, 35, 8, 15)),),
              caption="Budget complessivo approvato: 1,35 milioni di euro"),
        ), notes="La quota maggiore va al WMS; la formazione è sottostimata secondo HR."),
        Page((
            H("Prossimi passi"),
            L((
                "Workshop requisiti WMS: 14 e 15 aprile",
                "Sopralluogo polo di Campi Bisenzio: 22 aprile",
                "Comitato guida mensile: primo martedì del mese",
            )),
        )),
    ),
)

BUDGET_XLSX = Doc(
    doc_id="it-budget-2026",
    title="Budget 2026 Arcadia Logistics",
    filename="budget_2026_arcadia.xlsx",
    lang="it",
    category="spreadsheet",
    format="xlsx",
    layout="sheet",
    sheet_names=("Costi per reparto", "Personale", "Investimenti"),
    pages=(
        Page((
            T("budget-reparti", (
                ("Reparto", "Consuntivo 2025", "Budget 2026", "Variazione"),
                ("Magazzino", "4.820.000", "5.105.000", "5,9%"),
                ("Trasporti", "6.310.000", "6.250.000", "-1,0%"),
                ("Amministrazione", "1.140.000", "1.180.000", "3,5%"),
                ("IT", "690.000", "930.000", "34,8%"),
                ("Commerciale", "980.000", "1.020.000", "4,1%"),
                ("Totale", "13.940.000", "14.485.000", "3,9%"),
            )),
        )),
        Page((
            T("budget-personale", (
                ("Qualifica", "FTE 2025", "FTE 2026", "Costo medio annuo"),
                ("Magazziniere", "84", "88", "34.500"),
                ("Autista", "37", "36", "39.800"),
                ("Impiegato amministrativo", "14", "14", "41.200"),
                ("Sviluppatore software", "4", "7", "52.000"),
                ("Quadro", "9", "10", "68.000"),
            )),
        )),
        Page((
            T("budget-investimenti", (
                ("Investimento", "Importo", "Trimestre", "Ammortamento (anni)"),
                ("Scaffalature polo unico", "472.500", "Q1", "10"),
                ("Lettori RFID e varchi", "118.000", "Q1", "5"),
                ("Licenze WMS (capitalizzate)", "310.000", "Q2", "3"),
                ("Carrelli elevatori elettrici (6)", "246.000", "Q3", "8"),
            )),
        )),
    ),
)

FIN_HDR = "Arcadia Logistics S.r.l. — Relazione finanziaria annuale 2024"

FIN_REPORT = Doc(
    doc_id="it-financial-report",
    title="Relazione finanziaria annuale 2024",
    filename="relazione_finanziaria_2024_arcadia.pdf",
    lang="it",
    category="financial",
    format="pdf",
    header=FIN_HDR,
    footer="Pagina {n} di {total}",
    pages=(
        Page((
            H("Lettera del Presidente"),
            P("Signori Soci, l'esercizio 2024 si è chiuso con ricavi in crescita dell'8,6% e un "
              "margine operativo lordo che per la prima volta supera il 15% dei ricavi. Il "
              "risultato è stato ottenuto nonostante l'aumento del costo del gasolio nel primo "
              "semestre e la temporanea chiusura del raccordo ferroviario di Prato Est."),
            P("Nel corso dell'anno abbiamo avviato il progetto Magazzino Unico, che "
              "consoliderà due siti logistici in un solo polo entro gennaio 2026, e abbiamo "
              "rinnovato la flotta con 24 trattori stradali Euro 6e."),
            C("fin-ricavi", "line", "Ricavi delle vendite (milioni di euro)",
              ("2020", "2021", "2022", "2023", "2024"),
              (("Ricavi", (12.1, 13.8, 15.2, 16.95, 18.4)),),
              caption="Grafico 1 — Andamento dei ricavi 2020–2024"),
            P("Alessandro Rinaldi, Presidente del Consiglio di Amministrazione"),
        )),
        Page((
            H("Conto economico riclassificato"),
            T("fin-ce", (
                ("Voce", Cell("Esercizio", colspan=2), "Variazione"),
                ("(migliaia di euro)", "2023", "2024", "%"),
                ("Ricavi delle vendite", "16.950", "18.402", "+8,6%"),
                ("Costi per servizi di trasporto", "-7.105", "-7.590", "+6,8%"),
                ("Costo del personale", "-5.320", "-5.744", "+8,0%"),
                ("Altri costi operativi", "-2.115", "-2.283", "+7,9%"),
                ("EBITDA", "2.410", "2.785", "+15,6%"),
                ("Ammortamenti", "-890", "-1.006", "+13,0%"),
                ("EBIT", "1.520", "1.779", "+17,0%"),
                ("Oneri finanziari netti", "-142", "-118", "-16,9%"),
                ("Imposte", "-344", "-449", "+30,5%"),
                ("Utile netto", "1.034", "1.212", "+17,2%"),
            ), header_rows=2, caption="Tabella 1 — Conto economico riclassificato²"),
            P("Il margine EBITDA è passato dal 14,2% al 15,1% dei ricavi. L'incremento degli "
              "ammortamenti riflette il rinnovo della flotta e i primi investimenti in "
              "scaffalature."),
            N("² I dati 2023 sono stati riesposti per riflettere la riclassifica dei costi di "
              "noleggio dei semirimorchi da «altri costi operativi» a «costi per servizi di "
              "trasporto»."),
        )),
        Page((
            H("Ricavi per area di attività"),
            C("fin-segmenti", "bar", "Ricavi 2024 per area (milioni di euro)",
              ("Contract logistics", "Trasporto groupage", "Ultimo miglio", "Servizi doganali"),
              (("2023", (7.1, 5.9, 2.6, 1.35)), ("2024", (7.6, 6.1, 3.4, 1.3))),
              caption="Grafico 2 — Ricavi per area di attività, confronto 2023–2024"),
            P("La crescita più marcata riguarda l'ultimo miglio, sostenuta dai nuovi contratti "
              "con la grande distribuzione nell'area metropolitana di Firenze; i servizi "
              "doganali risultano sostanzialmente stabili."),
            H("Indicatori finanziari"),
            T("fin-indicatori", (
                ("Indicatore", "2023", "2024"),
                ("ROE", "11,8%", "12,9%"),
                ("ROI", "9,4%", "10,6%"),
                ("Indice di liquidità corrente", "1,21", "1,34"),
                ("PFN / EBITDA", "1,61", "1,12"),
            )),
        )),
        Page((
            H("Evoluzione prevedibile della gestione"),
            P("Per il 2025 il Consiglio prevede ricavi compresi tra 19,5 e 20,2 milioni di "
              "euro. I principali rischi sono legati alla volatilità del prezzo del gasolio, "
              "alla disponibilità di autisti qualificati e ai tempi di realizzazione del polo "
              "logistico unico."),
            H("Proposta di destinazione dell'utile"),
            P("Si propone di destinare l'utile netto di euro 1.212.000 come segue: euro 60.600 "
              "a riserva legale, euro 400.000 a dividendo ai soci ed il residuo di euro 751.400 "
              "a riserva straordinaria."),
            P("Prato, 12 marzo 2025 — Per il Consiglio di Amministrazione, il Presidente "
              "Alessandro Rinaldi"),
        )),
    ),
)

NEWSLETTER = Doc(
    doc_id="it-newsletter",
    title="Il Mille Notizie — giornalino di quartiere, numero 31",
    filename="newsletter_quartiere_n31.pdf",
    lang="it",
    category="newsletter",
    format="pdf",
    layout="two-column",
    header="IL MILLE NOTIZIE — Giornalino del quartiere Campo di Marte — n. 31, giugno 2025",
    footer="{n}",
    pages=(
        Page((
            H("Riapre il mercato coperto", 1, column=0),
            P("Dopo diciotto mesi di lavori riapre sabato 14 giugno il mercato coperto di "
              "piazza Elia Dalla Costa. I banchi passano da 22 a 31 e arriva un'area "
              "ristoro con quaranta posti a sedere. Orari: dal lunedì al sabato dalle 7:30 "
              "alle 14:00, il venerdì anche dalle 16:00 alle 19:30.", column=0),
            H("Biblioteca: corsi estivi", 1, column=0),
            P("La biblioteca di quartiere organizza un corso gratuito di fotografia con lo "
              "smartphone tenuto da Sara Peruzzi, il martedì sera di luglio. Iscrizioni allo "
              "sportello o via e-mail entro il 30 giugno; posti limitati a 15 partecipanti.",
              column=0),
            H("Raccolta differenziata: cambia il calendario", 1, column=1),
            P("Da lunedì 7 luglio il ritiro porta a porta dell'organico passa a tre giorni "
              "settimanali: lunedì, mercoledì e sabato. Il multimateriale sarà ritirato il "
              "giovedì e la carta il martedì. I nuovi mastelli con microchip si ritirano "
              "presso il centro civico di viale dei Mille 32.", column=1),
            H("Torneo di calcetto", 1, column=1),
            P("Si è concluso il torneo di calcetto under 14: ha vinto la squadra della "
              "polisportiva Rondinella, che ha battuto in finale gli Amici del Campo di Marte "
              "per 5 a 3. Miglior giocatore del torneo Edoardo Paoli.", column=1),
        )),
        Page((
            H("Lavori in via dei Mille", 1, column=0),
            P("Dal 21 luglio al 29 agosto via dei Mille sarà chiusa al traffico nel tratto tra "
              "via Masaccio e piazza delle Cure per il rifacimento delle tubature del gas. Le "
              "linee di autobus 11 e 17 saranno deviate su viale Volta.", column=0),
            H("Intervista: la panetteria che arriva", 1, column=0),
            P("Luca Ferraris ci racconta il suo nuovo forno che aprirà in via dei Mille 27: "
              "«Pane a lievitazione naturale e schiacciata all'olio, come quella di mio "
              "nonno». Inaugurazione prevista per settembre.", column=0),
            H("Appuntamenti", 1, column=1),
            L((
                "28 giugno — Cinema all'aperto al giardino di via Lungo l'Affrico",
                "5 luglio — Cena di quartiere in piazza Elia Dalla Costa",
                "12 luglio — Visita guidata allo stadio Franchi",
            ), column=1),
            H("Scrivi alla redazione", 1, column=1),
            P("Hai una notizia o una segnalazione? La redazione si riunisce il primo giovedì "
              "del mese alle 21 al centro civico. Responsabile del numero: Paola Innocenti.",
              column=1),
        )),
    ),
)

PHYSICS_NOTES = Doc(
    doc_id="it-physics-notes",
    title="Dispensa di Fisica Tecnica — Trasmissione del calore",
    filename="dispensa_fisica_tecnica_cap4_scan.pdf",
    lang="it",
    category="academic",
    format="scan",
    header="Fisica Tecnica — Cap. 4 Trasmissione del calore",
    footer="{n}",
    pages=(
        Page((
            H("4.1 Conduzione in regime stazionario"),
            P("In una parete piana omogenea di spessore s e conducibilità termica λ, il flusso "
              "termico per unità di superficie in regime stazionario è dato dal postulato di "
              "Fourier:"),
            F("phys-fourier", r"q = \lambda \frac{T_1 - T_2}{s}", "q = λ (T1 − T2) / s"),
            P("Per una parete composta da n strati in serie si sommano le resistenze termiche "
              "dei singoli strati e le resistenze liminari interna ed esterna:"),
            F("phys-transmittance", r"U = \frac{1}{\frac{1}{h_i} + \sum_{k=1}^{n} \frac{s_k}{\lambda_k} + \frac{1}{h_e}}",
              "U = 1 / (1/hi + Σ sk/λk + 1/he)"),
            P("La trasmittanza U si misura in W/(m²·K). Per una parete in laterizio "
              "intonacato da 30 cm con 8 cm di isolante in EPS (λ = 0,035 W/(m·K)) si ottiene "
              "U ≈ 0,32 W/(m²·K)."),
        )),
        Page((
            H("4.2 Irraggiamento"),
            P("La potenza emessa per irraggiamento da un corpo grigio di emissività ε alla "
              "temperatura assoluta T segue la legge di Stefan-Boltzmann:"),
            F("phys-stefan", r"E = \varepsilon \sigma T^4", "E = ε σ T⁴"),
            P("dove σ = 5,67·10⁻⁸ W/(m²·K⁴) è la costante di Stefan-Boltzmann. Esercizio 4.3: "
              "calcolare la potenza emessa da una piastra di 0,5 m² a 350 K con ε = 0,9 "
              "(risultato: circa 383 W)."),
            T("phys-conducibilita", (
                ("Materiale", "λ [W/(m·K)]"),
                ("Rame", "390"),
                ("Acciaio", "50"),
                ("Calcestruzzo", "1,4"),
                ("Laterizio", "0,72"),
                ("Legno di abete", "0,13"),
                ("EPS", "0,035"),
            ), caption="Tabella 4.1 — Conducibilità termica di alcuni materiali"),
        )),
    ),
)

INSURANCE_DOCX = Doc(
    doc_id="it-home-insurance",
    title="Polizza multirischi abitazione — Condizioni di assicurazione",
    filename="polizza_casa_serena_condizioni.docx",
    lang="it",
    category="contract",
    format="docx",
    pages=(
        Page((
            H("POLIZZA «CASA SERENA» — CONDIZIONI DI ASSICURAZIONE (Mod. CS-2025/04)"),
            H("Art. 12 — Danni da acqua condotta", 2),
            P("La Società indennizza i danni materiali e diretti causati ai beni assicurati da "
              "fuoriuscita di acqua condotta a seguito di rottura accidentale di pluviali, "
              "grondaie e impianti idrici, igienici e di riscaldamento posti al servizio del "
              "fabbricato. Sono comprese le spese di ricerca e riparazione del guasto fino a "
              "euro 2.500 per sinistro."),
            P("Per ogni sinistro si applica una franchigia fissa di euro 250. Sono esclusi i "
              "danni da umidità e stillicidio e quelli causati da gelo a impianti di edifici "
              "non abitati da oltre 72 ore."),
            H("Art. 13 — Responsabilità civile della famiglia", 2),
            P("La Società tiene indenne l'Assicurato di quanto questi sia tenuto a pagare quale "
              "civilmente responsabile per danni involontariamente cagionati a terzi nella vita "
              "privata, con un massimale di euro 500.000 per sinistro. È compresa la "
              "responsabilità derivante dalla proprietà di animali domestici, con uno scoperto "
              "del 10% e un minimo di euro 150."),
        )),
        Page((
            H("Art. 14 — Furto e rapina", 2),
            P("La garanzia furto è prestata a primo rischio assoluto fino alla somma di euro "
              "8.000; per gioielli e preziosi il limite di indennizzo è di euro 2.000 se "
              "custoditi fuori da cassaforte."),
            H("Art. 15 — Denuncia del sinistro", 2),
            P("L'Assicurato deve dare avviso del sinistro alla Società entro tre giorni da "
              "quando ne è venuto a conoscenza, tramite l'app, il numero verde 800 000 471 "
              "o l'agenzia presso cui è assegnata la polizza; in caso di furto deve allegare "
              "copia della denuncia presentata all'Autorità."),
            T("ins-massimali", (
                ("Garanzia", "Somma assicurata / massimale", "Franchigia / scoperto"),
                ("Incendio fabbricato", "€ 280.000", "€ 200"),
                ("Danni da acqua condotta", "compresa", "€ 250"),
                ("Ricerca guasti", "€ 2.500", "€ 250"),
                ("RC della famiglia", "€ 500.000", "10% min. € 150 (animali)"),
                ("Furto contenuto", "€ 8.000", "—"),
            ), caption="Riepilogo garanzie della polizza n. CS-77104512"),
        )),
    ),
)

DOCS_IT = (
    LEASE, LEASE_COMMERCIAL, INVOICE_187, INVOICE_193, BANK_STATEMENT, UTILITY_BILL,
    BOILER_MANUAL, LAB_REPORT, CONDO_MINUTES, BOARD_MINUTES, PROJECT_SLIDES, BUDGET_XLSX,
    FIN_REPORT, NEWSLETTER, PHYSICS_NOTES, INSURANCE_DOCX,
)
