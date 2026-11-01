# LLM integration (Claude adapter defaults)

The pipeline talks to a provider-neutral gateway (`08-llm-providers-and-byok.md`); this
document covers the Anthropic adapter, which is the first implementation and the platform
default, and the request design that the other adapters mirror. Facts below were checked
against the Anthropic platform docs on 2026-10-30; prices are USD per million tokens and
change, so the eval harness re-reads them from configuration. An OpenAI-compatible adapter
(OpenAI, Azure OpenAI) ships alongside it; its default models are chosen by the same eval
harness rather than listed here.

## What we use and why

| Capability | Use in the pipeline | Notes |
|---|---|---|
| Native PDF input (`document` block, base64 or Files API) | Extraction and splitting | 600 pages / 128 MB per request on the first-party API. Each page is rasterised and text-extracted server-side, so a scan and a digital PDF go through the same call. Roughly 1 500–3 000 text tokens per page plus image tokens. |
| Image input | Page-level classification, review of a single page | Up to 600 images per request; cost ≈ ⌈w/28⌉ × ⌈h/28⌉ tokens, capped per image. |
| Structured outputs (`output_config.format`, JSON schema) | Every extraction and classification call | Guarantees schema-valid JSON for the canonical model. Restrictions to design around: no recursive schemas, no min/max, no `additionalProperties` other than false. Cannot be combined with citations in the same request. |
| Prompt caching | System prompt + canonical schema + supplier hints | The stable prefix (instructions, schema, few-shot) is identical across documents of the same type, so cache reads cost 5–10 % of the input price. Use the 1-hour TTL with batches. |
| Message Batches | Backfills, re-extraction after a prompt change, evals, overnight volumes | 50 % off, stacks with caching, usually completes within an hour, 24 h maximum. Not for the interactive path. |
| Citations (`page_location`) | Optional second pass for provenance | Only works on PDFs with a text layer and not together with structured outputs. We locate values ourselves instead (below), so citations are an optional extra. |
| Official C# SDK (`Anthropic` NuGet, `AnthropicClient`) | All calls | `Anthropic.Models.Messages` namespace; `DocumentBlockParam` + `Base64PdfSource`, `OutputConfig.Format = new JsonOutputFormat { Schema = ... }`, `CacheControlEphemeral`, `client.Messages.Batches`. Also integrates with `Microsoft.Extensions.AI` `IChatClient`. |

## Model choice

| Stage | Default model | Rationale |
|---|---|---|
| Extraction (header + lines) | `claude-opus-5-5` | Highest accuracy on line items and odd layouts; thinking is always on, so set `OutputConfig.Effort` explicitly (start at `low`, raise only if the eval shows gains). |
| Split points, document-type classification, "is this body an invoice" | `claude-haiku-5-5` | Small requests, many per day; prices rise above 100 k-token prompts, which these never reach. |
| Profile synthesis (writes extractor profiles) | `claude-opus-5-5` at `high` effort | Rare, high-value, offline. |
| Evals / judge | same as extraction, batch mode | |

The eval harness (`tests/DocProc.Evals`) runs the gold set against each enabled provider,
candidate model and prompt version and reports field accuracy, STP and cost. Whether Sonnet 5.5 or Haiku 5.5 is
good enough for extraction on this corpus is an eval result, not an assumption; the design
makes the model per stage a configuration value.

Enable the server-side refusal fallback on Opus 5.5 calls (`fallbacks: "default"` with the
`server-side-fallback-2026-07-01` beta) so a rare safety-classifier refusal on an odd
document degrades to another model instead of failing the job, and always check
`stop_reason` before reading content.

## Cost model

Assumptions: a 1–3 page invoice ≈ 6 k–20 k input tokens (text + page images) plus a ~2 k
cached prefix and 0.5–1.5 k output tokens. Thinking tokens are billed as output, which can
add 1.5–3× on the output side; with `low` effort on extraction this is small in absolute terms.

| Model | List price in / out | Per document (standard) | Per document (batch) | 10 000 docs / month |
|---|---|---|---|---|
| Haiku 5.5 | $0.10 / $0.50 | ~$0.001–0.003 | ~$0.0005–0.0015 | ~$10–30 |
| Sonnet 5.5 | $2 / $10 | ~$0.02–0.06 | ~$0.01–0.03 | ~$170–550 |
| Opus 5.5 | $4 / $20 | ~$0.03–0.11 | ~$0.02–0.06 | ~$340–1 100 |

For scale: Azure Document Intelligence prebuilt-invoice, AWS Textract AnalyzeExpense and
Google Invoice Parser all list at about $10 per 1 000 pages (≈ $0.01–0.03 per invoice), the
same order of magnitude as Sonnet and without line-item descriptions of the same quality.
The deterministic profile path costs CPU time only. A published Amazon study of the same
"synthesise an extractor once, run it LLM-free afterwards" design measured a ~13 k-token
baseline per invoice and a 62 % cumulative token reduction over 1 000 documents, breaking
even after roughly 50 documents per format, which matches the roadmap's expectation that
profiles pay off only for repeat suppliers.

## Request design

```
system (cached):   role, extraction rules (verbatim values, no guessing, null when unreadable,
                   how to treat deposits, discounts, multi-page tables, languages)
                   + canonical JSON schema description + 2–3 worked examples
user:              [document block: the PDF]  [text: tenant context – the tenant's legal name
                   and VAT id so the model can confirm the customer block; the supplier's
                   known name/VAT id/SKUs if matched; the last approved extraction for this
                   supplier as a hint ("supplier memory")]
output_config:     { format: json_schema(canonical Invoice schema, version N), effort: low }
```

