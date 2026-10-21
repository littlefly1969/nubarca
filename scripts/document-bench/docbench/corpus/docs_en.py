"""English golden documents. Every name, code, amount and person is FICTIONAL."""

from __future__ import annotations

from .model import C, Cell, Doc, F, H, L, N, P, Page, T

SAAS_AGREEMENT = Doc(
    doc_id="en-saas-agreement",
    title="Master Cloud Services Agreement",
    filename="northwind_master_services_agreement.docx",
    lang="en",
    category="contract",
    format="docx",
    pages=(
        Page((
            H("MASTER CLOUD SERVICES AGREEMENT No. NWD-MSA-2025-031"),
            P("This Agreement is entered into on 2 May 2025 between Northwind Data Ltd, a "
              "company registered in England and Wales under number 11873402 (the "
              "\"Provider\"), and Arcadia Logistics S.r.l., Via del Purgatorio 88, Prato, Italy "
              "(the \"Customer\")."),
            H("1. Services", 2),
            P("The Provider shall host and operate the warehouse data platform described in "
              "Schedule A, including the RFID event ingestion pipeline and the reporting "
              "warehouse, from data centres located within the European Economic Area."),
            H("2. Service Levels", 2),
            P("The Provider guarantees a monthly availability of 99.7% for the production "
              "environment, excluding scheduled maintenance announced at least five business "
              "days in advance. Incidents are classified as Severity 1 (platform unavailable), "
              "Severity 2 (degraded) or Severity 3 (minor)."),
            T("saas-sla", (
                ("Severity", "Response time", "Resolution target", "Service credit"),
                ("Severity 1", "30 minutes", "4 hours", "10% of monthly fee"),
                ("Severity 2", "2 hours", "1 business day", "5% of monthly fee"),
                ("Severity 3", "1 business day", "10 business days", "none"),
            )),
            P("Service credits in any calendar month are capped at 25% of the monthly fee and "
              "constitute the Customer's sole financial remedy for availability failures."),
        )),
        Page((
            H("3. Fees and Payment", 2),
            P("The Customer shall pay a fixed monthly fee of EUR 7,450 plus VAT, invoiced "
              "quarterly in advance. Invoices are payable within 45 days. Late payments bear "
              "interest at the rate set by Italian Legislative Decree 231/2002."),
            H("4. Data Protection", 2),
            P("The Provider acts as data processor under Article 28 GDPR. Personal data shall "
              "not be transferred outside the EEA without the Customer's prior written "
              "consent. The Provider shall notify the Customer of a personal data breach "
              "without undue delay and in any event within 36 hours."),
            H("5. Term and Termination", 2),
            P("This Agreement has an initial term of three years and renews for successive "
              "one-year periods unless either party gives notice of non-renewal at least 90 "
              "days before the end of the current term. Either party may terminate for "
              "material breach not remedied within 30 days of written notice."),
            H("6. Governing Law", 2),
            P("This Agreement is governed by Italian law. The courts of Milan shall have "
              "exclusive jurisdiction over any dispute arising out of it."),
            P("Signed for the Provider: Hannah Whitcombe, Chief Operating Officer. Signed for "
              "the Customer: Alessandro Rinaldi, Chairman."),
        )),
    ),
)

