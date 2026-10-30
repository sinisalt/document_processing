# Canonical data model

This is the "internal data we collect" contract. Every extraction path (e-invoice XML,
supplier extractor profile, LLM) must produce the same canonical shape, so downstream
code (validation, review UI, exports, webhooks) never cares how a value was obtained.

Conventions:

- All monetary values are `decimal` with an explicit ISO 4217 currency on the document.
- Every extracted field carries **provenance** (see `FieldValue<T>` below), not just a value.
- Dates are `DateOnly`; timestamps are UTC `DateTimeOffset`.
- Unknown / unreadable values are `null` with a `status`, never a guessed default.
- Identifiers that come from the document (invoice number, PO number) are stored as
  strings verbatim plus a normalised form (trimmed, upper-cased, whitespace collapsed)
  used for matching and duplicate detection.

## 1. Tenancy and master data

```
Tenant (customer, e.g. one restaurant or a restaurant group)
  id, name, slug, status, defaultCurrency, country, timezone
  settings: reviewPolicy (auto-approve threshold, always-review suppliers), retentionYears

TenantMember
  tenantId, userId, role (TenantAdmin | Reviewer | Viewer)

LlmProviderConfig / TenantLlmPolicy  (see 08-llm-providers-and-byok.md)
  which provider, endpoint and models each stage uses; key stored in Key Vault by reference;
  spend caps; whether the platform key may be used as fallback

IngestionChannel (one tenant has many)
  id, tenantId, type (Mailbox | WatchedFolder | ApiUpload | PortalUpload)
  config: mailbox address / IMAP or Graph settings; folder path; API key id
  status, lastPolledAt, lastError

Supplier (per tenant – the same real-world vendor can exist for many tenants)
  id, tenantId, name, vatId, taxId, iban[], emailDomains[], addresses[]
  globalSupplierId (nullable link to a cross-tenant registry used for sharing extractor profiles)

ExtractorProfile (the "known format")
  id, globalSupplierId or supplierId, version, status (Candidate | Active | Degraded | Retired)
  fingerprint: { senderDomains[], anchorTokens[], pageGeometry, textHash }
  rules: JSON (see 04-known-format-automation.md)
  stats: attempts, successes, lastSuccessAt, lastFailureAt, successRate30d
  createdFrom: documentIds[] (samples), createdBy (LLM | Human), approvedBy
```

## 2. Ingestion objects

```
Ingestion (one email / one dropped file / one API call)
  id, tenantId, channelId, receivedAt, source: { messageId, from, subject, filename }
  rawBlobRef (the original .eml or file, immutable)
  status (Received | Unpacked | Processed | Failed)
  items: IngestionItem[]

IngestionItem (one attachment or body part)
  id, ingestionId, filename, mimeType, sizeBytes, sha256, blobRef
  kind (Pdf | Image | Xml | Office | Text | Archive | Other)
  disposition (Candidate | Accepted | Ignored(reason) | Unpacked(childIds) | Duplicate(ofItemId))
    -- at most one Accepted item per (tenantId, sha256): filtered unique index, see 03-pipeline.md

Document (one logical business document; an item can be split into several)
  id, tenantId, ingestionItemId, pageRange (start, end), pageCount
  normalizedBlobRef (PDF/A or PDF), pageImageRefs[], textLayerRef (positioned words JSON)
  type (Invoice | CreditNote | PurchaseOrder | DeliveryNote | Statement | Reminder | Other | Unknown)
  typeConfidence, typeSource (Rule | Llm | Human | Structured)
  supplierId (nullable until matched), supplierMatchConfidence
  extractorProfileId (nullable), extractionMethod (StructuredXml | Profile | Llm | Manual)
  status  -- see state machine in 03-pipeline.md
  flags: DocumentFlag[]
  costs: { llmInputTokens, llmOutputTokens, ocrPages, usd, llmProvider, llmModel, keyOwner (Platform | Tenant) }
  createdAt, updatedAt, approvedAt, approvedBy
```

## 3. Field provenance

```
FieldValue<T>
  value: T | null
  status: Extracted | Inferred | Missing | Unreadable | Conflict | Corrected
  confidence: 0..1 | null
  source: { method: StructuredXml | Profile | Llm | Ocr | Human,
            page: int | null, bbox: [x0,y0,x1,y1] | null, rawText: string | null }
  history: [{ value, status, source, at, by }]   -- audit trail for corrections
```

Confidence is calibrated per method:

- StructuredXml: always 1.0 (value comes from the embedded XML, not from reading pixels).
- Profile: 1.0 if the anchor matched exactly and the parsed value passed its type check; lowered
  by validation failures.
- Llm: self-reported confidence is weak; we derive confidence from (a) agreement between
  two extractions when we run them, (b) validation outcome, (c) citation presence.
- Human: 1.0, status Corrected.

## 4. Document types and their fields

### 4.1 Shared blocks

```
Party
  name, vatId, taxId, registrationNumber, address: Address, email, phone, iban, bic
Address
  line1, line2, postalCode, city, region, countryCode (ISO 3166-1 alpha-2)
Money
  amount: decimal, currency: string
TaxBreakdownLine
  rate: decimal (percent), taxableAmount: Money, taxAmount: Money, category (S | Z | E | AE | O | K | G)
PaymentTerms
  dueDate, discountPercent, discountUntil, paymentMeans (BankTransfer | DirectDebit | Card | Cash | Other),
  paymentReference, iban, bic
```

### 4.2 Invoice (also used for CreditNote with `kind = CreditNote` and negative-or-positive handling)

