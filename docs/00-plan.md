# Document processing platform – research and plan

Date: 2026-10-30. Status: proposal for a blank-slate repository.

## The problem in one paragraph

Restaurants (our tenants) receive invoices, credit notes, purchase orders and delivery notes
from many suppliers, as PDFs, scans, photos, occasionally XML, by email (one mailbox per
tenant) or dropped into a folder. We do not know what a file is until we read it, and one
email can carry several documents. We need to turn each document into one canonical data
set, flag anything unreadable or missing, let the tenant see and correct it, and do all of
this cheaply enough that the hundredth invoice from the same supplier costs almost nothing.

## Answer to the main question

**Automating known formats so that repeat documents from the same supplier skip the full LLM
pass is a good idea**, and it is how the mature commercial products (and a 2026 Amazon paper
on exactly this design) work. Two conditions make it pay off rather than hurt accuracy:

1. The deterministic path must be **gated by validation**: it only answers when its anchors
   match and the totals, tax and line arithmetic reconcile. Anything else falls back to the
   LLM and counts against the profile. The LLM stays the default; the profile is an optimisation.
2. It should be **synthesised by the LLM from approved documents, verified by replay, and
   retired automatically on drift**, never hand-authored. Hand-written templates do not scale
   past a few dozen suppliers and break silently when a supplier changes layout.

Two cheaper wins come first and are in the roadmap ahead of profiles: parse **structured
e-invoices** (Factur-X/ZUGFeRD, XRechnung, UBL/Peppol) directly, which EU mandates are
making common, and run the LLM with **prompt caching and supplier memory**, which cuts cost
and raises accuracy with no new engine. Profiles are then built for the high-volume suppliers
where the measured savings justify it. Full reasoning: `04-known-format-automation.md`.

## The plan in brief

| Doc | Content |
|---|---|
| `01-architecture-and-stack.md` | Two .NET 10 processes (API, worker) over SQL Server (Azure SQL in production, container locally), Azure Service Bus and Azure Blob Storage; library and vendor choices with licences; solution layout; multi-tenancy and data protection. |
| `02-data-model.md` | The canonical model: tenants, channels, suppliers, documents, field provenance, the fields we collect for invoices, purchase orders and delivery notes, and the flag catalogue for unreadable or missing data. |
| `03-pipeline.md` | Nine stages from ingest to publish, three extraction paths (XML, profile, LLM), validation rules, review, state machine, idempotency. |
| `04-known-format-automation.md` | Extractor profiles: what they are, lifecycle, fingerprinting, cross-tenant sharing, why not the alternatives, success metrics. |
| `05-api-and-ui.md` | API-first principles, roles, resource model, the admin console and the customer portal. |
| `06-llm-integration.md` | Claude capabilities used, model per stage, cost model, prompt design, provenance, confidence, data handling, evals. |
| `07-roadmap.md` | Six phases with exit criteria, risks and open decisions. |

## Key research findings (checked 2026-10-30)

**Platform**

- .NET 10 is the current LTS (Nov 2025 → Nov 2028); .NET 11 is STS and lands in Nov 2026.
  EF Core 10 and Aspire 13 need .NET 10. Target .NET 10.
- MassTransit v9 is commercial; v8 stays Apache-2.0 but is maintenance-only. Wolverine (MIT)
  is the recommended messaging/outbox layer; Hangfire (LGPL) and Quartz.NET (Apache) remain
  options for pure scheduling.
- Hosting is Azure with SQL Server as the only database: Azure SQL Database in production,
  the SQL Server 2025 container locally. EF Core 10's SQL Server provider covers native `json`
  columns and the vector type, so fingerprint similarity stays inside the database.
- PdfPig (Apache-2.0) for positioned text and PDFtoImage (MIT, PDFium) for rendering are
  active; iText is AGPL; Docnet is stale.
- Microsoft Graph change notifications + delta queries handle M365 mailboxes (application
  `Mail.Read`, subscriptions renewed roughly weekly); MailKit 5 (MIT) covers IMAP IDLE.
  Plus-addressing is on by default in Exchange Online but suppliers' systems sometimes strip
  the plus sign, so a shared mailbox per tenant is the safer design.

**Extraction**

- Cloud invoice parsers (Azure Document Intelligence prebuilt-invoice, AWS Textract
  AnalyzeExpense, Google Invoice Parser) list at about $10 per 1 000 pages. Independent tests
  put frontier VLMs at or above them on header fields and clearly ahead on line-item
  descriptions; fine-tuned layout models (LayoutLM, Donut) need labelled data per layout and
  degrade on unseen schemas.
- Claude: native PDF input (600 pages, 128 MB per request), structured outputs via JSON schema
  on all current models, prompt caching (cache reads at 5–10 % of input price), Message
  Batches at 50 % off, official C# SDK (`Anthropic` NuGet). Structured outputs and citations
  cannot be combined in one request. Estimated cost per 1–3 page invoice: Haiku 5.5
  ~$0.001–0.003, Sonnet 5.5 ~$0.02–0.06, Opus 5.5 ~$0.03–0.11 (half in batch mode).
- Verbalised LLM confidence is poorly calibrated (AUROC ~0.5–0.75 in a Sept 2026 study);
  validation-derived scores reach 0.9+ and allow 50–70 % of fields to be auto-approved at
  ≤10 % error. Gate automation on arithmetic and history checks, not on self-reported scores.
- Amazon's CACHE-ED2 (2026) synthesises per-format extraction programs with an LLM, caches
  them, and runs later documents LLM-free; it reports ~0.99 accuracy on real invoices and a
  62 % cumulative token reduction over 1 000 documents with break-even near 50 documents per
  format. The hard parts it reports are robust format routing (fingerprints that survive OCR
  noise) and validating the synthesised program before trusting it. Sensible (fingerprint
  tests + fallback config), Rossum (per-field thresholds, history-based validation) and Azure
  custom template vs neural models describe the same hybrid shape.
- Industry straight-through-processing rates for accounts payable average around a third of
  invoices, with best-in-class near half; vendor-reported STP of 75–92 % applies to clean
  digital invoices. Expect scans and photos to dominate the review queue.

**Compliance**

- EU e-invoicing: Germany must receive e-invoices since 2025 and issue from 2027/2028;
  Croatia B2B from 2026; Poland KSeF from 2026; France receive from Sept 2026; Slovenia 2028;
  ViDA fixes intra-EU digital reporting for 2030. Formats converge on EN 16931 (UBL 2.1 and
  CII syntaxes, Peppol BIS 3.0, Factur-X/ZUGFeRD).
- Retention: Germany cut accounting-document retention to 8 years (from 2025), Croatia
  requires 11 years, France 10. Make retention a per-tenant setting with country defaults.
- GDPR: statutory retention is the lawful basis for keeping invoice data; erase what is not
  covered when the period ends; keep an access log.

## Immediate next steps

1. Review this plan; confirm the remaining Phase 0 decisions (Entra External ID for customer
   login, Azure Container Apps for hosting, Azure SQL tier).
2. Collect 50–100 real documents from two or three pilot suppliers (mix of digital PDF, scan,
   photo, XML) to seed the gold set and the first profile experiments.
3. Start Phase 0 (solution skeleton, canonical model, tenancy, CI).