ROUTER_MANUAL = Doc(
    doc_id="en-router-manual",
    title="Halyard AX3000 Wi-Fi 6 Router — User Guide",
    filename="halyard_ax3000_user_guide.pdf",
    lang="en",
    category="manual",
    format="pdf",
    header="Halyard AX3000 User Guide — Version 2.1",
    footer="Page {n}",
    pages=(
        Page((
            H("1. Getting Started"),
            P("Connect the WAN port (blue) to your fibre or DSL modem with the supplied Cat6 "
              "cable, then connect the power adapter (12 V, 1.5 A). The router is ready when "
              "the Internet LED turns solid white, which normally takes about 90 seconds."),
            P("Open a browser and go to http://192.168.8.1 or http://halyard.setup. The "
              "default administrator password is printed on the label under the device; you "
              "will be asked to change it at first login."),
            H("2. LED Indicators"),
            T("router-leds", (
                ("LED", "State", "Meaning"),
                ("Power", "Solid white", "Device is on"),
                ("Power", "Blinking amber", "Firmware update in progress — do not unplug"),
                ("Internet", "Solid white", "Connected to the Internet"),
                ("Internet", "Solid red", "WAN cable connected but no Internet access"),
                ("Wi-Fi", "Blinking white", "WPS pairing active (2 minutes)"),
            )),
        )),
        Page((
            H("3. Reset and Recovery"),
            P("To restore factory settings, press and hold the recessed RESET button with a "
              "paper clip for at least 10 seconds until the Power LED blinks amber, then "
              "release it. All settings, including Wi-Fi names and passwords, are erased."),
            P("If the router does not boot after a failed firmware update, hold RESET while "
              "connecting the power and keep it pressed for 30 seconds to enter recovery mode; "
              "then upload the firmware file from http://192.168.8.1/recovery."),
            H("4. Technical Specifications"),
            T("router-specs", (
                ("Feature", "Specification"),
                ("Wi-Fi standard", "IEEE 802.11ax (Wi-Fi 6), dual band"),
                ("Maximum PHY rate", "574 Mbps (2.4 GHz) + 2402 Mbps (5 GHz)"),
                ("Ports", "1 × 2.5 GbE WAN, 4 × 1 GbE LAN, 1 × USB 3.0"),
                ("Security", "WPA3-Personal, WPA2/WPA3 mixed mode"),
                ("Power consumption", "max 18 W"),
            )),
        )),
        Page((
            H("5. Troubleshooting"),
            L((
                "Slow Wi-Fi in distant rooms: enable Mesh mode and add a Halyard AX1800 "
                "satellite.",
                "Devices cannot see the 5 GHz network: set the channel width to 80 MHz and "
                "the region to Europe.",
                "Internet LED solid red: restart the modem first, then the router.",
            )),
            H("6. Warranty and Support"),
            P("The router is covered by a 24-month limited warranty from the date of purchase. "
              "Support tickets can be opened at support.halyard.example with the serial "
              "number, which starts with HX3 and is printed under the device."),
        )),
    ),
)

DISCHARGE = Doc(
    doc_id="en-discharge-summary",
    title="Hospital discharge summary (fictional data)",
    filename="IMG_4471_discharge_summary.jpg",
    lang="en",
    category="medical",
    format="photo",
    pages=(
        Page((
            H("DISCHARGE SUMMARY — SAMPLE DOCUMENT WITH FICTIONAL DATA"),
            P("Patient: Thomas Ellery (fictional) — Admission: 14 Feb 2025 — Discharge: "
              "19 Feb 2025 — Ward: Cardiology B"),
            H("Diagnosis", 2),
            P("Paroxysmal atrial fibrillation with rapid ventricular response, successfully "
              "converted to sinus rhythm after pharmacological cardioversion with flecainide. "
              "Transthoracic echocardiogram: left ventricular ejection fraction 58%, no "
              "significant valvular disease."),
            H("Discharge medication", 2),
            T("discharge-meds", (
                ("Drug", "Dose", "Frequency"),
                ("Apixaban", "5 mg", "twice daily"),
                ("Bisoprolol", "2.5 mg", "once daily (morning)"),
                ("Flecainide (pill-in-the-pocket)", "200 mg", "single dose if palpitations"),
            )),
            P("CHA2DS2-VASc score 2. Follow-up with Holter ECG monitoring in six weeks at the "
              "outpatient arrhythmia clinic; avoid excessive alcohol and caffeine."),
            P("Discharging physician: Dr. Priya Raman, MRCP"),
        )),
    ),
)

