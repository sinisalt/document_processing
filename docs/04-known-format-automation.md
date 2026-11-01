# Known-format automation ("extractor profiles")

## Is it a good idea?

Yes, with three qualifications. Treat this as the decision record.

1. **Do it as a confidence-gated hybrid, not as a template engine with an LLM bolted on.**
   The LLM is the default path and the thing that makes the product work on day one. The
   deterministic path is an *optimisation* that is only allowed to answer when it can prove
   its answer (anchors found, types parse, arithmetic validates). Any doubt falls back to the
   LLM. This keeps accuracy at the LLM level while cost converges to near zero for suppliers
   that send the same layout every week, which in the restaurant supply chain is most of them
   (the same ten to thirty suppliers deliver to a given restaurant, usually weekly).
2. **It works best on digital-native PDFs.** When a supplier's system generates the PDF, word
   positions are exact and stable. On scans and photos, OCR noise and skew make anchor rules
   brittle; the fallback rate will be high and the saving small. The pragmatic rule: profiles
   are synthesised only for suppliers whose documents arrive with a text layer (or whose
   scans OCR cleanly), everything else stays on the LLM path and is still cheap enough (see
   `06-llm-integration.md` for per-document cost).
3. **The cheapest "known format" is a structured e-invoice.** Factur-X/ZUGFeRD, XRechnung,
   UBL (Peppol BIS 3.0) and national formats carry the data as XML. Germany already requires
   receiving them (since 2025) and issuing them from 2027/2028, Croatia from 2026, France from
   September 2026, Poland via KSeF in 2026. Parsing XML is deterministic, free, and
   exact. Build this path first: it is both the simplest and the one whose share will grow.

There is also a middle tier worth measuring before investing in the rule engine:
**LLM with supplier memory**. Same model, but the prompt includes the last confirmed
extraction for this supplier and its known quirks, and the stable system prompt + schema are
prompt-cached. This usually raises accuracy noticeably and is far simpler than templates. The
roadmap orders the work: XML → LLM with caching → LLM with supplier memory → deterministic
profiles for the top-volume suppliers, with measurement at each step.

## What a profile is

A profile is a versioned JSON document executed by a small deterministic engine over the
*positioned text layer* (words with bounding boxes, page size normalised to 1000 × 1000 units
so the same rules survive dpi changes). The LLM writes profiles; it never runs inside them.

```jsonc
{
  "profileId": "sup_metro_de_v3",
  "documentType": "Invoice",
  "fingerprint": {
    "senderDomains": ["metro.de", "rechnung.metro.de"],
    "vatIds": ["DE811177936"],
    "anchorTokens": ["METRO Deutschland GmbH", "Rechnungsnummer", "Kundennummer"],
    "anchorPositions": [[62, 40], [720, 180], [720, 205]],   // where those tokens normally sit
    "pageSize": [595, 842],                                  // A4 points, tolerance applied
    "minAnchorHits": 3
  },
  "fields": {
    "invoiceNumber": {
      "strategy": "label-right",                  // value to the right of a label on the same line
      "label": { "regex": "Rechnungs-?Nr\\.?|Rechnungsnummer", "page": 1 },
      "value": { "regex": "^[A-Z0-9/-]{6,20}$" },
      "type": "string"
    },
    "issueDate": {
      "strategy": "label-right", "label": { "regex": "Rechnungsdatum" },
      "type": "date", "format": ["dd.MM.yyyy"]
    },
    "totalGross": {
      "strategy": "label-row",                    // numeric cell on the row containing the label, rightmost
      "label": { "regex": "Rechnungsbetrag|Gesamtbetrag" }, "page": "last",
      "type": "money", "numberFormat": "de-DE"
    },
    "supplier.vatId": { "strategy": "regex-anywhere", "value": { "regex": "DE\\d{9}" } }
  },
  "lineItems": {
    "strategy": "column-table",
    "headerRow": { "anchors": ["Art.-Nr.", "Bezeichnung", "Menge", "Einheit", "Preis", "Betrag"] },
    "columns": {                                    // x-ranges in normalised units; derived from header positions
      "supplierSku": [40, 140], "description": [140, 520], "quantity": [520, 600],
      "unit": [600, 660], "unitPrice": [660, 780], "lineNet": [780, 960]
    },
    "rowGrouping": "by-y-with-wrap",                // continuation lines merge into description
    "stopAt": { "regex": "Zwischensumme|Nettobetrag" },
    "numberFormat": "de-DE",
    "repeatsOnEveryPage": true
  },
  "postConditions": ["sumLines(lineNet) ≈ subtotalNet ± 0.02", "subtotalNet + totalTax ≈ totalGross ± 0.02"]
}
```

Strategies are a closed, small set (label-right, label-below, label-row, fixed-region,
regex-anywhere, column-table, key-value-block). A closed set is what makes the engine testable
and lets the LLM generate profiles reliably: it is choosing from a menu, not writing code.

## Lifecycle