Rules the prompt enforces, mirrored by validation so a prompt regression is caught:

- Copy values verbatim; normalisation happens in code.
- Never invent a currency, a due date or a tax rate; leave null and let validation flag it.
- Line items: one entry per printed line, keep continuation lines merged into the description,
  keep deposit and discount lines as separate entries with their type set.
- Report `unreadableRegions` (page, rough area, reason) so pages with poor scans get flagged.

### Timeouts

Every request carries `LlmOptions.Timeout` (default 3 minutes). Both adapters bound one network
attempt with it, the Anthropic adapter exactly like the OpenAI-compatible one: the timer is a
`CancellationTokenSource` created from the host's `TimeProvider` (`TimeProvider.System` by default,
a `FakeTimeProvider` in the tests, so expiry is driven by the clock rather than waited for) and
linked to the caller's token. An attempt that expires surfaces as a transient, retryable
`LlmException` (`RetryAfter` 5 s); the adapters never retry themselves (`MaxRetries = 0`), the
worker's retry policy does. The SDK client's own timeout (`NetworkTimeout`, 5 minutes) stays the
hard ceiling on a single attempt whatever the request asks for, and the caller's own cancellation
passes through unmapped. The credential probe (`ValidateCredentialAsync`) is bounded by the default
timeout.

The OpenAI-compatible adapter relies on OpenAI's automatic prefix caching and sends no
`prompt_cache_key`: the `OpenAI` package in use (2.14.0) exposes that field only on the Responses
API (`CreateResponseOptions.PromptCacheKey`), not on `ChatCompletionOptions`.

## Provenance without citations

After extraction we locate each returned value in our own positioned text layer (PdfPig words,
or OCR words for scans): exact match first, then a normalised match (whitespace, number
formats), then fuzzy. The match gives page and bounding box for the review UI and a cheap
sanity check: a required value that cannot be found anywhere in the text layer gets
`LOW_CONFIDENCE`. This works for scans too, which citations do not, and keeps structured
outputs available on the same request.

Fuzzy matching applies to free text only (party names, descriptions, notes, payment terms).
Identifier-like values (invoice number, order and delivery note references, VAT and tax ids,
SKU, GTIN, lot numbers) must match exactly or after normalisation: a document number
one digit off from the printed one is a different document number, so it is reported as not
found rather than confidently anchored to the wrong text. The text layer is indexed once per
document and the fuzzy pass only compares windows anchored on a word within one edit of the
value's first or last token, so hundreds of lookups per invoice stay in the millisecond range.

Short values are ambiguous on a page ("5", "12", a date printed twice), so the first equal word
is not good enough. A line item's values are searched in its own row: its description is
located first, and quantity, unit, prices and line amounts must sit in that description's
vertical band, never on the row's position number, and two values of one row (a unit price and
a line net that print the same amount) take different words. A value the row does not print is
not found, except the tax rate, which invoices often print once for all lines: it is then the
rate beside a tax label ("zzgl. 7 % MwSt."). Header dates prefer the occurrence beside their
own label (Rechnungsdatum, Lieferdatum, Fällig…), so an invoice that prints the same date as
invoice and delivery date boxes each on its own line (`ProvenanceLocator.LocateLine`,
`LocateHints`).

A region the model reports as unreadable becomes `UNREADABLE_PAGES`: blocking when a required
header field came back empty (the region may be what hid it), a warning otherwise.

## Confidence

LLM self-reported confidence is not calibrated enough to gate automation on (recent work
measured AUROC around 0.5–0.75 for verbalised confidence versus 0.9+ for validation-based
scores). We therefore derive confidence from:

1. validation outcome (arithmetic, tax, date plausibility, customer match),
2. agreement with supplier history (same supplier, same field format as previous approved docs),
3. text-layer locatability of the value,
4. for the first documents of a new supplier or after a prompt change: agreement between two
   independent extractions (second run at batch price).

Per-field thresholds live in tenant settings; the console shows the correction rate per field
and supplier so thresholds can be loosened where reviewers never correct anything.

## Data handling

- Send documents inline (base64) rather than persisting them in the Files API, so nothing
  tenant-related is stored at the provider beyond the request. BYOK tenants can point the
  adapter at Claude on Microsoft Foundry or their own Anthropic account so the data agreement
  is theirs. If Files API is ever used,
  note that files are workspace-scoped and the docs recommend one workspace per tenant.
- Batches keep results for 29 days at the provider; use them only for tenants whose data
  processing agreement allows it, otherwise stay on standard requests.
- Log per call: model, prompt version, schema version, token usage (incl. cache reads),
  latency, cost, stop reason; never the document content. These rows drive the cost
  dashboard and the per-tenant spend limits.
- Rate limits: a per-provider concurrency limiter in the worker; 429s back off with jitter;
  the queue absorbs bursts (a Monday-morning mailbox can deliver hundreds of invoices at once).

## Evaluation loop

- Gold set = approved documents with corrections applied, stratified by supplier, type,
  language and scan/digital. Target 200+ documents before the first threshold tuning.
- Metrics: per-field precision/recall, line-item row match (by SKU+qty+price), document-level
  "all required fields correct", STP rate, cost per document, p95 latency.
- Any prompt, schema or model change runs the eval in batch mode before promotion; results
  are stored and visible in the admin console.
