# Processing pipeline

One pipeline, many entry points, three extraction paths. Every stage is an idempotent
worker step driven by a durable queue; every stage writes its output to storage and
advances the document state, so any stage can be retried or re-run (for example,
re-extract all documents of a supplier after a profile change) without touching the
others.

```
                 ┌────────────┐  ┌───────────────┐  ┌───────────┐  ┌───────────┐
  mailbox ──────►│            │  │               │  │           │  │           │
  watched folder►│  1 Ingest  ├─►│ 2 Unpack &    ├─►│ 3 Split   ├─►│ 4 Classify│
  API upload ───►│            │  │   Normalize   │  │           │  │           │
  portal upload ►└────────────┘  └───────────────┘  └───────────┘  └─────┬─────┘
                                                                         │
                  ┌──────────────────────────────────────────────────────┘
                  ▼
            ┌───────────┐     ┌──────────────────────────────────────────────┐
            │ 5 Match   │     │ 6 Extract                                    │
            │ supplier  ├────►│   a) structured e-invoice XML   (no LLM)     │
            │ & profile │     │   b) extractor profile          (no LLM)     │
            └───────────┘     │   c) LLM (Claude), schema-constrained        │
                              └──────────────────────┬───────────────────────┘
                                                     ▼
                              ┌───────────────┐  ┌───────────┐  ┌───────────┐
                              │ 7 Validate &  ├─►│ 8 Review  ├─►│ 9 Publish │
                              │   enrich      │  │ (humans)  │  │  & learn  │
                              └───────────────┘  └───────────┘  └───────────┘
```

## Stage 1 – Ingest

| Channel | Mechanism | Notes |
|---|---|---|
| Mailbox per customer | Microsoft 365: Graph change notifications (webhook) on the tenant's mailbox, fall back to delta polling. Other providers: IMAP IDLE via MailKit. | One mailbox per tenant is the simplest tenancy boundary: the mailbox is the tenant key. Keep the full `.eml` as the immutable raw artefact. |
| Watched folder | Polling worker over SMB/SFTP/blob container with a per-tenant root; file is moved to `processed/` or `failed/` after a stable-size check. | Partial uploads are the classic bug: wait until size is stable for N seconds. |
| API upload | `POST /ingestions` with multipart or a pre-signed blob URL. | For integrations and the portal. |

Output: `Ingestion` + `IngestionItem` rows, blobs stored, SHA-256 computed. Exact duplicate items
(same tenant, same hash) are linked to the earlier document and skipped.

## Stage 2 – Unpack and normalize

- Expand archives (zip) one level; refuse nested archives beyond a depth of 2.
- Email body: if there are no candidate attachments and the HTML/text body itself looks like
  an invoice (some suppliers send inline), render the body to PDF and treat it as a candidate.
- Ignore obvious non-documents (signature images, logos < 20 KB, `.ics`, tracking pixels) with a
  recorded reason.
- Detect **embedded structured data first**: Factur-X / ZUGFeRD (XML attached inside a PDF/A-3),
  XRechnung / UBL / CII XML attachments, Peppol envelopes. These skip OCR and the LLM entirely.
- Convert everything else to a canonical pair: a PDF plus one PNG per page at 200 dpi (for
  classification, LLM vision input and the review UI), plus a **positioned text layer**
  (words with bounding boxes) from the PDF text if present, otherwise from OCR.
- Office formats (docx/xlsx) are converted to PDF; the original is kept.

## Stage 3 – Split

A single PDF frequently contains several invoices (batch scans) or an invoice plus its
delivery note. Splitting runs before classification:

1. Cheap heuristics first: page-level "document start" signals (an invoice number or
   "Invoice/Rechnung/Faktura/Račun" header reappearing, page numbering reset `1/3`, supplier
   header block repeating, blank separator pages).
2. If heuristics are uncertain or the file has more than ~6 pages, ask the LLM for split points
   using page thumbnails plus the first lines of text of each page. This is a tiny request
   compared with extraction and runs once per attachment: the splitter downscales the stage 2
   renders to thumbnails (long edge 768 px) and attaches at most the first 40 pages; the text of
   every page is always sent, and the model is told when thumbnails were left out.