QUARTERLY_SLIDES = Doc(
    doc_id="en-quarterly-review",
    title="Northwind Data — Q1 2025 Business Review for Arcadia Logistics",
    filename="northwind_qbr_q1_2025.pptx",
    lang="en",
    category="presentation",
    format="pptx",
    layout="slide",
    pages=(
        Page((
            H("Quarterly Business Review — Q1 2025"),
            P("Northwind Data Ltd for Arcadia Logistics S.r.l. — prepared by Hannah Whitcombe"),
        )),
        Page((
            H("Platform availability"),
            C("qbr-availability", "bar", "Monthly availability (%)",
              ("January", "February", "March"),
              (("Availability", (99.92, 99.41, 99.88)),),
              caption="Availability target: 99.7% per month"),
        ), notes="February dipped because of the storage controller incident on 11 Feb."),
        Page((
            H("Incidents"),
            T("qbr-incidents", (
                ("Date", "Severity", "Duration", "Root cause"),
                ("11 Feb 2025", "Severity 1", "3 h 42 min", "Storage controller firmware bug"),
                ("26 Feb 2025", "Severity 2", "55 min", "Expired TLS certificate on API gateway"),
                ("19 Mar 2025", "Severity 3", "2 h", "Delayed nightly report export"),
            )),
        ), notes="A service credit of 10% was applied to the February invoice."),
        Page((
            H("RFID event volume"),
            P("Events ingested per day grew from 1.8 million in January to 2.6 million in "
              "March as the Campi Bisenzio gates came online."),
            L((
                "Peak: 3.1 million events on 31 March (quarter-end inventory)",
                "Median ingestion latency: 1.4 s; 99th percentile: 6.8 s",
            )),
        )),
        Page((
            H("Next quarter"),
            L((
                "Migrate article master data (milestone M3) — target 30 September 2025",
                "Introduce anomaly alerts on gate read rates",
                "Rotate certificates automatically (follow-up from 26 Feb incident)",
            )),
        )),
    ),
)

INVENTORY_XLSX = Doc(
    doc_id="en-inventory",
    title="Warehouse inventory snapshot 30 June 2025",
    filename="inventory_snapshot_2025-06-30.xlsx",
    lang="en",
    category="spreadsheet",
    format="xlsx",
    layout="sheet",
    sheet_names=("Stock", "Reorder"),
    pages=(
        Page((
            T("inv-stock", (
                ("SKU", "Description", "Location", "On hand", "Unit cost EUR"),
                ("SKU-10442", "Pallet wrap film 23 µm", "PRA-A-03", "412", "11.40"),
                ("SKU-10457", "EUR pallet 1200x800 (used, grade A)", "PRA-Y-01", "1,250", "9.80"),
                ("SKU-20310", "RFID UHF pallet tag, adhesive", "CAM-B-12", "18,400", "0.21"),
                ("SKU-20311", "RFID UHF tag, hard case", "CAM-B-13", "2,300", "1.35"),
                ("SKU-30987", "Strapping band PET 16 mm", "PRA-A-07", "96", "38.00"),
                ("SKU-41002", "Thermal labels 100x150 mm (roll)", "CAM-C-02", "640", "4.60"),
            )),
        )),
        Page((
            T("inv-reorder", (
                ("SKU", "Reorder point", "Reorder quantity", "Supplier", "Lead time (days)"),
                ("SKU-10442", "300", "600", "Imballi Toscana", "5"),
                ("SKU-20310", "20,000", "50,000", "TagWave GmbH", "21"),
                ("SKU-30987", "120", "200", "Imballi Toscana", "5"),
                ("SKU-41002", "500", "1,000", "LabelPro Srl", "7"),
            )),
        )),
    ),
)