Header (all `FieldValue<...>`):

| Field | Type | Required | Notes |
|---|---|---|---|
| kind | Invoice / CreditNote / Proforma | yes | credit notes reference the original invoice |
| invoiceNumber | string | yes | verbatim + normalised |
| issueDate | date | yes | |
| deliveryDate / servicePeriod | date / {from,to} | no | tax-relevant in EU |
| dueDate | date | no | derive from terms if absent, status Inferred |
| currency | string | yes | default tenant currency only with status Inferred |
| supplier | Party | yes | at least name; VAT id strongly preferred |
| customer | Party | yes | used to verify the document really belongs to this tenant |
| orderReferences[] | string | no | PO numbers (used to match to PurchaseOrder) |
| deliveryNoteReferences[] | string | no | used to match to DeliveryNote |
| originalInvoiceReference | string | credit note only | |
| subtotalNet | Money | yes | |
| taxBreakdown[] | TaxBreakdownLine | yes if taxable | |
| totalTax | Money | yes | |
| totalGross | Money | yes | |
| amountDue | Money | no | gross minus prepayments / deposits |
| rounding | Money | no | |
| charges[] / allowances[] | {description, amount, taxRate} | no | freight, pallets, discounts |
| paymentTerms | PaymentTerms | no | |
| notes | string | no | free text that reviewers may need |

Line items (`InvoiceLine[]`):

| Field | Type | Notes |
|---|---|---|
| lineNumber | int | position on the document |
| supplierSku | string | supplier's article code |
| gtin | string | EAN/GTIN if printed |
| description | string | verbatim |
| quantity | decimal | |
| unit | string | normalised to a small enum (kg, g, l, ml, pc, box, case, crate, pallet, hour, other) plus raw |
| packSize | string | "6 x 1.5 l" kept raw and parsed when possible |
| unitPrice | Money | net |
| discountPercent / discountAmount | | |
| lineNet | Money | |
| taxRate | decimal | |
| lineGross | Money | optional, many suppliers do not print it |
| depositAmount | Money | bottle/crate deposits are common in food & beverage |
| lotNumber / bestBefore | string / date | present on food invoices and delivery notes |
| matchedProductId | id | tenant-side catalog mapping, filled by matching, not extraction |

### 4.3 PurchaseOrder (issued by the tenant to the supplier)

Header: poNumber, orderDate, requestedDeliveryDate, supplier (Party), buyer (Party), deliveryAddress,
currency, totalNet, totalGross (optional), status.
Lines: lineNumber, supplierSku, description, quantityOrdered, unit, unitPrice, lineNet.

### 4.4 DeliveryNote

Header: deliveryNoteNumber, deliveryDate, supplier, recipient, orderReferences[], carrier, signedBy (if visible),
temperatureReadings[] (cold-chain docs), notes.
Lines: lineNumber, supplierSku, description, quantityDelivered, unit, quantityOrdered (if printed),
lotNumber, bestBefore, backorderQuantity.

### 4.5 Other / Unknown

Stored with page images and text only; no header fields. Classified for the review queue.

## 5. Flags (what we show the customer when something could not be read)

```
DocumentFlag
  code, severity (Info | Warning | Blocking), field (optional), message, createdBy (Pipeline | Human), resolvedAt
```

Initial flag codes:

| Code | Severity | Raised when |
|---|---|---|
| UNREADABLE_PAGES | Blocking | OCR text density below threshold or image unreadable |
| TYPE_UNKNOWN | Blocking | classifier confidence below threshold |
| SUPPLIER_UNMATCHED | Warning | no supplier matched; new supplier suggested |
| CUSTOMER_MISMATCH | Blocking | customer block does not look like this tenant |
| MISSING_REQUIRED | Blocking | any required header field is Missing/Unreadable |
| TOTALS_MISMATCH | Blocking | sum(lines) ≠ subtotal or net+tax ≠ gross beyond tolerance |
| TAX_MISMATCH | Warning | tax breakdown does not reproduce totalTax |
| LINE_ARITHMETIC | Warning | qty × price ≠ line net on one or more lines |
| LOW_CONFIDENCE | Warning | any field confidence below tenant threshold |
| DUPLICATE_SUSPECTED | Blocking | same supplier + number (+ amount/date) already exists |
| PROFILE_FALLBACK | Info | extractor profile failed and the LLM path was used |
| LLM_PROVIDER_UNAVAILABLE | Blocking | the tenant's LLM provider key is invalid, over quota or capped; document waits |
| MULTI_DOCUMENT_SPLIT | Info | the attachment was split into several documents |
| CURRENCY_INFERRED | Info | currency not printed, tenant default assumed |
| PO_UNMATCHED | Info | PO reference present but not found |
| DATE_IMPLAUSIBLE | Warning | issue date in the future or > 1 year old |
| INVALID_VALUE | Warning | a value failed its type or format check (malformed currency code, amount in a different currency than the document) |
| UNSUPPORTED_TYPE | Warning | the document type (purchase order, delivery note, statement, other) has no extraction path yet; the document goes to review without canonical data |

## 6. Review and audit

```
ReviewTask
  id, documentId, tenantId, reason (flags summary), assignedTo, status (Open | InProgress | Done | Skipped)
  slaDueAt

Correction (append-only, feeds profile learning and evals)
  id, documentId, fieldPath, oldValue, newValue, by, at, reason
```

Confirmed documents (approved with or without corrections) become the gold set for:

- evaluation of the LLM prompts / models (field-level accuracy, STP rate), and
- generating or re-validating extractor profiles for that supplier.