3. Each resulting page range becomes a `Document`. Single-document files skip the LLM step.

## Stage 4 – Classify

Document type (Invoice, CreditNote, PurchaseOrder, DeliveryNote, Statement, Reminder, Other).

- If a supplier profile is already matched with a fixed document type, use it.
- Otherwise a cheap classifier: keyword rules over the text layer (multilingual), and when
  below threshold, a small LLM call with the first page image. Record `typeConfidence` and
  `typeSource`.
- `Unknown` goes straight to review.

## Stage 5 – Match supplier and extractor profile

Signals, in order of reliability: VAT id / tax id found in the text, IBAN, sender email
domain, supplier name fuzzy match against the tenant's supplier list, and the layout
fingerprint (see `04-known-format-automation.md`). The result is a `supplierId` (or a
"new supplier" suggestion) and, when the fingerprint matches an `Active` profile, an
`extractorProfileId`.

## Stage 6 – Extract

Three paths, tried in this order, each producing the canonical model from `02-data-model.md`:

a. **Structured e-invoice** (Factur-X/ZUGFeRD, XRechnung, UBL, CII): parse the XML and map
   fields. Confidence 1.0. Cost ≈ 0. This share of traffic grows every year under EU mandates.
b. **Extractor profile**: deterministic rules executed over the positioned text layer.
   Cost ≈ 0, milliseconds. Any rule failure or validation failure marks the attempt as failed
   and falls through to (c) with the flag `PROFILE_FALLBACK`.
c. **LLM extraction**: Claude with the document pages (native PDF input or page images),
   the canonical JSON schema as a structured output, and supplier-specific hints when we have
   them (previously confirmed extractions of the same supplier, known SKUs, quirks). Citations
   give page-level provenance for the review UI.

## Stage 7 – Validate and enrich

Runs identically regardless of extraction path:

- Schema and type checks (dates parse, numbers parse, currency valid).
- Arithmetic: Σ line net = subtotal; subtotal + tax = gross; tax breakdown reproduces tax;
  qty × unit price = line net within a tolerance (rounding, deposits, discounts).
- Business rules: customer block matches this tenant; issue date plausible; invoice number
  unique for the supplier; credit note references an existing invoice.
- Enrichment: match PO and delivery-note references to existing documents; map supplier SKUs
  to the tenant's product catalog; compute `amountDue`; derive `dueDate` from terms.
- Every failure becomes a `DocumentFlag`. Documents with no Blocking flags and all confidences
  above the tenant threshold are eligible for **auto-approval**; everything else gets a
  `ReviewTask`.

## Stage 8 – Review

Human-in-the-loop in the customer portal (tenant reviewers) or the admin console (operators).
The reviewer sees the page image with highlighted field locations, corrects values, resolves
flags, and approves or rejects. Corrections are recorded as `Correction` rows.

## Stage 9 – Publish and learn

- Emit `document.approved` / `document.rejected` events (outbox pattern) and deliver webhooks;
  expose exports (JSON, CSV, UBL) for accounting systems.
- Learning loop: when a supplier reaches N approved documents without an active profile,
  schedule **profile synthesis**; when a profile's recent success rate drops, mark it
  `Degraded` and schedule re-synthesis; store approved extractions as the evaluation gold set.

## Document state machine

```
Received → Normalized → Split → Classified → Matched → Extracted → Validated
   → (AutoApproved | NeedsReview → (Approved | Rejected))
   any stage → Failed(reason)  (retryable; surfaces in admin console)
```

State transitions are recorded in an append-only `DocumentEvent` table used for the
audit trail, the admin timeline view and metrics (time-to-approve, STP rate).

## Idempotency and retries

- Each stage consumes a message `{documentId, stage, attempt}`; the handler checks the
  current state before doing work and is safe to run twice.
- Blob keys are content-addressed (`{tenant}/{sha256}`), so re-processing never duplicates storage.
- LLM calls are recorded with request hash, model, token usage and cost before the result is
  applied, so a crash between the call and the write can be reconciled without paying twice.
