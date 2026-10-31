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

## Implementation

The runner lives in `src/DocProc.Pipeline` and is composed into both hosts by one call,
`builder.AddDocProcProcessingStack()` (API) / `builder.AddDocProcProcessingStack(runInProcessDefault: true)`
(worker). The helper registers PDF processing, classification and splitting (`DocumentProcessing:*`),
e-invoice parsing, the LLM gateway with its EF-backed ports (`EfLlmPolicyStore`,
`ConfigurationLlmCredentialStore`, `EfLlmUsageRecorder`) and the vendor adapters, blob storage
unless the host registered it already, and the pipeline itself.

**Queue modes.** `InProcessPipelineQueue` implements `IPipelineQueue` over an unbounded
`System.Threading.Channels` channel; a delayed enqueue (retry with backoff) waits on a background
continuation driven by the injected `TimeProvider` (the tests advance a fake clock instead of
waiting) that is cancelled on shutdown. It is the transport for local development, the
dependency-free mode and the tests, and it is not durable: a message in flight when the process
stops is lost, which the idempotent handlers and a re-process from the API tolerate.
`Pipeline:RunInProcess` (default: true outside Production; the worker defaults to true everywhere)
decides whether `PipelineRunnerHostedService` consumes the queue in that process. With it off, only
the queue is registered so the API can still enqueue; the Service Bus transport that makes this
durable in Azure is a later block. The runner processes `Pipeline:MaxParallelism` messages at a
time (default 2), one DI scope and one `DbContext` per message, with the scoped `TenantContext` set
to the message's tenant before any query runs. Messages with the same key (the document id, or the
ingestion id for stage 1) are serialised through a keyed single-flight gate (`KeyedWorkGate`), so
two copies of one message never run at the same time in one process; the copy that waits finds the
stage already done and is dropped by the status check.

**Concurrency token.** `Document`, `Ingestion` and `ReviewTask` carry an `int Version` column
(migration `AddConcurrencyTokens`) that `DocProcDbContext` increments on every update and uses as
an optimistic concurrency token, so the status check above holds across processes too: two
handlers (or a handler and a reviewer) that loaded the same row and both write it cannot overwrite
each other. The second writer's `SaveChanges` fails with `DbUpdateConcurrencyException`; a stage
handler treats that as `Skipped` (an Information log line, no failure, no retry, nothing enqueued),
`IngestHandler` drops its attempt the same way, and `FailureRecorder` drops its record because the
other writer's outcome stands.

**Stages.** `IIngestionHandler` (`IngestHandler`) implements stage 1 and the unpacking half of
stage 2: item kinds from extension and content type, zip archives expanded one level (nested
archives beyond `Pipeline:MaxArchiveDepth`, default 2, are ignored; a corrupt or truncated archive is ignored with the reason
`unreadable zip archive: <reader message>`; entries over `Pipeline:MaxArchiveEntryBytes` are
skipped, and the copy itself stops at that limit, so a header that under-declares the entry size
does not get around it), exact duplicates (same tenant, same SHA-256) linked to the item that
already owns a document for the bytes (disposition `Accepted`) or, when none does yet, to the
strictly earlier candidate (older `CreatedAt`, or the same instant and the smaller v7 id), so two
ingestions of the same bytes processed at the same time agree on the one that owns the document, an
older item is never the duplicate of a newer one that is still undecided, and an existing document
is never created a second time, images under `Pipeline:MinimumImageBytes` (default 20 KiB) and `.ics` files
ignored with a reason, one `Received` document per remaining candidate, which becomes `Accepted`. The handler commits in two
units of work: the files unpacked from archives are inserted first, as candidates (their `CreatedAt`
is the unpacking time; the ingestion's version token bumps with them, so a twin message on another
node drops out before it inserts a second set), and only then are the dispositions decided and the
documents created and committed together, so two ingestions carrying the same zip see each other's
files just as they see the attachments the API stored before enqueueing. A retry after a crash
between the two commits finds the archive `Unpacked` and its files as candidates, and decides them
without unpacking again.