PAPER = Doc(
    doc_id="en-paper-rfid",
    title="Estimating Read Reliability of UHF RFID Gates in Cross-Docking Warehouses",
    filename="paper_rfid_gate_reliability.pdf",
    lang="en",
    category="academic",
    format="pdf",
    header="Proceedings of the Workshop on Logistics Informatics (fictional), pp. 41–44",
    footer="{n}",
    pages=(
        Page((
            H("Estimating Read Reliability of UHF RFID Gates in Cross-Docking Warehouses"),
            P("E. Conti¹, H. Whitcombe², M. Sileoni¹ — ¹ University of Prato (fictional), "
              "² Northwind Data Ltd"),
            H("Abstract", 2),
            P("Passive UHF RFID gates in cross-docking warehouses miss a fraction of tags on "
              "every pallet. We model the per-tag read probability as a logistic function of "
              "received signal strength and pallet speed, and show that a Bayesian "
              "Beta-Binomial estimator gives calibrated gate reliability with as few as 200 "
              "pallet passes. On 41,000 passes recorded at two warehouses the estimated "
              "per-pallet miss rate falls from 3.9% to 0.6% after antenna re-alignment."),
            H("1. Introduction", 2),
            P("Gate reads are the backbone of automated goods receipt. A missed tag forces a "
              "manual recount, which in our sites costs on average 4.5 minutes per pallet. "
              "Prior work estimates reliability from laboratory tests, which do not capture "
              "forklift speed or the shadowing effect of liquids and metal."),
        )),
        Page((
            H("2. Model", 2),
            P("Let r be the received signal strength in dBm and v the pallet speed in m/s. "
              "The probability that a single tag is read is"),
            F("paper-logistic", r"p(r, v) = \frac{1}{1 + e^{-(\beta_0 + \beta_1 r - \beta_2 v)}}",
              "p(r, v) = 1 / (1 + exp(−(β0 + β1 r − β2 v)))"),
            P("With k tags per pallet read independently, the probability that at least one "
              "tag of the pallet is missed is"),
            F("paper-miss", r"P_{miss} = 1 - \prod_{i=1}^{k} p(r_i, v)", "Pmiss = 1 − Π p(ri, v)"),
            P("Gate reliability θ is given a Beta(α, β) prior; after observing s successful "
              "passes out of n, the posterior is Beta(α + s, β + n − s), whose mean we report "
              "as the reliability estimate³."),
            N("³ We use α = β = 1 (uniform prior) throughout; results are insensitive to "
              "this choice above 100 passes."),
        )),
        Page((
            H("3. Results", 2),
            T("paper-results", (
                ("Site", Cell("Before re-alignment", colspan=2), Cell("After re-alignment", colspan=2)),
                ("", "Passes", "Miss rate", "Passes", "Miss rate"),
                ("Prato", "12,400", "3.9%", "9,800", "0.6%"),
                ("Campi Bisenzio", "10,300", "2.7%", "8,500", "0.8%"),
            ), header_rows=2, caption="Table 1: Per-pallet miss rate before and after antenna re-alignment"),
            C("paper-speed", "line", "Read probability vs. pallet speed",
              ("0.5", "1.0", "1.5", "2.0", "2.5", "3.0"),
              (("Tag on carton", (0.998, 0.995, 0.989, 0.97, 0.94, 0.89)),
               ("Tag near liquid", (0.97, 0.95, 0.91, 0.85, 0.77, 0.66))),
              caption="Figure 2: Estimated single-tag read probability as a function of speed (m/s)"),
            P("Tags placed near liquids degrade fastest with speed; above 2 m/s their read "
              "probability drops below 0.85, which motivates a speed limit at the gates."),
        )),
        Page((
            H("4. Conclusion", 2),
            P("A Beta-Binomial estimator over routine gate traffic is enough to monitor RFID "
              "gate reliability continuously, without laboratory tests. We recommend alerting "
              "when the posterior probability that reliability drops below 99% exceeds 0.95."),
            H("References", 2),
            L((
                "[1] D. Lowe, Antenna placement for portal readers, J. Supply Chain Tech., 2019.",
                "[2] A. Gelman et al., Bayesian Data Analysis, 3rd ed., CRC Press, 2013.",
                "[3] EPCglobal, UHF Class 1 Gen 2 Standard, version 2.1, 2018.",
            )),
        )),
    ),
)

DOCS_EN = (SAAS_AGREEMENT, ROUTER_MANUAL, DISCHARGE, QUARTERLY_SLIDES, INVENTORY_XLSX, PAPER)