```
            N approved LLM extractions for supplier S (N = 3 by default)
                              │
                              ▼
                   ┌──────────────────────┐   LLM is given: the positioned text of the samples,
                   │ Synthesize (LLM)     │   the confirmed canonical values, the strategy menu,
                   └──────────┬───────────┘   and asked for a profile that reproduces the values
                              ▼
                   ┌──────────────────────┐   Engine runs the candidate on all N samples plus any
                   │ Replay & verify      │   other approved documents of S. Every field must match
                   └──────────┬───────────┘   the approved value. Otherwise: one repair round, then drop.
                              ▼
                   Candidate ──(human or automatic approval)──► Active
                              │
                              ▼
            Live: each new document of S fingerprint-matches ──► engine runs ──► validation
                              │ pass                                   │ fail
                              ▼                                        ▼
                    canonical result, cost ≈ 0            PROFILE_FALLBACK flag, LLM path,
                                                            failure counted against the profile
                              │
                              ▼
            successRate30d < 90 % or 3 consecutive failures ──► Degraded ──► re-synthesize
            from the latest approved documents (layout drift); old version Retired, kept for audit
```

Shadow mode is the safety net during roll-out: for the first M documents after activation the
engine and the LLM both run, results are compared field by field, and only when they agree on
all required fields for M consecutive documents does the profile start answering alone.

## Fingerprinting (how we recognise "same format as before")

Stage 5 computes, per document, a cheap signature and compares it with every Active profile
of the matched supplier (and, when no supplier matched, with the global registry):

- exact signals: sender domain, VAT id, IBAN, supplier name found in text;
- layout signals: presence and approximate position of the profile's anchor tokens
  (`minAnchorHits`), page size, number of columns detected in the line-item header;
- optional: a text-shingle hash of the first page with digits masked, used for near-duplicate
  detection and as a fast pre-filter when the registry grows large (SQL Server's native
  `VECTOR` type and distance functions, or a MinHash index; decision deferred until volume
  justifies it).

A profile only claims a document when the exact signals agree *and* the layout signals pass.
A supplier may have several Active profiles (invoice vs credit note, old vs new layout).

## Cross-tenant sharing

Suppliers in the restaurant sector are highly shared (wholesalers, breweries, beverage
distributors). Profiles are attached to a **global supplier** record so that a profile learned
from tenant A's documents benefits tenant B. What is shared is the *layout rules*, never
tenant data or document content; the sample document ids referenced by a profile are visible
only to platform operators. A tenant can opt out of sharing. This is the main reason the
feature pays off at platform scale rather than per customer.

## Prior art this design follows

- **CACHE-ED2 (Amazon, 2026)**: an LLM agent synthesises an extraction program in a small
  DSL per document format, validates it by execution against the page, caches it, and later
  documents of that format run LLM-free; a learned format encoder routes documents to cached
  programs because naive structural fingerprints missed on OCR noise and skew; reviewer
  corrections are turned into program fixes. Reported ~0.99 accuracy on 5 000 real invoices and
  break-even after ~50 documents per format. Our "profile" is that program, our strategy menu is
  that DSL, and the fingerprint section above is the routing problem it warns about.
- **Sensible**: hand-authored layout configs for high-volume senders plus one generalised LLM
  config for the long tail; configs claim documents through fingerprint tests; several matching
  configs run and the best-scoring result wins.
- **Rossum**: per-field calibrated thresholds and history-based validation ("does this match
  what we exported for this vendor before"); trains only on human corrections to avoid
  feedback loops.
- **Azure Document Intelligence custom template vs custom neural**: template models need a
  consistent layout and break on variation; neural models generalise at higher training cost.
  The same trade-off, which is why profiles are per layout and the LLM remains the generalist.

## Why not fine-tuned layout models, cloud invoice parsers, or pure templates?

- *Cloud prebuilt invoice parsers* (Azure Document Intelligence prebuilt-invoice, AWS Textract
  AnalyzeExpense, Google Invoice Parser) cost about the same per page as an LLM pass on a
  cheap model and are weaker on line-item descriptions and on non-standard layouts. They are a
  reasonable **OCR provider** for scans (Azure Layout gives positioned words and tables) but
  not the extraction brain. Keep the OCR provider behind an interface; start with Tesseract,
  switch to Azure Layout if scan quality demands it.
- *Fine-tuned layout models* (LayoutLM, Donut) reach high F1 on benchmarks but need labelled
  data per layout and language and degrade sharply on unseen schemas. Frontier VLMs are at or
  above their accuracy zero-shot, so the training pipeline is not worth owning.
- *Pure template tools* (invoice2data style) are the deterministic engine above, minus the
  LLM to author templates and minus the fallback. Authoring templates by hand does not scale
  past a few dozen suppliers.

## Metrics that decide whether this is working

| Metric | Definition | Target after roll-out |
|---|---|---|
| Path share | % documents answered by XML / profile / LLM | profile + XML > 60 % of volume for stable suppliers |
| Profile precision | % profile answers that reviewers did not correct | ≥ LLM precision on the same supplier |
| Fallback rate | % profile attempts that fell back | < 10 % per Active profile |
| Cost per document | blended LLM + OCR spend | < 25 % of the LLM-only baseline at steady state |
| STP rate | % documents auto-approved without a human touch | measured per tenant, reported in the console |