**Durable ownership.** The rule "one owner per tenant and bytes" is enforced by the database, not by
the query: the filtered unique index `UX_IngestionItems_TenantId_Sha256_Accepted` on
`IngestionItems(TenantId, Sha256) WHERE Disposition = 'Accepted'` (migration `AddItemOwnershipIndex`,
which also back-fills `Accepted` for the items that already own a document, in platform scope because neither
migration path sets a tenant for the row-level security policy; the SQLite schema carries
the same index with its own filter syntax) admits at most one `Accepted` item per tenant and SHA-256.
When another process accepts the same bytes between one handler's duplicate query and its commit,
that commit fails on the index (`IngestionItemOwnership.IsViolation` recognises the failure on both
providers), the handler re-decides its accepted items against the owners now committed: each one
with a committed owner becomes that owner's `Duplicate` (the items of the same ingestion that pointed
at it follow it to the owner), its `Document` and `document.received` event leave the unit of work
before anything is inserted, and the commit is tried again, with only the surviving documents queued
for stage 2. Every round relinks at least one item, so the loop ends; a violation that no committed
owner explains surfaces as the error it is. There is no in-process gate any more: the index covers
the two-node case and the single-node case alike, and the strictly-earlier rule keeps the sequential
case free of such retries.

**Document stages.** One `IStageHandler` per document stage (`Normalize`,
`Split`, `Classify`, `Match`, `Extract`, `Validate`, `Publish`) follows; every handler loads the
document, compares its status with the one the stage starts from and drops a stale message with a
log line, so a message delivered twice does nothing the second time. Each stage writes a
`document.stage_completed` event with its details (page counts, split signals, classification
source, matching method, extraction path and cost, validation flags) to the timeline; the validate
stage adds `DUPLICATE_SUSPECTED` only for a matched supplier (two unmatched documents with the same
number are not compared with each other) and decides between `AutoApproved` (plus a `Publish`
message) and `NeedsReview` (plus an open `ReviewTask`). Each stage owns the flag codes it writes
and replaces them on every run; `Split`, `Classify` and `Extract` also own
`LLM_PROVIDER_UNAVAILABLE`, which the failure recorder writes on their behalf, so a later successful
run clears it.

**Failure policy.** A `DocumentProcessingException` (encrypted or corrupt PDF, too large) or a
`StageFailedException` (unsupported format, XML that is not an e-invoice or does not parse, no
extraction path) is about the document: `Failed(reason)` with a `document.stage_failed` event and no
retry. A retryable
`LlmException` (rate limit, transient) is re-enqueued with exponential backoff
(`Pipeline:RetryBaseDelay`, default 5 s, doubled per attempt, capped by `Pipeline:MaxRetryDelay`)
up to `Pipeline:MaxAttempts` (default 5); the last attempt ends `Failed` with the
`LLM_PROVIDER_UNAVAILABLE` flag. An `LlmException` that blocks the tenant (invalid key, quota or
spend cap, no usable configuration) ends `Failed` with the same flag at once, because a retry
cannot fix it and the document is waiting for an operator. Anything else retries with the same
backoff and then fails with the exception as reason. Every retry leaves a `document.stage_failed`
event marked `retry: true`, so the job inspector shows the attempts.

**Stubbed for later blocks.** OCR (image items and PDFs without a text layer carry
`UNREADABLE_PAGES` as a blocking flag, as the catalogue in docs/02 says, go through classification
and extraction by image only and always end in review: until OCR lands nothing but the LLM has read
those pages, so an image-only invoice is never auto-approved), extractor profiles
(`ExtractorProfileId` stays null and the profile path has no implementation), Office and text
items (`Failed: unsupported format`), the Service Bus transport, webhook delivery (stage 9 writes
the `OutboxMessage` row; nothing reads it yet), exports and the learning loop. The LLM credential
store reads `Llm:Secrets:<secretRef>` from configuration or `LLM_SECRET_<SECRET_REF>` from the
environment; the Key Vault store replaces it in Azure.
