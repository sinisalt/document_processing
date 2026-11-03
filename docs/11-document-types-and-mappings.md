# 11. Configurable document types, supplier layouts and output mappings

Status: accepted (decisions in section 24, 2026-11-02). This document replaces the "fixed document types" assumptions in docs/00, 02, 03, 04, 05, 06 and 07 (the list is in section 25). Where it conflicts with them, this document wins.

## 1. Why this document exists

The current design fixes the shape of every document in compiled C#:

- the closed `DocumentType` enum (`src/DocProc.Domain/Documents/DocumentEnums.cs:3-13`);
- the sealed `Invoice`, `InvoiceLine`, `PurchaseOrder` and `DeliveryNote` records;
- the reflection walk in `CanonicalPatcher`;
- the static `InvoiceValidator.Default`;
- the `InvoiceExtraction` LLM contract;
- the static `PathOrder` and `PipelineStages.Next`;
- the hand-written review form in the customer portal's `lib/canonical.ts` (replaced in P1 by the generated form of `web/packages/review`).

Extractor profiles are "never hand-authored" (docs/00-plan.md:22-24). Nothing identifies a supplier by folder or mailbox, and nothing leaves the system except an ids-only event that nobody reads.

The product owner needs the opposite:

- We do not know in advance which documents a tenant will process.
- We ship standard templates, but tenants define their own structures and custom fields.
- Each supplier gets its own mapping on top of the tenant's types.
- The supplier is identified from the incoming folder, mailbox or From address, and its configuration is applied automatically.
- A reviewer can map fields from any file to a chosen type when the system cannot tell what a document is, and that mapping is learned.
- Mapped data is sent to existing systems in the format those systems dictate.
- Handlers can be modified per type and per supplier.
- Documents are not only invoices: a full price list or today's specials list arrives as a document too, and has to be converted into the receiving system's format (follow-up F1).
- The operator can say "this is an invoice because it has X and Y" (the word "Invoice" or the word "Rechnung"), and because one supplier's documents keep their format, those identifiers are checked strictly (follow-up F2).
- A supplier can send several designs of the same type (different source systems, a redesign). A design that is not recognised goes to review, where it is matched to a type, pre-filled from what the system knows, adjusted, and remembered (follow-up F3).

This design keeps the parts of the runtime that are already generic and moves every shape and every per-type or per-supplier behaviour into versioned, tenant-scoped data. Those generic parts are the runner, the queue, the outbox, the `FieldValue` provenance envelope, the text layer, `ProvenanceLocator`, the LLM gateway, the e-invoice parsers and `SupplierMatcher`.

## 2. Requirements and where they are met

| # | Requirement | Met by | Section | First demo |
|---|---|---|---|---|
| R1 | Document types are data. Standard templates can be adopted, copied and modified, and tenants can define new types. | `DocumentTypeDefinition`/`DocumentTypeVersion`, compiled to a JSON Schema, an LLM schema, a form spec and field descriptors | 5, 6.1 | P1 |
| R2 | Custom fields, such as a supplier-specific ID mapped to a custom field | Declared fields on the tenant type, plus a declared `ext` bag for one-off supplier attributes; `DocumentFieldIndex` for search | 5.6, 6.2 | P1 |
| R3 | Per-supplier mappings on top of the tenant's types | `SupplierLayout`/`SupplierLayoutVersion`: the field mapping of a layout (locate rules, transforms, defaults, LLM hints, rule and step overrides), group inheritance | 8 | P4 |
| R4 | Output in custom formats to existing systems | `OutputDestination`, `OutputMapping` (JSONata, later Liquid for text formats), `OutputBinding`, per-destination `OutboxDelivery`, multi-call recipes, batches, acknowledgements | 13 | P3 |
| R5 | Manual mapping of Unknown documents from any file, learnable | `:set-type`, map-from-page mode, table tool, structured viewer, `:learn-layout`, replay, activation lifecycle | 12 | P2 (capture), P4 (learn) |
| R6 | Supplier identified by folder, mailbox or From address, with its configuration auto-applied | Source metadata on `Ingestion`, `SupplierSourceRule` (multi-condition), `SupplierSourceResolver` at ingest, `TypeSource.Supplier`, `AppliedConfigJson` | 7 | P4 |
| R7 | Modifiable handlers per type and supplier | Fixed phases with configurable, keyed steps inside each phase; supplier `pipelineOverrides`; plugin step kinds such as `http-lookup` | 10 | P5 |
| R8 (F1) | Price lists (full list, today's specials) converted into the receiving system's format | Templates `std.price-list` and `std.daily-specials` (validity, large line table, pack size, tier prices); multi-page `column-table` mapped once per layout; validation and auto-approve without totals (row control figures, table completeness, deltas to the previous list); `article-price-import` output mapping, usually to its own destination | 5.8, 8.6, 11, 12.4, 13.10 | P1 (type, always reviewed), P2 (capture), P3 (conversion and delivery), P5 (auto) |
| R9 (F2) | One or more components of a document identify its type ("Invoice" or "Rechnung"; "Rechnung" in the title and our VAT id), checked strictly per supplier | **Identification rules**: identifiers (exact text, regex, supplier master-data value, source fact) with optional page and region, combined with `all`/`any` (and `none`), evaluated without a score; scopes supplier layout, supplier group, tenant fallback; evaluated in Classify before the keyword rules and the LLM; created in review by clicking words or drawing a box | 8.5, 9.4, 12.9 | P4 (supplier), P5 (tenant) |
| R10 (F3) | Several designs per supplier and type; an unrecognised design triggers review and manual matching to a type, with known details pre-filled | **Supplier layouts**: layout = identification rule + field mapping, versioned, several Active per supplier and type; blocking `NEW_LAYOUT` when the supplier is known but no layout matches; "match new layout" review flow with pre-fill from master data, other layouts, known values and the LLM; saved as a new layout on approval | 6.6, 8.4, 12.10 | P4 |

The requirements also imply the following, and the design covers each one:

- master data synced from the receiving systems (section 14);
- several layouts per supplier and type, selected by strict identification rules (8.4, 8.5);
- supplier groups (6.4);
- a receiving legal entity (6.4);
- re-splitting a document in review (12.7);
- asynchronous ERP acknowledgements (13.8);
- batched exports (13.7);
- configuration audit, promotion and regression suites (15);
- RLS for platform rows (6.10);
- data protection and cost limits (16);
- an operations view that tells the product owner which supplier to onboard next (17).

## 3. Design principles

1. **One canonical format.** Every document keeps today's `CanonicalJson`: a tree whose leaves are the `FieldValue` envelope `{value, status, confidence, source{method, page, bBox, rawText, path, ruleRef, step}, history[]}`.
   - Standard templates use exactly today's camelCase paths. Existing rows, `Correction.FieldPath` values and replay fixtures stay valid with no backfill.
   - Validation and outputs read a *values view* derived from the tree. There is no second stored tree.
2. **Types are data; code provides kinds.** Tenants compose and parameterise platform-registered field kinds, rule kinds, step kinds, strategies, transports and renderers. Tenants never upload code. New capabilities ship as platform plugins registered with keyed DI.
3. **Phases stay fixed; steps inside them are configurable.** The coarse pipeline (Ingest, Normalize, Split, Classify, Match, Extract, Validate, Publish), `DocumentStateMachine` and the "one status per stage" idempotency in `DocumentStageHandler` stay as they are. What happens inside Split, Classify, Extract, Validate and Publish comes from the type version, patched by the supplier's settings and the selected layout's overrides.
4. **No stage reorder.** The supplier is resolved from the source at document creation. When its configuration fixes the type, Classify only transitions (`TypeSource.Supplier`). Match stays where it is and corroborates the source-resolved supplier against content evidence.
5. **Every configuration is versioned, immutable once published, and pinned per document.** `Document.AppliedConfigJson` records which type version, supplier layout version (with the identification evidence), source rule, supplier settings snapshot and pipeline hash a document was processed with. A retry uses the pinned configuration; a reprocess offers "pinned" or "current".
6. **Additive migrations behind per-tenant feature flags.** `TenantSettings.Features` holds `configurableTypes`, `outputs`, `sourceRouting`, `supplierLayouts`, `customTypes` and `structuredSources`, all off by default and read only through `ITenantFeatures` (`src/DocProc.Application/Configuration/ITenantFeatures.cs`). The P0 migration writes the flags object into every existing tenant's settings: EF updates a nested owned JSON object only in place, so a settings row without it could not be switched on. Nothing is dropped until the hardening phase. The invoice family behaves identically throughout and is the regression gate.
7. **The invoice family stays strong.** The std invoice and credit note templates keep `LlmInvoiceExtractor`, today's prompt and examples, `invoice_extraction@1` byte-identical, the UBL/CII parsers and the eight coded invoice rules. These run through adapters over the same tree.
8. **Safe by default.** Nothing that a tenant newly configured auto-approves until it has rules, an explicit policy and a review warm-up. This applies to a new type, a new layout and a source rule that relies on an unauthenticated sender.
9. **Strict rules decide, scores only suggest.** Which layout (and therefore which field mapping) applies to a document is decided by identification rules that either hold or do not (section 8.5). A similarity score may rank suggestions for a reviewer; it never applies a layout. A known supplier whose document matches none of its layouts goes to review with a blocking `NEW_LAYOUT`.

## 4. The model in one page

An operator configures four things, in this order:

1. **Document types.** The tenant's set: adopted standard templates, copies with custom fields, and new types.
2. **Suppliers.** Identity (VAT, IBAN, name), attributes such as `erpVendorId`, routing rules (folder, mailbox, From, subject, filename), and one or more **layouts** per type. A layout is one design of one document type from that supplier: an **identification rule** (the components that prove "this is Nordwind's 2025 invoice", for example "Rechnung" in the page-1 title area and Nordwind's VAT id) plus a **field mapping** (where each value sits). A supplier that invoices from two systems has two invoice layouts, both Active.
3. **Destinations and output mappings.** Where data goes, in which shape, and for which events, types and suppliers.
4. **Processing.** The steps inside each phase for a type, and what a supplier changes.

Tenant-wide identification rules ("a document with 'Preisliste' in the title is a price list") can be added as a fallback for suppliers that have no layouts of their own.

A reviewer works on the documents the system could not handle. They pick the type, map values from the page or file, mark the words that identify the type, approve, and click "Remember for this supplier". The result becomes a new layout of that supplier (identification rule plus field mapping), plus source rules. When a known supplier sends a design none of its layouts recognises, the document goes to review flagged `NEW_LAYOUT`, and the same flow, pre-filled with what the system already knows about the supplier, saves the new design as an additional layout (section 12.10).

There are three mapping layers, in data-flow order:

```
 source file + source facts                     tenant document type                       receiving system
 (PDF text layer, XML, CSV, mail facts)         (CanonicalJson = FieldValue tree)          (ERP JSON / file / feed)
        |                                               |                                          ^
        |  1. Supplier layout: Source -> Fields          |  2. Enrich: Fields -> Fields             |  3. Output mapping: Fields -> Output
        |     (identify, then rules, transforms)         |     (defaults, lookups, compute,         |     (JSONata over the values view,
        +----------------------------------------------->|      http-lookup)                        |      per destination and binding)
                                                         +------------------------------------------+
```

Runtime path for one e-mail. The stage names are unchanged; the new behaviour is shown in brackets.

```
Mail poller -> Ingestion{sender, recipient, auth, subject}
  Ingest   [SupplierSourceResolver: rule hit -> SupplierId, ReceivingEntityId, pinned type?]
  Normalize[INormalizer per item kind: pdf | image | e-invoice xml | generic xml/json | csv/xlsx | office->pdf]
  Split    [policy: pinned type's split.mode | else the type of the one layout or tenant rule that holds on the
            whole item (split prefilter, 5.8) | else tenant default; auto | never | perPage]
  Classify [supplier known and layout-managed: its layouts' identification rules (strict) -> layout + type
            | no match -> NEW_LAYOUT (blocking; type only proposed, TypeSource=Proposed);
            supplier unknown, or without layouts and layouts not required: tenant identification rules
            -> tenant keyword set -> LLM enum of tenant types;
            skip type decision if TypeSource=Supplier|Human|Hint|Structured, but still resolve the layout]
  Match    [corroborate: VAT/IBAN/name vs source rule -> SUPPLIER_CONFLICT/AMBIGUOUS;
            supplier found only now by content, or replaced -> resolve its layouts here (may re-type before Extract)]
  Extract  [steps: xml-einvoice -> supplier-mapping (the layout's field mapping) | layout-prefill on NEW_LAYOUT
            -> llm(fill-missing) -> defaults -> transforms -> http-lookup]
  Validate [validate.schema -> definition rules + supplier rule overrides -> review gate]
  Publish  [outbox (written in decision tx) -> bindings -> OutboxDelivery per destination -> transports]
```

## 5. Document types as data

### 5.1 Templates, adopt, copy, new

- **Platform templates** have `TenantId` NULL, are read-only and ship as embedded JSON in the new project `src/DocProc.Definitions/Templates/*.json`:
  - `std.invoice`, `std.credit-note`, `std.purchase-order`, `std.delivery-note`, `std.statement`, `std.reminder`, `std.price-list`, `std.daily-specials`, `std.generic`.
  - `std.price-list` is a supplier's list of article prices with a validity period: mostly one large line table (article/SKU, GTIN, description, unit, pack size, price, currency, optional tier prices), no totals. `std.daily-specials` is shipped as a fork of `std.price-list`: the same field tree, but single-day validity, its own classification keywords, rules and review priority. Section 5.8 defines both and explains why they are two templates rather than one with a variant field.
  - Neither price-list template is adopted automatically by existing tenants (they have no legacy key); a tenant adopts them from the catalogue.
  - The invoice and credit-note field trees are generated once from `Invoice.cs`, `InvoiceLine.cs`, `Party.cs`, `Tax.cs` and `PaymentTerms.cs` by a dev-time tool, then committed. A parity test asserts that their envelope paths (the fields with their own `FieldValue`) equal `Invoice.EnumerateAllFields()`, and that the whole tree, including the members today's records store bare (`provenance: false`: the VAT summary, charges and allowances, a line's number, printed unit and matched product), is the records' tree member by member.
  - The purchase-order and delivery-note templates are generated from today's records, which no code path creates.
  - P0 seeds the seven templates of the legacy keys (`std.invoice`, `std.credit-note`, `std.purchase-order`, `std.delivery-note`, `std.statement`, `std.reminder`, `std.generic`) in `src/DocProc.Definitions/Templates`. `std.statement`, `std.reminder` and `std.generic` have no fields yet, because no canonical data is stored for those types today; their fields arrive with the first extraction path for them. The two price-list templates ship with P1.
- **Adopt.** The tenant gets a type that follows the template. Every existing tenant automatically adopts all std templates under the legacy keys `Invoice`, `CreditNote`, `PurchaseOrder`, `DeliveryNote`, `Statement`, `Reminder` and `Other`, so the stored `Document.Type` values are already valid type keys. An adopted type moves to a new template version automatically when that version is non-breaking. A breaking template change is offered as an upgrade.
- **Copy (fork).** The tenant edits the type: adds fields, deprecates optional std fields, relabels, changes required flags, rules and processing. The type keeps its key (so `Invoice` can be forked in place) and stays pinned to the template version it was copied from. A template update is offered as a three-way diff with one-click merge and is never applied automatically.
- **New type.** A tenant can create a type from scratch, for example `return-slip`, or start it from `std.generic`.
- **Restructuring std fields.** It is allowed. Coded invoice rules declare the roles and kinds they require. If a fork changes them, the publish report says "invoice.totals disabled: role `net` missing" and offers the generic `sumEquals` and `compare` rules instead. There is one storage format either way; nothing is "invoice-backed" versus "generic".
- `Unknown` remains a reserved key.

### 5.2 Definition format

A definition is the JSON stored in `DocumentTypeVersion.DefinitionJson`. It is validated on save by a meta-schema, `schemas/document-type-definition.v1.json`, which is snapshot-tested like the other `schemas/*.v1.json`.

**Field kinds.** Each kind carries typed semantics from `src/DocProc.Domain/Common`:

| Kind | Stored value | Semantics |
|---|---|---|
| `string`, `text` | string | `text` is multi-line |
| `identifier` | `{verbatim, normalized}` (the `DocumentIdentifier` wire shape) | `Identifiers.Normalize`/`NormalizeCompact` |
| `integer`, `decimal`, `percent` | number | `percent`: 19 means 19 % |
| `money` | `{amount, currency}` | `currencyFrom` names the currency field. The shorthand "bare number" correction uses it. |
| `currency` | ISO 4217 string | Normalised as today (`CanonicalPatcher.cs:327-359`) |
| `date`, `period` | `yyyy-MM-dd`, `{from, to}` (the `DatePeriod` wire shape) | Supplier locale drives parsing |
| `boolean` | bool | |
| `enum` | code | Inline `options`, or `codeList` (a `LookupTable` with aliases) |
| `unit` | UN/ECE code (version 1 trees hold the `UnitOfMeasure` name, which the `uom` list maps) | `UnitOfMeasureParser` plus tenant and supplier aliases from the `uom` lookup |
| `party`, `address`, `taxRow`, `paymentTerms`, `chargeOrAllowance`, `temperatureReading` | object | Shipped composites mirroring `Party.cs`, `Tax.cs`, `PaymentTerms.cs`, `ChargeOrAllowance` (`Invoice.cs`) and `TemperatureReading` (`DeliveryNote.cs`), inlined at compile time; their members are fixed in `FieldKinds.CompositeMembers` |
| `object` | object | Nested group |
| `array` | array of objects | Repeating group; `anchor` names the row-anchor field used by `LocateLine` |
| `bag` | `{key: scalar FieldValue}` | Declared extension bag for supplier-specific attributes (`ext`) |

**Per-field attributes:**

- `id`: stable, immutable, of the form `fld_xxx`.
- `key`: the JSON path segment. It can be renamed only by publishing a new version.
- `label {locale}`, `description` (also sent to the LLM schema), `required`, `readOnly`, `section`, `order`, `visibleWhen` (equality on a sibling field).
- `extraction {labels{lang:[...]}, pattern, llmHint}`.
- `indexed`, `sensitive`, `deprecated`.
- `list`: the field is a list of values of its kind (`orderReferences`), each with its own envelope; `array` is for rows of several fields.
- `provenance` (default true): false for a member the canonical tree stores as a bare value without the `FieldValue` envelope. Only today's records have such members (version 1 trees: the VAT summary, charges, a line's number and printed unit); they are part of the template so its paths are exactly the stored tree's, are `readOnly`, and no correction addresses them.
- validation: `pattern`, `min`, `max`, `maxLength`; on `array` fields also `maxItems`, the row cap of that repeating group (at most 20,000).

**Roles** bind well-known semantics to paths:

- `documentNumber`, `documentDate`, `dueDate`, `currency`, `total`, `net`, `tax`
- `validFrom`, `validUntil` (price lists and other documents with a validity period)
- `supplierParty`, `customerParty`
- `lineItems`, `lineNet`, `lineQuantity`, `lineUnitPrice`, `lineSku`
- `taxBreakdown`
- `duplicateKey` (a list)

Every role is optional. A type without `total` (a price list) simply leaves `TotalGross` empty, and rules that need the role are not offered for it.

Roles feed the denormalised columns, duplicate detection, the inbox, the coded invoice rules and the `roles` object of the output values view. Output mappings can therefore work across several types without knowing each type's keys.

**Other blocks:**

- `rules`: a list of `{id, kind, params, severity, message{locale}, appliesWhen?}` (section 11).
- `processing`: steps per phase (section 10), plus the `review` block:
  - `autoApprove`, `minConfidence`;
  - `priority` (`normal | high | urgent`) and `dueBy` (a date role, `validFrom` or `validUntil`), which set the ReviewTask's `Priority` and `SlaDueAt` (section 6.2);
  - `requirePassedRules` (rule ids that must have run and passed), `blockOnWarnings` (rule ids whose warnings stop auto-approval, section 11);
  - `lines {requireSource, requireLocated, maxLowConfidenceRows}` for the rows of the `lineItems` role.
  All of these keys are part of the meta-schema.
- `classification`: `{description, keywords{lang:[...]}, labels{lang:[...]}, dateLabels{lang:[...]}, negatives{lang:[...]}}`. These are the fuzzy, weighted keyword signals of today's `KeywordMatcher`: titles, document-number labels, date labels and negative phrases, with the weights it gives each kind. Strict identification rules are not part of the type definition: they belong to a supplier layout, or to the tenant fallback list (section 8.5), so a type version does not change when a supplier's design does.
- `llm`: `{guidance, examples: "template"|[...], adapter?}`.
- `ui`: `{sections:[{id, label}]}`.
- Platform templates only: `legacyKey`, the `DocumentType` name existing tenants adopt the template under (21, step 2).

### 5.3 Example: Acme's credit note (fork of std.credit-note v1)

The example is abbreviated: inherited std fields are elided with `"..."` comments in prose. The stored version is fully materialised, with `base` kept for upgrade diffs.

```json
{
  "key": "CreditNote",
  "version": 3,
  "base": { "template": "std.credit-note", "version": 1, "mode": "copy" },
  "name": { "en": "Credit note", "de": "Gutschrift" },
  "roles": {
    "documentNumber": "invoiceNumber", "documentDate": "issueDate", "currency": "currency",
    "total": "totalGross", "net": "subtotalNet", "tax": "totalTax",
    "supplierParty": "supplier", "customerParty": "customer", "lineItems": "lines",
    "duplicateKey": ["$supplierId", "invoiceNumber"]
  },
  "ui": { "sections": [ { "id": "document" }, { "id": "supplier" }, { "id": "totals" }, { "id": "lines" } ] },
  "fields": [
    { "id": "fld_inv_no", "key": "invoiceNumber", "kind": "identifier", "required": true, "section": "document", "indexed": true,
      "label": { "en": "Credit note no.", "de": "Gutschrift-Nr." },
      "extraction": { "labels": { "de": ["Gutschrift-Nr.", "Gutschriftsnummer"], "en": ["Credit note no."] } } },
    { "id": "fld_issue", "key": "issueDate", "kind": "date", "required": true, "section": "document" },
    { "id": "fld_orig", "key": "originalInvoiceReference", "kind": "identifier", "required": true, "section": "document" },
    { "id": "fld_cur", "key": "currency", "kind": "currency", "required": true, "section": "totals" },
    { "id": "fld_sup", "key": "supplier", "kind": "party", "required": true, "section": "supplier" },
    { "id": "fld_gross", "key": "totalGross", "kind": "money", "currencyFrom": "currency", "required": true, "section": "totals" },
    { "id": "fld_c_scr", "key": "supplierCustomerRef", "kind": "identifier", "indexed": true, "section": "document",
      "label": { "en": "Our customer no. at supplier", "de": "Unsere Kundennummer beim Lieferanten" },
      "description": "The account number the supplier assigned to us, printed as Kunden-Nr. or Kd.-Nr.",
      "extraction": { "labels": { "de": ["Kunden-Nr.", "Kd.-Nr.", "Kundennummer"], "en": ["Customer No.", "Account No."] },
                      "pattern": "^[0-9]{3}\\s?[0-9]{6}$" } },
    { "id": "fld_c_reason", "key": "creditReason", "kind": "enum", "section": "document",
      "options": [ { "code": "Return", "label": { "de": "Retoure" } }, { "code": "PriceCorrection", "label": { "de": "Preiskorrektur" } },
                   { "code": "Bonus" }, { "code": "Other" } ] },
    { "id": "fld_lines", "key": "lines", "kind": "array", "anchor": "description", "section": "lines",
      "item": { "fields": [
        { "id": "fld_l_desc", "key": "description", "kind": "text", "required": true },
        { "id": "fld_l_qty", "key": "quantity", "kind": "decimal" },
        { "id": "fld_l_unit", "key": "unit", "kind": "unit", "codeList": "uom" },
        { "id": "fld_l_price", "key": "unitPrice", "kind": "money", "currencyFrom": "currency" },
        { "id": "fld_l_net", "key": "lineNet", "kind": "money", "currencyFrom": "currency" },
        { "id": "fld_l_sku", "key": "supplierSku", "kind": "string" },
        { "id": "fld_l_ext", "key": "ext", "kind": "bag" }
      ] } },
    { "id": "fld_ext", "key": "ext", "kind": "bag", "label": { "en": "Supplier-specific attributes" } }
  ],
  "rules": [
    { "id": "r_totals", "kind": "invoice.totals", "params": { "tolerance": 0.02 } },
    { "id": "r_lines", "kind": "invoice.lineArithmetic", "params": { "tolerance": 0.02 } },
    { "id": "r_tax", "kind": "invoice.taxBreakdown" },
    { "id": "r_dates", "kind": "datePlausibility", "params": { "path": "issueDate", "maxPastDays": 400, "maxFutureDays": 7 } },
    { "id": "r_dup", "kind": "duplicate", "severity": "blocking" },
    { "id": "r_scr", "kind": "requiredIf", "params": { "path": "supplierCustomerRef", "when": "$exists(supplier.attributes.erpVendorId)" },
      "severity": "warning", "message": { "en": "Customer number at supplier missing", "de": "Kundennummer beim Lieferanten fehlt" } }
  ],
  "processing": {
    "split": { "mode": "auto" },
    "classify": { "llmThreshold": 0.75 },
    "extract": [
      { "id": "xml", "kind": "xml-einvoice" },
      { "id": "map", "kind": "supplier-mapping" },
      { "id": "llm", "kind": "llm", "params": { "mode": "fill-missing", "adapter": "invoice_extraction@1", "effort": "medium" } },
      { "id": "defaults", "kind": "defaults" },
      { "id": "uom", "kind": "code-list", "params": { "list": "uom" } }
    ],
    "validate": [ { "id": "schema", "kind": "validate.schema" }, { "id": "rules", "kind": "validate.rules" } ],
    "review": { "autoApprove": true, "minConfidence": 0.9 }
  },
  "classification": {
    "description": "A supplier document that reduces or reverses an earlier invoice.",
    "keywords": { "de": ["Gutschrift", "Rechnungskorrektur"], "en": ["Credit note", "Credit memo"] },
    "negatives": { "de": ["Lieferschein"] }
  },
  "llm": { "guidance": "Amounts may be printed positive; keep the printed sign.", "examples": "template" }
}
```

Correction (P1): the coded invoice kinds take no parameters (`InvoiceRuleAdapter` has none to pass on to the coded rules), so a publish refuses `"params": { "tolerance": 0.02 }` on `invoice.totals` and `invoice.lineArithmetic`; the tolerance stays the coded one until those rules take parameters. `requiredIf` (`r_scr`) is a P5 kind, so the example as a whole publishes only from P5.

The std template keeps the key `invoiceNumber` for the credit-note number. This keeps existing CreditNote rows, `Correction.FieldPath` values and `invoice_extraction@1` compatible. The label says "Credit note no.", and output mappings normally use `roles.documentNumber`, so the legacy key does not leak into integrations.

### 5.4 Example: a new type (return slip)

```json
{
  "key": "return-slip", "version": 1, "base": { "template": "std.generic", "version": 1, "mode": "copy" },
  "name": { "en": "Return slip", "de": "Retourenschein" },
  "roles": { "documentNumber": "returnNumber", "documentDate": "returnDate", "supplierParty": "supplier",
             "lineItems": "items", "duplicateKey": ["$supplierId", "returnNumber"] },
  "fields": [
    { "id": "fld_rn", "key": "returnNumber", "kind": "identifier", "required": true, "extraction": { "labels": { "de": ["Retouren-Nr.", "RMA"] } } },
    { "id": "fld_rd", "key": "returnDate", "kind": "date", "required": true },
    { "id": "fld_sup", "key": "supplier", "kind": "party" },
    { "id": "fld_dn", "key": "deliveryNoteReference", "kind": "identifier" },
    { "id": "fld_items", "key": "items", "kind": "array", "anchor": "description", "item": { "fields": [
      { "id": "fld_i_sku", "key": "supplierSku", "kind": "identifier" },
      { "id": "fld_i_desc", "key": "description", "kind": "text", "required": true },
      { "id": "fld_i_qty", "key": "quantity", "kind": "decimal", "min": 0 },
      { "id": "fld_i_reason", "key": "reason", "kind": "enum", "options": [ { "code": "Damaged" }, { "code": "Wrong" }, { "code": "Surplus" } ] }
    ] } }
  ],
  "rules": [ { "id": "r_dup", "kind": "duplicate" }, { "id": "r_qty", "kind": "range", "params": { "path": "items[*].quantity", "min": 0.001 } } ],
  "processing": { "extract": [ { "id": "map", "kind": "supplier-mapping" }, { "id": "llm", "kind": "llm", "params": { "mode": "fill-missing" } } ],
                  "review": { "autoApprove": false } },
  "classification": { "description": "Goods returned to a supplier", "keywords": { "de": ["Retourenschein", "Rücksendung"] } }
}
```

### 5.5 Compiled artifacts

The new `DefinitionCompiler` in `src/DocProc.Definitions` compiles each published version into four artifacts, stored on `DocumentTypeVersion`:

1. **`CompiledJsonSchema`**: JSON Schema 2020-12 over the values view. It is used by the server (JsonSchema.Net) and by the client (Ajv 2020) for instant hints.
   - Licence check (P1): JsonSchema.Net is MIT up to 8.0.5. From 9.0 its binary releases ship under the json-everything "Open Source Maintenance Fee" EULA (`OSMFEULA.txt` in the package): the source stays MIT, but users with revenue-generating use above US$10,000 a year owe a monthly fee for the published binaries (self-compiled binaries are exempt). The build pins **8.0.5** (`Directory.Packages.props`); moving to 9.x needs the fee or a self-compiled package. The fallback, Corvus.Json.Validator, is Apache-2.0 but pulls Roslyn in to compile validators at run time, so it was not taken.
2. **`CompiledLlmSchema`**: built by reusing the `PortableSchemaGenerator.Transform` rules (all properties required, nullable type arrays, `additionalProperties:false`, composites inlined with no `$ref`, `description` copied, the reserved `unreadableRegions` appended). It is checked by `SchemaPortability.EnsurePortable`.
   - Name: `doc_<key>`.
   - Version: the first 16 hex characters of the SHA-256 of the compiled JSON (`LlmSchemaHash`).
   - For invoice-family types the compiled schema is `invoice_extraction@1` unchanged, plus a nullable `x_custom` object that holds only the tenant's added fields, and only when there are any.
3. **`FormSpecJson`**: sections and a tree of FieldSpec entries (path, kind, label, options, required, currencyPath, visibleWhen). This is what the review form renders.
4. **Field descriptor index**: path to `{fieldId, kind, isList, children, currencyFrom, required, sensitive}`. It is used by the patcher, the role projector, the rule engine, the mapping engine and the values-view builder.

### 5.6 Canonical storage, custom fields and the values view

- `Documents.CanonicalJson` stays a FieldValue tree. Generic leaves are `FieldValue<JsonNode>`, which keeps the same envelope.
- `CanonicalSchemaVersion` keeps its column with new meaning: 1 = legacy, written by today's records; 2 = definition-driven. Version 2 is a superset of version 1, so version 1 rows deserialise unchanged.
- The new column `DocumentTypeVersionId` pins the definition.
- **Custom fields** are declared on the tenant type, like `supplierCustomerRef` above. They are first-class paths: validated, indexed, shown in the generated form, and addressable by rules and outputs.
- **The `ext` bag** holds one-off supplier attributes that need no type change, such as a store number printed only by one supplier. A bag is declared at header level and at line level. Its values are scalar FieldValues, written by supplier mappings that declare the key with a kind and label (section 8). If two suppliers declare the same ext key with different kinds, publish raises a warning.
- **Round-trip safety.** In P0, `Invoice`, `InvoiceLine`, `Party`, `PurchaseOrder` and `DeliveryNote` get `[JsonExtensionData] Dictionary<string, JsonElement>? Extra` (so do the PurchaseOrder and DeliveryNote lines). Before P0, `CanonicalDocumentSerializer` silently dropped properties that are not on the record. With this change, custom fields survive every typed round trip. (Correction: the first draft said `JsonObject? Extra`; System.Text.Json writes a `JsonObject` extension bag as a bare nested object, which is invalid JSON, so the bag is a dictionary of `JsonElement`.)
- **Typed projection.** Coded invoice rules and the e-invoice mapper keep using `Invoice` via `InvoiceProjection`, which is `CanonicalDocumentSerializer.Deserialize<Invoice>` over the same tree. This works because std paths are identical.
- **Values view.** `IDocumentDataViewBuilder` (in `src/DocProc.Application/Outputs`) produces plain values and is cached per `(DocumentId, Version)`:
  - identifier becomes the verbatim string, money becomes `{amount, currency}`, date becomes `yyyy-MM-dd`, enum becomes its code, and Missing becomes `null`;
  - `roles` resolves the role bindings;
  - `source` holds the source facts of the ingestion (section 7.1), including `receivedAt` (UTC) and `receivedDate` (`yyyy-MM-dd` in the tenant time zone, `TenantSettings.TimeZone`). The `defaults` step and rule params address them as `$source.<key>`, for example `$source.receivedDate`;
  - `meta.supersedes` names the earlier document this one replaces, when a reviewer resolved a `REVISION_OF` that way (section 11);
  - `$field(path)` exposes `{status, confidence, method, normalized}`.
  The view is the input to JSON Schema validation, generic rules and output mappings.

```json
{
  "doc": { "invoiceNumber": "GS-2026-0042", "issueDate": "2026-10-28", "currency": "EUR",
           "totalGross": { "amount": -119.00, "currency": "EUR" }, "originalInvoiceReference": "RE-2026-9917",
           "supplierCustomerRef": "123456789", "creditReason": "Return", "ext": { "storeNo": "0417" },
           "lines": [ { "description": "Kaffee Crema 1kg", "quantity": -2, "unit": "H87",
                        "unitPrice": { "amount": 50.00, "currency": "EUR" }, "lineNet": { "amount": -100.00, "currency": "EUR" },
                        "supplierSku": "4711", "ext": {} } ] },
  "roles": { "documentNumber": "GS-2026-0042", "documentDate": "2026-10-28", "currency": "EUR",
             "total": { "amount": -119.00, "currency": "EUR" } },
  "meta": { "documentId": "6c1f...", "version": 7, "typeKey": "CreditNote", "typeVersion": 3, "status": "Approved",
            "approvedAt": "2026-10-29T09:12:00Z", "approvedBy": "j.weber", "event": "document.approved", "flags": [], "supersedes": null },
  "supplier": { "id": "9b1e...", "name": "Nordwind Großhandel GmbH", "vatId": "DE811111111", "group": "Nordwind Gruppe",
                "attributes": { "erpVendorId": "V-10042", "defaultCostCentre": "4711" } },
  "entity": { "id": "e1...", "code": "1000", "name": "Acme GmbH" },
  "source": { "channel": "AP mailbox", "connector": "Mailbox", "mailbox": "nordwind@tenant-a.local",
              "sender": "rechnungsversand@nordwind.example", "folderPath": null, "fileName": "GS-2026-0042.pdf", "subject": "Gutschrift GS-2026-0042",
              "receivedAt": "2026-10-29T09:09:51Z", "receivedDate": "2026-10-29" },
  "links": { "original": "https://.../api/v1/documents/6c1f.../original", "api": "https://.../api/v1/documents/6c1f..." }
}
```

### 5.7 Publish-time checks and versioning

`POST /document-types/{key}/versions/{v}:publish` runs these checks:

- the meta-schema; key syntax `^[A-Za-z][A-Za-z0-9_.-]{0,63}$`; at most one binding per role, and each role bound to a field of a fitting kind;
- rule kind parameters against each kind's `ParamsSchema`. A rule kind that is not registered in this deployment fails the publish with "rule kind `tableComplete` is not available yet"; there is no "compile now, run later". Platform templates reference only kinds registered in the phase that ships them, which the template test asserts (section 11 lists the phase of each kind);
- every field named in `roles.duplicateKey`, other than the one bound to `documentNumber`, is `indexed`, because the duplicate check reads those components from `DocumentFieldIndex` (section 11);
- `SchemaPortability` on the compiled LLM schema, plus size caps (at most 300 fields, depth 6, 200 enum values, 200 rules and 8 components of `roles.duplicateKey`; every rule runs on every validation of a document and each duplicate-key component is one more lookup in `DocumentFieldIndex`, so these two bound the cost per document). The caps are set after checking current provider strict-mode limits in the P1 spike;
- regex patterns compiled with `RegexOptions.NonBacktracking` and a timeout;
- tenant text (descriptions, guidance) length-limited;
- **dependents**: every supplier layout, output mapping, binding, rule and indexed key that references a field. A field that was removed, or whose kind changed, blocks the publish. A renamed field, detected through its stable `id`, triggers the offer "rewrite 3 dependent mappings into new draft versions";
- the **golden-document regression suite** of the type (section 15.3).

Published versions are immutable. Documents pin `DocumentTypeVersionId`. A bulk "upgrade open documents to vN" action re-runs Extract or Validate for selected documents. Approved documents are never changed.

### 5.8 Example: price list and daily specials (std.price-list, std.daily-specials)

Follow-up F1: "I could get a full price list as a document, or today's specials price list that I could convert." A price list is the opposite of an invoice: a small header (supplier, validity, currency) and a table of hundreds or thousands of rows over many pages, with no totals to check.

**One template or two.** Two templates, `std.daily-specials` being a fork of `std.price-list` with the same field tree. Reasons:

- Output bindings, review policy, identification rules and the inbox are all scoped by type key (sections 6.7, 8.5). Specials usually go to a different receiver (POS or web shop promotions) than the full list (ERP article master). With two keys a binding says so directly, without a `Condition` on a field.
- The validation differs: specials are valid for one day and are worthless once that day has passed, while a full list may be open-ended and is checked against the previous list.
- The urgency differs: a specials list must be approved the same morning, so its ReviewTasks get a high priority and a due time.
- The field tree is identical, so a supplier's column mapping, the table tool configuration and output mappings (which read `roles` and `doc.lines`) carry over between the two types.

The alternative, one type with a `listKind` field, was rejected: every rule and binding would need `appliesWhen`/`Condition` on that field, and the classifier would be deciding a field value rather than a type. Types are data, so a tenant that prefers one type can archive `daily-specials` and add the field.

**Template versions and phases.** The rule kinds that stand in for a totals check arrive in P5, while the templates ship in P1. Publishing a type that references an unregistered rule kind fails (5.7), so the templates ship in two versions:

- **v1 (P1)** has the same fields, roles, classification and processing steps as below, but only the rules whose kinds exist in P1 (`r_validity`, `r_from`, `r_price_pos`, `r_pack`, `r_unit`, `r_dup`; for specials `r_validity`, `r_not_expired`, `r_price_pos`, `r_pack`, `r_unit`, `r_dup`) and `"review": { "autoApprove": false }`. This is how "always reviewed" is enforced from P1 to P4: the gate's first condition (section 11) fails, so every list goes to review whatever its other rules say.
- **v2 (P5)**, shown below, adds `r_table`, `r_count`, `r_sku_unique`, `r_tiers` and `r_delta` (for specials `r_single_day` and `r_below_prev` instead of `r_delta`) and the full review block. It changes no field, so it is a non-breaking template version and adopted types move to it automatically, with one exception: an automatic template upgrade never switches `review.autoApprove` on. The upgrade report offers it, and a `ConfigAdmin` turns it on in the type's Processing tab. A tenant that copied v1 gets the usual three-way diff.

**`std.price-list` v2** (adopted by Acme as `price-list`; abbreviated only where noted). Correction (P1): the line roles are full paths, as in section 5.2 and the invoice template (`"lineSku": "lines[*].supplierSku"`, `"lineUnitPrice": "lines[*].price"`); the block below writes them relative to the row, which the publish checks refuse. The shipped templates use the full paths.

```json
{
  "key": "price-list",
  "version": 2,
  "base": { "template": "std.price-list", "version": 2, "mode": "adopt" },
  "name": { "en": "Price list", "de": "Preisliste" },
  "roles": {
    "documentNumber": "priceListNumber", "documentDate": "issueDate", "currency": "currency",
    "validFrom": "validFrom", "validUntil": "validUntil",
    "supplierParty": "supplier", "customerParty": "customer", "lineItems": "lines", "lineSku": "supplierSku",
    "lineUnitPrice": "price",
    "duplicateKey": ["$supplierId", "validFrom", "listScope", "priceListNumber"]
  },
  "ui": { "sections": [ { "id": "list" }, { "id": "supplier" }, { "id": "lines" } ] },
  "fields": [
    { "id": "fld_pl_no", "key": "priceListNumber", "kind": "identifier", "section": "list", "indexed": true,
      "label": { "en": "Price list no.", "de": "Preislisten-Nr." },
      "extraction": { "labels": { "de": ["Preisliste Nr.", "Preislisten-Nr."], "en": ["Price list no."] } } },
    { "id": "fld_pl_title", "key": "title", "kind": "string", "section": "list" },
    { "id": "fld_pl_issue", "key": "issueDate", "kind": "date", "section": "list" },
    { "id": "fld_pl_from", "key": "validFrom", "kind": "date", "required": true, "section": "list", "indexed": true,
      "label": { "en": "Valid from", "de": "Gültig ab" },
      "extraction": { "labels": { "de": ["gültig ab", "Gültig vom", "Preise ab"], "en": ["valid from", "effective"] } } },
    { "id": "fld_pl_until", "key": "validUntil", "kind": "date", "section": "list",
      "label": { "en": "Valid until", "de": "Gültig bis" }, "description": "Empty means valid until replaced." },
    { "id": "fld_pl_scope", "key": "listScope", "kind": "enum", "section": "list", "indexed": true,
      "options": [ { "code": "Full", "label": { "de": "Gesamtliste" } }, { "code": "Changes", "label": { "de": "Änderungsliste" } } ],
      "description": "Full replaces every price of the supplier; Changes updates only the listed articles." },
    { "id": "fld_pl_cur", "key": "currency", "kind": "currency", "required": true, "section": "list" },
    { "id": "fld_pl_basis", "key": "priceBasis", "kind": "enum", "section": "list",
      "options": [ { "code": "Net", "label": { "de": "netto" } }, { "code": "Gross", "label": { "de": "brutto" } } ] },
    { "id": "fld_pl_count", "key": "declaredLineCount", "kind": "integer", "section": "list",
      "description": "Number of articles printed on the list, if any (for example 'Anzahl Artikel: 412'). Used as a control figure.",
      "extraction": { "labels": { "de": ["Anzahl Artikel", "Positionen"], "en": ["Number of items"] } } },
    { "id": "fld_sup", "key": "supplier", "kind": "party", "required": true, "section": "supplier" },
    { "id": "fld_cust", "key": "customer", "kind": "party", "section": "supplier" },
    { "id": "fld_c_scr", "key": "supplierCustomerRef", "kind": "identifier", "section": "supplier" },
    { "id": "fld_lines", "key": "lines", "kind": "array", "anchor": "supplierSku", "section": "lines", "maxItems": 10000,
      "item": { "fields": [
        { "id": "fld_l_sku", "key": "supplierSku", "kind": "identifier", "required": true,
          "label": { "en": "Article no.", "de": "Art.-Nr." } },
        { "id": "fld_l_gtin", "key": "gtin", "kind": "identifier", "pattern": "^[0-9]{8}([0-9]{4,6})?$" },
        { "id": "fld_l_desc", "key": "description", "kind": "text", "required": true },
        { "id": "fld_l_cat", "key": "category", "kind": "string",
          "description": "Group heading the row is printed under, for example 'Rotweine'." },
        { "id": "fld_l_unit", "key": "unit", "kind": "unit", "codeList": "uom",
          "label": { "en": "Order unit", "de": "Bestelleinheit" } },
        { "id": "fld_l_pack", "key": "packSize", "kind": "decimal", "min": 0,
          "label": { "en": "Pack size", "de": "Inhalt" }, "description": "Number of content units per order unit, for example 6 bottles per case." },
        { "id": "fld_l_packu", "key": "packUnit", "kind": "unit", "codeList": "uom" },
        { "id": "fld_l_packd", "key": "packDescription", "kind": "string", "description": "As printed, for example '6 x 0,75 l'." },
        { "id": "fld_l_pq", "key": "priceQuantity", "kind": "decimal", "min": 0,
          "description": "The price applies per this many units (for example 'per 100'). Empty means 1." },
        { "id": "fld_l_price", "key": "price", "kind": "money", "currencyFrom": "currency", "required": true },
        { "id": "fld_l_prev", "key": "previousPrice", "kind": "money", "currencyFrom": "currency",
          "description": "Printed old price, for example 'statt 12,99'." },
        { "id": "fld_l_tax", "key": "taxRate", "kind": "percent" },
        { "id": "fld_l_moq", "key": "minOrderQuantity", "kind": "decimal", "min": 0 },
        { "id": "fld_l_tiers", "key": "tiers", "kind": "array", "anchor": "minQuantity",
          "item": { "fields": [
            { "id": "fld_t_qty", "key": "minQuantity", "kind": "decimal", "required": true, "min": 0 },
            { "id": "fld_t_price", "key": "price", "kind": "money", "currencyFrom": "currency", "required": true }
          ] } },
        { "id": "fld_l_from", "key": "lineValidFrom", "kind": "date" },
        { "id": "fld_l_until", "key": "lineValidUntil", "kind": "date" },
        { "id": "fld_l_status", "key": "status", "kind": "enum",
          "options": [ { "code": "New" }, { "code": "Changed" }, { "code": "Discontinued", "label": { "de": "Auslauf" } }, { "code": "Unchanged" } ] },
        { "id": "fld_l_ext", "key": "ext", "kind": "bag" }
      ] } },
    { "id": "fld_ext", "key": "ext", "kind": "bag" }
  ],
  "rules": [
    { "id": "r_validity", "kind": "dateOrder", "params": { "before": "validFrom", "after": "validUntil", "allowEqual": true }, "severity": "blocking" },
    { "id": "r_from", "kind": "datePlausibility", "params": { "path": "validFrom", "maxPastDays": 60, "maxFutureDays": 180 } },
    { "id": "r_table", "kind": "tableComplete", "params": { "path": "lines", "maxRejectedRows": 0, "everyPageInRange": true }, "severity": "blocking" },
    { "id": "r_count", "kind": "expression", "appliesWhen": "$exists(doc.declaredLineCount)",
      "params": { "predicate": "$count(doc.lines) = doc.declaredLineCount" }, "severity": "blocking",
      "message": { "en": "Row count differs from the count printed on the list", "de": "Anzahl Zeilen weicht von der gedruckten Anzahl ab" } },
    { "id": "r_sku_unique", "kind": "unique", "params": { "path": "lines[*].supplierSku", "scopeBy": ["lineValidFrom"] }, "severity": "blocking" },
    { "id": "r_price_pos", "kind": "range", "params": { "path": "lines[*].price.amount", "min": 0.0001 }, "severity": "blocking" },
    { "id": "r_pack", "kind": "range", "params": { "path": "lines[*].packSize", "min": 0.0001 } },
    { "id": "r_unit", "kind": "lookupExists", "params": { "path": "lines[*].unit", "table": "uom" } },
    { "id": "r_tiers", "kind": "ordered", "params": { "path": "lines[*].tiers", "by": "minQuantity", "strict": true,
                                                       "then": { "path": "price.amount", "order": "nonIncreasing" } } },
    { "id": "r_delta", "kind": "deltaToPrevious", "params": { "path": "lines[*].price.amount", "joinOn": "supplierSku",
                                                              "maxChangePercent": 25, "maxChangedRowsPercent": 30 },
      "severity": "warning", "message": { "en": "Unusual price change against the last approved list" } },
    { "id": "r_dup", "kind": "duplicate", "params": { "onMatch": "revision" }, "severity": "warning",
      "message": { "en": "A list with the same validity, scope and number exists", "de": "Eine Liste mit gleicher Gültigkeit, Art und Nummer existiert" } }
  ],
  "processing": {
    "split": { "mode": "never" },
    "extract": [
      { "id": "map", "kind": "supplier-mapping" },
      { "id": "llm", "kind": "llm", "params": { "mode": "fill-missing", "exclude": ["lines"], "effort": "low" } },
      { "id": "defaults", "kind": "defaults", "params": { "values": { "currency": { "fromSupplierSetting": "defaults.currency" } } } },
      { "id": "uom", "kind": "code-list", "params": { "list": "uom" } }
    ],
    "validate": [ { "id": "schema", "kind": "validate.schema" }, { "id": "rules", "kind": "validate.rules" } ],
    "review": { "autoApprove": true, "minConfidence": 0.9,
                "requirePassedRules": ["r_table", "r_sku_unique", "r_price_pos"],
                "blockOnWarnings": ["r_delta", "r_dup"],
                "lines": { "requireSource": ["Rule", "Structured"], "maxLowConfidenceRows": 0 } }
  },
  "classification": {
    "description": "A supplier's list of article prices valid from a date, usually a long table without totals.",
    "keywords": { "de": ["Preisliste", "Preisänderung", "Gültig ab"], "en": ["Price list", "Price change", "Valid from"] },
    "negatives": { "de": ["Rechnung", "Gutschrift", "Tagesangebot"], "en": ["Invoice", "Daily specials"] }
  },
  "llm": { "guidance": "Extract the header only. Rows are read by the table mapping.", "examples": "template" }
}
```

How this type behaves differently from an invoice:

- **No totals.** No `total`, `net` or `tax` role is bound, so `invoice.totals` and `sumEquals` are not offered, and the RoleProjector leaves `TotalGross` empty (it fills `ValidFrom`, `ValidUntil` and `LineCount` instead, section 6.2). Correctness is established by other means: the table is complete (`tableComplete`: every row band between the header and `stopAt` on every page in the table's page range parsed into a row, and, with `everyPageInRange`, no page in that range yielded zero rows; such a page is then a blocking `TABLE_PAGE_NO_ROWS`), the printed row count when there is one, unique article numbers, positive prices, ordered tier breaks, and the price movement against the last approved list of the same supplier (`deltaToPrevious`, which joins on `supplierSku` through `SupplierHints.LastApproved`).
- **Rows come from the table mapping, not from the LLM.** The `llm` step excludes `lines`: an LLM call over 30 pages of rows is slow, costly and cannot be checked row by row. Rows come from the supplier layout's `column-table` (section 8.6) or, for a first document, from the review table tool (section 12.4). An optional P5 step parameter `{"lines": "perPage"}` lets the LLM read rows page by page under `processing.llm.maxCostPerDocument`, for suppliers whose lists change design every time; documents whose rows came from the LLM never auto-approve (`lines.requireSource`).
- **Auto-approve for a type without totals** (section 11) needs everything in the generic gate plus the type's `requirePassedRules` (those rules must have run and passed, not been skipped) and every row sourced by `Rule` or `Structured` with no low-confidence row. `r_delta` has severity warning, and the type lists it in `review.blockOnWarnings`: a list with unusual price changes (one article up 40 %, or more than 30 % of the rows changed) does not auto-approve and is never sent to the ERP unseen. It goes to review with the changed rows filtered, where a reviewer can still approve it, because a warning does not block a human approval. A normal yearly 3 % increase raises no flag and auto-approves. Warnings of rules not listed there (for example `r_pack`) are shown but do not stop auto-approval.
- **Size.** `maxItems` raises the array cap for this type (the compile caps in 5.7 limit fields, not rows). It is 10,000 rows until chunked delivery exists (P6, section 13.10): 10,000 rows with 40-character texts and two tier prices render to about 4.5 MB, under the 5 MB render cap of 13.1, so every list that passes validation can also be delivered. A longer list fails `validate.schema` with a blocking `FIELD_INVALID {path: "lines", keyword: "maxItems"}` and goes to review instead of failing at delivery. The template version that ships with chunked delivery raises the cap to 20,000. Line fields are not `indexed` (indexing 2,000 rows per list would swamp `DocumentFieldIndex`); the header fields are. The review grid is virtualised (12.2), and the values view is built once per version and cached.
- **Duplicates and corrected lists.** The key is supplier, `validFrom`, `listScope` and `priceListNumber`. `priceListNumber` is read from the denormalised `DocumentNumberNormalized`; `validFrom` and `listScope` are `indexed`, so the check can read them from `DocumentFieldIndex`. A list without a printed number has a null component, which matches only another null (section 11). A second document with the same key is usually a corrected list or, for specials, a second list on the same day, not a duplicate, so `r_dup` uses `onMatch: "revision"`: it raises the warning `REVISION_OF {documentId}`, which `blockOnWarnings` turns into a review. The reviewer resolves it as "replaces the earlier list", "an additional list" or "a duplicate (reject)" (section 11). A byte-identical file never gets this far, because ingest de-duplicates by SHA-256 (6.3).
- **Split.** `never`: a 30-page list is one document, even though page 7 starts with a group heading that looks like a new document to the heuristic splitter. Split runs before Classify, so for a list whose type is not pinned, Split chooses its policy like this (`SplitHandler`):
  1. A type pinned before Split (source rule `PinTypeKey`, channel default, an authorised hint, or a single allowed type under `fixDocumentType`) gives its `split.mode`.
  2. Otherwise the **split prefilter** evaluates identification rules on the whole item, before any cut: the layouts of the supplier resolved at ingest, or, when the supplier is unknown or has no layouts and they are not required, the tenant fallback rules. If exactly one layout (or rules of exactly one type) holds, that type's `split.mode` applies. The prefilter decides nothing else: it writes no layout and no type, and Classify evaluates again on the resulting document.
  3. Otherwise the tenant default (`auto`) applies, and the heuristic splitter runs.

  A price list in a design nobody has recognised yet, from a source without a pinned type, can therefore still be cut by the heuristic splitter; the reviewer then joins the parts back into one document in review (`:join`, 12.7). For folders and mailboxes that only carry price lists, the reliable setting is a source rule with `PinTypeKey: price-list` (7.5).

**`std.daily-specials` v2** differs from `std.price-list` v2 in the `key`, `name`, `rules`, `processing`, `classification` and `llm` blocks, shown below. Each block shown **replaces** the parent's block as a whole; nothing is merged, and the stored template is fully materialised, so nothing is inherited at run time. `fields`, `roles` (including `duplicateKey`) and `ui` are identical copies of `std.price-list` v2. Of the price-list rules, the validity order, row count, unique article numbers, positive prices, pack size, unit and tier-order rules apply to specials too and are repeated here; `r_from` is replaced by `r_not_expired`, and `r_delta` is dropped (specials are compared with the printed old price, not with the last list).

```json
{
  "key": "daily-specials",
  "version": 2,
  "base": { "template": "std.daily-specials", "version": 2, "mode": "adopt" },
  "name": { "en": "Daily specials", "de": "Tagesangebote" },
  "rules": [
    { "id": "r_validity", "kind": "dateOrder", "params": { "before": "validFrom", "after": "validUntil", "allowEqual": true }, "severity": "blocking" },
    { "id": "r_single_day", "kind": "compare", "params": { "left": "validUntil", "op": "==", "right": "validFrom" },
      "severity": "warning", "message": { "en": "Specials valid for more than one day", "de": "Angebot gilt länger als einen Tag" } },
    { "id": "r_not_expired", "kind": "datePlausibility", "params": { "path": "validUntil", "maxPastDays": 0, "relativeTo": "now" },
      "severity": "blocking", "message": { "en": "These specials have already expired", "de": "Angebot ist bereits abgelaufen" } },
    { "id": "r_table", "kind": "tableComplete", "params": { "path": "lines", "maxRejectedRows": 0, "allowUnreported": true }, "severity": "blocking" },
    { "id": "r_count", "kind": "expression", "appliesWhen": "$exists(doc.declaredLineCount)",
      "params": { "predicate": "$count(doc.lines) = doc.declaredLineCount" }, "severity": "blocking" },
    { "id": "r_sku_unique", "kind": "unique", "params": { "path": "lines[*].supplierSku", "scopeBy": ["lineValidFrom"] }, "severity": "blocking" },
    { "id": "r_price_pos", "kind": "range", "params": { "path": "lines[*].price.amount", "min": 0.0001 }, "severity": "blocking" },
    { "id": "r_pack", "kind": "range", "params": { "path": "lines[*].packSize", "min": 0.0001 } },
    { "id": "r_unit", "kind": "lookupExists", "params": { "path": "lines[*].unit", "table": "uom" } },
    { "id": "r_tiers", "kind": "ordered", "params": { "path": "lines[*].tiers", "by": "minQuantity", "strict": true,
                                                       "then": { "path": "price.amount", "order": "nonIncreasing" } } },
    { "id": "r_below_prev", "kind": "compare", "params": { "left": "lines[*].price.amount", "op": "<", "right": "lines[*].previousPrice.amount",
                                                           "skipWhenMissing": true }, "severity": "warning" },
    { "id": "r_dup", "kind": "duplicate", "params": { "onMatch": "revision" }, "severity": "warning" }
  ],
  "processing": {
    "split": { "mode": "never" },
    "extract": [
      { "id": "map", "kind": "supplier-mapping" },
      { "id": "llm", "kind": "llm", "params": { "mode": "fill-missing", "effort": "low" } },
      { "id": "defaults", "kind": "defaults", "params": { "values": {
          "currency": { "fromSupplierSetting": "defaults.currency" },
          "validFrom": { "from": ["issueDate", "$source.receivedDate"], "mode": "ifMissing" },
          "validUntil": { "from": "validFrom", "mode": "ifMissing" },
          "listScope": { "value": "Changes", "mode": "ifMissing" } } } },
      { "id": "uom", "kind": "code-list", "params": { "list": "uom" } }
    ],
    "validate": [ { "id": "schema", "kind": "validate.schema" }, { "id": "rules", "kind": "validate.rules" } ],
    "review": { "autoApprove": true, "minConfidence": 0.9, "priority": "urgent", "dueBy": "validUntil",
                "requirePassedRules": ["r_table", "r_not_expired", "r_price_pos", "r_sku_unique"],
                "blockOnWarnings": ["r_dup"],
                "lines": { "requireSource": ["Rule", "Structured", "Llm"], "requireLocated": true, "maxLowConfidenceRows": 0 } }
  },
  "classification": {
    "description": "Special prices valid for a single day, usually a short list sent early in the morning.",
    "keywords": { "de": ["Tagesangebot", "Angebot des Tages", "Tagespreise", "heute"], "en": ["Today's specials", "Daily offer"] },
    "negatives": { "de": ["Preisliste 20"] }
  },
  "llm": { "guidance": "Extract the header and every row. Copy prices exactly as printed; leave previousPrice empty unless an old price is printed.", "examples": "template" }
}
```

A specials list is short (one or two pages, 10 to 80 rows), so the `llm` step may read its rows. Rows read by the LLM count towards auto-approval only when every value of the row was located on the page by `ProvenanceLocator` (`requireLocated`); otherwise the document goes to review. `r_table` sets `allowUnreported` for exactly this case: LLM-read rows have no table report, so the rule passes on that point and the row-level gate (`lines`) decides. When the rows came from a layout's `column-table` or the review table tool, the report exists and is checked as for a full list. A specials list whose rows the LLM read and located therefore auto-approves; one with a single unlocated row does not.

When no date is printed, `validFrom` falls back to `$source.receivedDate`, the date the mail or file was received in the tenant time zone (5.6), and `validUntil` to `validFrom`, so an undated "today's specials" list received at 06:40 on 3 November is valid on 3 November only. `dueBy: "validUntil"` with `priority: "urgent"` sets the ReviewTask's `Priority` to Urgent and its `SlaDueAt` to the end of the `validUntil` day (23:59:59 in the tenant time zone, or the tenant's normal SLA when that is earlier), so the list sorts to the top of the queue. Section 13.10 shows the output side of both types.

## 6. Data model

All new tables implement `ITenantScoped`, get a `HasQueryFilter` (the pattern at `DocProcDbContext.cs:111-132`) and an RLS entry (section 6.10). Tables marked "temporal" use SQL Server temporal tables, as `Documents` and `ExtractorProfiles` already do; their history tables need RLS entries of their own (section 6.10).

### 6.1 Type catalogue

New file `DocumentTypes.cs` in `src/DocProc.Infrastructure/Persistence/Entities` (P1).

**DocumentTypeDefinition**
- `Id`, `TenantId?` (null for a platform template), `Key nvarchar(64)` unique per `(TenantId, Key)`.
- `Name json`, `Description`.
- `Origin {Platform, Adopted, Copied, Custom}`, `BaseDefinitionId?`, `BaseVersion?`.
- `Status {Active, Archived}`, `PublishedVersion int?`.
- `DraftJson json?`, `DraftRevision rowversion` (used for If-Match).
- `CreatedAt/By`, `UpdatedAt`.
- Correction (P1): the draft is a `DocumentTypeVersion` in state `Draft` (the per-version API of section 18 addresses it as `…/versions/{v}`), so the definition row has no `DraftJson`, and the If-Match token is the version's `Revision int`, raised with every saved change, instead of a `rowversion`, which the SQLite development mode does not have. A type has at most one draft.

**DocumentTypeVersion** (temporal; immutable once Published)
- `Id`, `DefinitionId`, `TenantId?`, `Version int`, unique on `(DefinitionId, Version)`.
- `DefinitionJson`, `CompiledJsonSchema`, `CompiledLlmSchema`, `LlmSchemaHash char(16)`, `FormSpecJson`, `DescriptorsJson`, `ContentHash char(64)` (SHA-256 of the JCS-canonicalised JSON).
- `State {Draft, PendingApproval, Published, Deprecated}`, `PublishedAt/By`, `ChangeNote`.

**LookupTable**
- `Id`, `TenantId?`, `Key` (examples: `uom`, `iso4217`, `erp-uom`, `erp-tax-key`), `Description`, `CaseSensitive`, `Source {Manual, Import, Sync}`.
- **LookupEntry**: `TableId`, `From`, `To`, `Labels json?`, `Aliases json?`, `ValidFrom?`, `ValidTo?`.
- Platform code lists (`uom` generated from `UnitOfMeasure.cs:6-49`, `uncl5305`, `iso4217`) have `TenantId` NULL. A tenant copy adds aliases such as `Krt` to `CT`.

### 6.2 Documents and corrections

Changes to `Entities/Documents.cs`. All additions are nullable.

- `Type` changes CLR type from the `DocumentType` enum to `DocumentTypeKey`, a `readonly record struct` wrapping the string. `WellKnownTypeKeys` holds the constants `Unknown`, `Invoice`, `CreditNote` and so on. The column stays `Type nvarchar(64)` (`InitialCreate.cs:409`), so no data change is needed.
- New columns:
  - `DocumentTypeVersionId uniqueidentifier?`
  - `ReceivingEntityId?`
  - `SupplierMatchMethod nvarchar(32)?`: `Hint | SourceRule | Vat | Iban | Domain | Name | Human | Legacy`
  - `SupplierSourceRuleId?`
  - `SupplierLayoutVersionId?`: the layout version whose identification rule matched. The existing `ExtractorProfileId` is left in place, always null, and dropped in P6.
  - `IdentificationRuleVersionId?`: set instead when a tenant fallback identification rule decided the type. It pins the immutable `TypeIdentificationRuleVersion` (section 6.6), as `SupplierLayoutVersionId` pins a layout version.
  - `AppliedConfigJson json?`
  - `IsSandbox bit` (default 0)
  - `ValidFrom date?`, `ValidUntil date?`, `LineCount int?`: denormalised from the roles `validFrom`, `validUntil` and the row count of `lineItems`, for the inbox (price lists show validity and row count instead of a total).
  - `SupersededByDocumentId?`: set when a later document was accepted as its replacement (`REVISION_OF`, section 11). Superseded documents are skipped by `SupplierHints.LastApproved`, `deltaToPrevious` and the duplicate check.
- `TypeSource` gains `Supplier`, `Hint`, `Identification` (a strict identification rule of a layout or of the tenant decided the type) and `Proposed` (a provisional type for a `NEW_LAYOUT` document, chosen by tenant rules, keywords or the LLM only so that Extract can pre-fill against it; it never counts as decided, section 8.4); `Human` is now written. `SourceMethod` gains `Rule` and `Default`.

`AppliedConfigJson` example:

```json
{ "typeVersionId": "…", "typeKey": "CreditNote", "typeVersion": 3,
  "supplierMatch": { "method": "SourceRule", "ruleId": "…", "describe": "From rechnungsversand@nordwind.example (DMARC pass)" },
  "layoutVersionId": "…", "layout": "nordwind-credit-note-2025@4", "layoutScope": "Supplier",
  "identification": { "ruleHash": "c07e19aa", "matched": true, "evidence": [
      { "identifier": "all[0]", "text": "Gutschrift", "page": 1, "bBox": [72, 118, 241, 142] },
      { "identifier": "all[1]", "supplierValue": "vatId", "text": "DE 811 111 111", "page": 1, "bBox": [610, 912, 760, 926] } ] },
  "supplierSettingsHash": "3fa1c09e", "supplierSettings": { "locale": "de-DE", "review": "inherit" },
  "defaultsApplied": ["currency", "ext.costCentre"],
  "pipelineHash": "a91c44d0", "llm": { "schema": "invoice_extraction@1+x.5b1e02aa", "mode": "fill-missing" },
  "resolvedAt": "2026-10-29T09:10:04Z" }
```

When a tenant fallback rule decided the type, `identification` holds `{"scope": "Tenant", "ruleVersionId": "…", "ruleHash": "…", "matched": true, "evidence": [...]}` and the layout fields are absent, so the review header and support can tell which rule version re-typed a document.

**DocumentFieldIndex** (new)
- Columns: `TenantId`, `DocumentId`, `FieldKey nvarchar(200)`, `Ordinal int`, `StringValue nvarchar(400)?` (normalised), `NumberValue decimal(28,8)?`, `DateValue date?`.
- PK `(DocumentId, FieldKey, Ordinal)`; indexes `(TenantId, FieldKey, StringValue)` and `(TenantId, FieldKey, DateValue)`.
- Written by the `RoleProjector` for fields marked `indexed`. It works the same on SQL Server and on SQLite in dev.
- Correction (P1): the Application layer cannot see the entity, so `DocumentFieldIndexWriter` (`src/DocProc.Infrastructure/Persistence`) writes the rows from the stored tree, next to `DocumentRoleColumns` and in the same save (the Extract stage and every correction). `FieldKey` is the field's definition path (`supplierCustomerRef`, `lines[*].supplierSku`), and identifiers are indexed by their normalised form.
- Bounds (`FieldIndexLimits`): a row field or a list has one value per row, so at most the first 100 values of a field and 2,000 rows per document are indexed, every field's first value (`Ordinal` 0) before any field's further ones. A later row's value is neither an inbox column nor found by `?field.`; the inbox column reads `Ordinal` 0 only. Publishing warns when a row field is `indexed` (5.8 advises against it).

**Correction** (extended)
- `Op nvarchar(16)`: `set | append | remove | move | setType | split | join`.
- `SourceJson json?`: `{method, page, bBox, rawText, words:[[page,i]], path, cell, table?}`. `table` holds the table tool's specification and a copy of its report when one Correction writes a whole repeating group (section 12.4), for the audit; the report that rules read is the `DocumentTableReport` below.
  - Correction (P2): a `move` records the field the value came from as `from` (`{"from": "issueDate", …}`), next to the source the reviewer gave; `FieldPath` is the target. The review actions write `setType` with `FieldPath` `type` and `split` / `join` with `FieldPath` `pages` (old and new type or page ranges as their values).
- `DocumentTypeVersionId`.
- `FieldPath` keeps the existing grammar.

**DocumentTableReport** (new; the table report of a repeating group, sections 8.6 and 12.4)
- `TenantId`, `DocumentId`, `Path` (for example `lines`), PK `(DocumentId, Path)`.
- `Origin {Layout, ReviewTool}`, `StepId?` or `CorrectionId?`, `ProducedAtVersion int`, `ReportJson` (per page: rows parsed, rows rejected with their text, header found).
- Written by the `column-table` strategy and by the table tool, replacing the previous report for the path. It is deleted when the group is written by another source (the LLM, or a reviewer replacing all rows), so `tableComplete` never reads a report about rows that are no longer there. It is not part of the `FieldValue` envelope, which stays unchanged.
- Correction (P2): the table is `DocumentTableReports` (migration `TableReports`, a strict tenant table under `PolicyV6`). `ProducedAtVersion` is the document's `Version` the rows were written at; `CorrectionId` is a foreign key to the Correction without cascade (the document's cascade deletes both). Besides the cases above, the report is deleted when the Extract stage writes a new tree, by `:set-type`, and by `:split` and `:join`, which clear the data.

**ReviewTask** (extended)
- `Priority {Normal, High, Urgent}` (default Normal), from the type's `review.priority`.
- The existing `SlaDueAt` is set from `review.dueBy` when present: the end of that date in the tenant time zone (`TenantSettings.TimeZone`), or the normal SLA when that is earlier. The queue sorts by `Priority`, then `SlaDueAt`.
- Correction (P1): the tenant time zone this document calls `TenantSettings.TimeZone` already exists as `Tenants.TimeZone` (required since `InitialCreate`, `timeZone` in the tenant and settings API), and the validate stage already takes "today" from it; P1 adds no setting. The same holds for every other mention of `TenantSettings.TimeZone` (§5.6, §13.1, §18 "Settings", §21 step 1).

**OutboxMessage**
- Adds `FannedOutAt datetimeoffset?` (like every other timestamp of the table; added in P0). It is a concurrency token, so two publishers cannot both hand the same row on. That guards only a row that exists: for a settlement without one (decided before P0), the publish stage inserts the row and claims the document in the same save (`DocumentOutbox.AddMissing` marks the document's `Version` modified), so of two publishers that both found no row only the first commits; the index `(DocumentId, EventType)` is not unique, because a document settled again gets a row per settlement. Until bindings exist (P3) the publish stage sets it itself: handing the row on is the publish. `DeliveredAt` is deprecated: it is never set today, and deliveries track their own state.
- The payload becomes `DocumentEventMessage` v2: `{tenantId, documentId, documentVersion, typeKey, typeVersion, supplierId, receivingEntityId, channelId, eventType, at}`.

The single **RoleProjector** (`src/DocProc.Application/Documents/RoleProjector.cs`) fills the existing denormalised columns (`DocumentNumber`, `DocumentNumberNormalized`, `IssueDate`, `Currency`, `TotalGross`), the new `ValidFrom`, `ValidUntil` and `LineCount`, and `DocumentFieldIndex` from role bindings. It replaces both `ExtractHandler.Apply` and `DocumentEndpoints.RefreshDenormalizedColumns` (done in P0). It reads the stored JSON tree, not a typed record, so it serves definition-driven trees unchanged; the Application layer cannot see the `Document` entity, so `DocumentRoleColumns` (`src/DocProc.Infrastructure/Persistence/DocumentRoleColumns.cs`) writes its result into the columns, leaving the columns of unbound roles alone. Until definitions carry role bindings (P1), `RoleBindings.ForLegacyType` holds the bindings of the coded types. The two old copies differed slightly; the projector follows the corrections endpoint (a value counts only when its field has one, and a currency is upper-cased before the three-letter check).

### 6.3 Ingestion, channels and attachment-level routing

**Ingestion** (`Entities/Ingestion.cs`) gains these columns:

- `SourceSender nvarchar(320)?`: the real e-mail From, lower-cased.
- `SubmittedBy nvarchar(200)?`: the actor. This fixes the overload in which `SourceFrom` receives `ActorName` (`IngestionEndpoints.cs:189`).
- `SourceRecipients json?`, `SourceMailbox nvarchar(320)?`, `SourceFolderPath nvarchar(1024)?` (relative to the channel root), `SourceSubject` (already exists).
- `SenderAuthJson json?`: SPF, DKIM and DMARC parsed by MimeKit from `Authentication-Results`.
- `ForwardedBy nvarchar(320)?`.
- `SupplierIdHint?`, `DocumentTypeHint nvarchar(64)?`.
- `ReceivedAt datetime2`: when the source received the file (the IMAP `INTERNALDATE`, the time a watched file passed its stable-size check, or the upload time). It feeds `source.receivedAt` and `source.receivedDate` in the values view (5.6).

`SourceFrom` is still written for one release and then dropped.

**IngestionItem** gains:

- `Role {Attachment, Body, ArchiveMember}`. An e-mail body becomes an item when the channel option `bodyAsDocument` is on.
- `RouteJson?`: the per-item routing result, because rules can match on the file name (see below).

**IngestionChannel** (`Entities/Channels.cs`):

- The `Type` enum becomes `ConnectorKey nvarchar(64)`. The existing values `Mailbox`, `WatchedFolder`, `ApiUpload` and `PortalUpload` stay valid.
- New columns: `DefaultSupplierId?`, `DefaultDocumentTypeKey?`, `DefaultReceivingEntityId?`.
- `SourceKey nvarchar(400)`: the normalised mailbox address or folder root, with a unique index `(ConnectorKey, SourceKey)` across tenants, so two tenants cannot poll the same mailbox.
- `ConfigJson` is validated against a per-connector JSON Schema. For example, a Mailbox has `{host, port, username, folder, unwrapForwards, trustedForwarderDomains, bodyAsDocument}`.

**Attachment-level routing.** Routing runs per `IngestionItem`:

- The ingestion-level facts (sender, recipient, folder, subject) are shared.
- `fileNameRegex` conditions apply per item.
- A mail with an invoice PDF and a delivery-note PDF can therefore pin different types.
- Split parts inherit their item's route and are re-checked by Match.

**SHA-256 de-duplication** (`IngestionItemOwnership`) is unchanged. When a file arrives again through a different route, the second ingestion links to the first document as today. If the route resolves to a different supplier, an info flag `ROUTE_DIFFERS {firstRuleId, secondRuleId}` is added to the first document while it is not yet settled. That makes the case visible instead of silent.

### 6.4 Suppliers, groups, receiving entities and attributes

**Supplier** (`Entities/Suppliers.cs`) gains:

- `GroupId?`.
- `AttributesJson json`, validated against `TenantSettings.SupplierAttributeSchema` (a JSON Schema the tenant edits), for example `{"erpVendorId":"V-10042","defaultCostCentre":"4711","paymentBlock":false}`.
- `SettingsJson json`, mapped as an owned record:

```json
{ "defaultDocumentTypeKey": "Invoice", "allowedTypeKeys": ["Invoice", "CreditNote"],
  "fixDocumentType": "whenSourceMatched",
  "locale": { "culture": "de-DE", "dateOrder": "DMY" },
  "review": { "mode": "inherit", "autoApproveMinConfidence": 0.92 },
  "defaults": { "currency": "EUR" },
  "layouts": { "required": "inherit" },
  "requireSenderAuth": true }
```

- `fixDocumentType` is one of `always | whenSourceMatched | never`. `review.mode` is one of `inherit | always`.
- `defaults` are supplier-wide and apply with or without a layout (the `defaults` step reads them); layout-specific defaults stay in the layout's field mapping.
- `layouts.required` is `true | false | inherit` (default `inherit`, which reads the tenant-wide `TenantSettings.Layouts.RequiredByDefault`, itself default false). When it resolves to true, a document of this supplier that matches no layout raises `NEW_LAYOUT` even while the supplier has no layout at all yet (section 8.4), so a source-routed supplier can be made layout-managed before its first layout exists and none of its documents is auto-processed without one. Otherwise a supplier counts as layout-managed from its first Active layout on. It is set in the Suppliers sheet's Settings tab (section 19).
- `AlwaysReview`, `DefaultCurrency` and `TenantSettings.AlwaysReviewSupplierIds` are read as fallbacks, migrated (section 21), and dropped in P6.
- `EmailDomains` is migrated into source rules.
- `Supplier.RowVersion` hashes into `AppliedConfigJson.supplierSettingsHash`, so a pinned retry reuses the snapshot.

**SupplierGroup** (new): `Id`, `TenantId`, `Name`, `AttributesJson`, `SettingsJson`.
- Layouts, attributes, settings and output bindings can target a group, and the supplier inherits them (supplier overrides group).
- This covers branches and subsidiaries of one supplier group, and many suppliers that share one layout or ERP convention.
- `POST /suppliers/{id}:clone-config {fromSupplierId}` copies rules (as drafts), layouts (as Draft versions), attributes and bindings.

**ReceivingEntity** (new; the legal entity or company code receiving the document): `Id`, `TenantId`, `Code`, `Name`, `VatIds json`, `AddressesJson`, `AttributesJson`.
- It is resolved from a source rule (for example, one mailbox per company), from the channel default, or from the customer party's VAT. `CustomerMatchRule` generalises from the tenant VAT to entity VATs.
- **SupplierEntityAttributes**: `(SupplierId, ReceivingEntityId, AttributesJson)`. The vendor number in company 1000 can differ from company 2000. The values view merges entity-specific attributes over the defaults.

### 6.5 SupplierSourceRule

A rule is a set of conditions that must all hold. This covers the plain cases (one folder or one address) as well as shared intake services, such as a scanning bureau that sends every supplier from one address with the supplier in the subject.

| Column | Meaning |
|---|---|
| `Id`, `TenantId`, `SupplierId` | |
| `ChannelId?` | Condition: the ingestion came through this channel |
| `Recipient?` | Exact mailbox or alias, normalised (e.g. `nordwind@tenant-a.local`) |
| `Sender?` / `SenderDomain?` | Exact From address / From domain (platform free-mail deny list applies) |
| `FolderPrefix?` | Path prefix relative to the channel root, normalised with a trailing `/` |
| `SubjectRegex?` / `FileNameRegex?` | NonBacktracking, 100 ms timeout |
| `Mode` | `Exclusive` (authoritative) or `DefaultWhenNoContentMatch` (yields to VAT/IBAN) |
| `RequireSenderAuth` | Default true when `Sender` or `SenderDomain` is set |
| `Priority` | Tie-break after specificity |
| `PinTypeKey?`, `ReceivingEntityId?` | What the rule also decides |
| `Enabled`, `CreatedFromDocumentId?`, `CreatedBy/At` | Traceability for rules learned from review |

- Indexed lookup columns hold the normalised condition values: `(TenantId, Recipient)`, `(TenantId, Sender)`, `(TenantId, SenderDomain)` and `(TenantId, ChannelId, FolderPrefix)`.
- Uniqueness is on the full condition hash per tenant, so two suppliers cannot claim identical conditions.

### 6.6 Supplier layouts and identification rules

A supplier can send the same document type in several designs: invoices from its ERP and from its web shop, or a redesign in 2025 while some branches still print the old one. Each design is one **layout**. A layout is an identification rule (which components of the page prove this design, section 8.5) plus a field mapping (where each value sits, section 8.1), versioned together. Several layouts per supplier and type are Active at the same time; adding a new design never retires an old one. New file `Layouts.cs` in `src/DocProc.Infrastructure/Persistence/Entities` (P4).

**SupplierLayout** (one design of one type)
- `Id`, `TenantId`, `Scope {Supplier, Group}`, `SupplierId?`, `GroupId?`, `DocumentTypeKey`.
- `LayoutKey nvarchar(64)` (for example `nordwind-invoice-erp`, `nordwind-invoice-webshop`), `Name`, `ReceivingEntityId?`, `ItemKinds json?` (restricts the layout to `pdf`, `image`, `einvoice-xml`, `tabular`…).
- `Priority int` (default 100; only used to order two layouts whose rules both hold, section 8.5).
- `ActiveVersion?`, `ShadowVersion?`, `Status {Active, Retired}`.
- `CreatedFromDocumentId?`, `LastMatchedAt?` (feeds "layout not seen for 180 days" in section 17).
- Unique on `(TenantId, Scope owner, DocumentTypeKey, LayoutKey)`.

**SupplierLayoutVersion** (temporal; immutable once out of Draft)
- `Id`, `LayoutId`, `Version`, `BuiltAgainstTypeVersionId`.
- `Status {Draft, Candidate, Shadow, Active, Degraded, Retired}`.
- `Origin {Human, Review, NewLayoutReview, LlmProposed, Imported}`.
- `IdentificationJson` (the identification rule; required and non-empty for every version that leaves Draft), `IdentificationHash char(8)`.
- `MappingJson` (the field mapping), `CoverageJson` (which fields the rules cover, used for the reduced LLM schema), `SourceDocumentIds json`, `ReplayJson` (field agreement plus the identification test: which of the supplier's documents the rule matches, section 8.5).
- `SimilarityProfileJson`: page-1 tokens, label positions, page size and item kind. It is used only by `LayoutSuggester` to rank suggestions for a reviewer (section 12.10); it never selects a layout. It replaces the fingerprint of the earlier draft.
- `ReviewWarmup int` (`firstNDocumentsReview`): set at activation from the tenant's layout activation policy (decision 2 in section 24, `TenantSettings.LayoutActivation`), not from a fixed default. An author can raise it per version.
- Stats: `Identified` (rule matched), `Applied`, `Agreed`, `Corrected`, `ConsecutiveFailures`.
- `ApprovedBy/At`, `CreatedBy/At`.

**TypeIdentificationRule** (tenant fallback; section 8.5), modelled like a layout:
- `Id`, `TenantId`, `DocumentTypeKey`, `Name`, `ActiveVersion?`, `Status {Active, Disabled}`.
- `Priority int` (default 100). It orders Active rules **of the same type** only: when two of them hold, the one with the higher priority is recorded as the evidence and counted in `Matched`. It never decides between rules of different types (section 8.5).
- `CreatedFromDocumentId?`, `CreatedBy/At`, stats `Matched`, `Overridden` (a reviewer changed the type it set).

**TypeIdentificationRuleVersion** (temporal; immutable once out of Draft)
- `Id`, `RuleId`, `Version`, `RuleJson` (same format as `IdentificationJson`, without `supplierValue`), `RuleHash char(8)`, `ExcludedSupplierIds json?`.
- `State {Draft, PendingApproval, Active, Retired}`, `TestJson` (the save-time test against the last 500 approved documents of the tenant), `ApprovedBy/At`, `CreatedBy/At`.
- Documents pin the version (`Documents.IdentificationRuleVersionId`), so a later edit never changes which rule a past document was typed by.

A tenant rule decides only the type, never a field mapping, and is evaluated only for documents whose supplier is unknown, or is known but has no layouts of its own or of its group and does not have `layouts.required` (for such a supplier the tenant rules only propose a type for the `NEW_LAYOUT` review).

Reading the earlier draft's terms: a "mapping variant" is now a layout, `SupplierMapping` is `SupplierLayout`, and the "mapping" is the layout's field mapping, executed by the step kind `supplier-mapping` (the step name is unchanged).

`ExtractorProfiles` is left untouched: no code writes it today (`MatchHandler.cs:44` always nulls the reference). It is dropped in P6 after a zero-row check.

### 6.7 Outputs and delivery

New file `Outputs.cs` in `src/DocProc.Infrastructure/Persistence/Entities` (P3).

**OutputDestination**
- `Id`, `TenantId`, `Name`, `Transport nvarchar(32)`: `http | blob | sftp | email | pull`.
- `ConfigJson` (validated per transport schema), `SecretRef` (write-only, using the `ChannelEndpoints` secret-reference pattern; supports two active secrets for rotation).
- `CallbackConfigJson?`, `Status`, `LastTestAt/Result`.

**OutputMapping**: `Id`, `TenantId?` (null for a platform integration template), `Key`, `Name`, `SourceTypeKey?` (null means any type), `PublishedVersion`.

**OutputMappingVersion**
- `MappingId`, `Version`, `Engine`: `jsonata | fieldmap` (compiled to JSONata) | `liquid` (P6, for text formats) | `passthrough`.
- `Expression`, `ContentType`, `FileNameTemplate?`.
- `TargetSchemaJson?` (the receiver's JSON Schema) or `TargetXsd?` (P6).
- `ReferencedPathsJson` (from a JSONata AST scan), `SampleDocumentIds`, `State`, `PublishedAt/By`.

**OutputBinding**
- `Id`, `TenantId`, `Name`, `Events json`: `document.approved | document.rejected | document.updated | document.needs_review`.
- Scope: `DocumentTypeKey?`, `SupplierId?`, `GroupId?`, `ChannelId?`, `ReceivingEntityId?`, `Condition?` (a JSONata boolean).
- `MappingId`, `PinnedMappingVersion?`, `DestinationId`, `RecipeJson?`.
- `Mode {PerDocument, Batch}`, `BatchJson?`.
- `ExclusiveGroup?`, `Priority`, `OnReapproval {Resend, Skip, Update}`, `UpdateMappingId?`, `Enabled`.

**OutboxDelivery**
- `Id`, `TenantId`, `OutboxMessageId`, `DocumentId`, `DocumentVersion`, `BindingId`, `DestinationId`, `MappingVersionId`, `BatchId?`.
- `IdempotencyKey` unique: `{documentId}:{version}:{bindingId}`.
- `Status {Pending, Sending, Delivered, Failed, DeadLettered, Skipped, Cancelled}`, `Attempts`, `NextAttemptAt`.
- `RecipeStateJson` (step reached, captured variables), `LastStatusCode`, `LastError`.
- `PayloadBlobKey` (`BlobContainers.Exports`), `PayloadSha256`, `ContentType`, `CreatedAt`, `DeliveredAt`.
- Indexes `(Status, NextAttemptAt)` and `(DocumentId)`.

**DeliveryAttempt**: `DeliveryId`, `Step`, `At`, `DurationMs`, `StatusCode`, `ErrorClass`, `ResponseExcerpt` (4 KB, with sensitive values masked).

**DocumentExternalRef**: `DocumentId`, `DestinationId`, `ExternalId`, `Status {Pending, Accepted, Posted, Rejected}`, `Message`, `UpdatedAt`.

**OutputBatch**: `Id`, `TenantId`, `BindingId`, `WindowStart/End`, `Status`, `ItemCount`, `PayloadBlobKey`.

### 6.8 Master data sync

**MasterDataSource**
- `Id`, `TenantId`, `Target {Suppliers, SupplierEntityAttributes, LookupTable:<key>, ReceivingEntities}`.
- `Transport {upload, http, blob}` (reuses the destination config and secret model), `RequestJson`.
- `MappingExpression` (JSONata from external records to our record shape), `MatchKeys` (for example `attributes.erpVendorId`, then `vatId`).
- `ConflictPolicy` per attribute: `externalWins | localWins | flag`.
- `ScheduleCron?`, `LastRunAt`, `LastResultJson`.

**MasterDataRun**: `SourceId`, `At`, `Created`, `Updated`, `Conflicts`, `DiffBlobKey`.

### 6.9 Governance

- **ConfigAuditEntry**: `TenantId`, `EntityType`, `EntityId`, `Version?`, `Action` (`create | edit | publish | approve | activate | retire | rollback | import | sync`), `Actor`, `At`, `DiffJson`, `Reason?`.
- **ConfigApproval**: `TenantId`, `EntityType`, `EntityId`, `Version`, `RequestedBy`, `ApprovedBy?`, `State`. It is used when `TenantSettings.ConfigApprovals` requires four-eyes for that entity type.
- **GoldenDocument**: `TenantId`, `DocumentId` (approved), `TypeKey`, `SupplierId?`, `ExpectedValuesJson` (a values-view snapshot), `ExpectedOutputsJson` (`{bindingId: payloadSha256}`), `AddedBy/At`.
- **RegressionRun**: `TenantId`, `Trigger` (`typeVersion | layoutVersion | identificationRule | outputVersion | prompt/model | manual`), `CandidateConfigJson`, `ResultJson`, `At`.
- **Sandbox documents**: `Document.IsSandbox = 1`. These are excluded from the inbox, insights and duplicate checks, never delivered (outputs are rendered only), and deleted after 7 days.

### 6.10 Tenancy, RLS and platform rows

`RowLevelSecurity.cs` today has one predicate, which matches either the session tenant or the platform scope. It cannot express "every tenant may read platform rows (TenantId NULL) but no tenant may write them". `ExtractorProfiles`, which has a nullable TenantId, is protected only by its EF filter (`DocProcDbContext.cs:132`) and is not in `ProtectedTables`.

New in P0:

- A second function `rls.fn_tenant_or_platform_read(@TenantId)`. It allows `@TenantId IS NULL OR @TenantId = SESSION_CONTEXT tenant OR platform scope`, and is used as the **filter** predicate on nullable-tenant tables.
- **Block** predicates `AFTER INSERT`, `AFTER UPDATE`, `BEFORE UPDATE` and `BEFORE DELETE` use the existing strict `fn_tenant_predicate`. A tenant session can therefore never create, change or delete a platform row.
- Nullable-tenant tables: `DocumentTypeDefinitions`, `DocumentTypeVersions`, `LookupTables`, `LookupEntries`, `OutputMappings`, `OutputMappingVersions`, and `ExtractorProfiles` (closing today's gap).
- Every other new table goes under the strict predicate. A migration re-creates the policy from frozen table lists of its own (`PipelineProtectedTables`, `PlatformRowTablesV1`, as documented in that file): the SQL is generated when a migration runs, so a migration built from a list that later grew would reference tables that do not exist yet on a fresh database.
- **History tables.** SQL Server does not apply a table's security predicates to its history table, so a tenant session read every tenant's history through `FOR SYSTEM_TIME` or the history table itself. Each temporal table's history table gets a filter predicate of its own (only a filter: the system writes history): `DocumentsHistory` the strict one, `ExtractorProfilesHistory` the platform-row read one, and every later temporal table the one of its table (`StrictHistoryTablesV1`, `PlatformRowHistoryTablesV1`, migration `TenantPolicyHistoryAndMoves`; corrected after review: the first P0 policy left them out). The policy is schema-bound: a migration's plain column add or drop on a temporal table passes, but turning system versioning off is refused while the policy names the table (with or without the history predicates), so such a migration drops and re-creates the policy around it.
- **Moves between tenants.** The strict tables get an `AFTER UPDATE` block predicate too, so a tenant session cannot update one of its rows to another tenant's `TenantId` (before, only an `AFTER INSERT` block existed). Platform scope still can.
- `LlmProviderConfigs` has the same shape as `ExtractorProfiles` (platform configurations with TenantId NULL) and is also protected only by its EF filter. It is not in the P0 list above; it joins the nullable-tenant tables with the P1 policy re-creation.
- Tests (Testcontainers.MsSql): a tenant session can SELECT a platform template but gets an error on UPDATE or DELETE of it; tenant B cannot see tenant A's type, not even through `FOR SYSTEM_TIME ALL` or a history table; a tenant cannot move its own row to another tenant; platform scope can do everything.

## 7. Supplier identification and configuration (R6)

### 7.1 Capturing source facts

- `UploadIntake.CreateIngestion` (`src/DocProc.Pipeline/Intake/UploadIntake.cs:48-89`) takes a `SourceMetadata` record (`Sender`, `Recipients`, `Mailbox`, `FolderPath`, `Subject`, `SubmittedBy`, `SenderAuth`, `ForwardedBy`, `SupplierIdHint`, `TypeHint`, `ReceivedAt`). Every channel goes through it.
- **Mail.** `MailKitImapMailSource : IMailSource` uses MailKit 4.18.2, already pinned. `ChannelPollerHostedService` runs in the worker, and in the API when `Pipeline:RunInProcess` is set, as in compose.
  - It persists `InboundMessage.To` (`IMailSource.cs:23`), From and `Authentication-Results`, and advances `IngestionChannel.Cursor`.
  - With `unwrapForwards` it reads an attached `message/rfc822`, or the inline "From:" block of a forward, when the forwarder's domain is in the channel's `trustedForwarderDomains`. The original sender becomes `SourceSender` and the forwarder goes to `ForwardedBy`.
  - A Graph source follows the same contract later.
- **Folders.** `LocalFolderSource` (a mounted path) and `BlobFolderSource` (a container prefix) are run by `FolderWatcherHostedService`. They use a stable-size check, move files to `processed/` or `failed/`, and record the relative path.
- **API and portal.** `POST /ingestions` accepts `supplierId` and `documentTypeKey`. A hint is honoured only from a portal user with Review rights or from an API key with the `ingest:hint` scope, and it is included in `RequestHash` (`IngestionEndpoints.cs:509-519`). The portal `UploadDialog` sends the PortalUpload channel (today it sends none: `UploadDialog.tsx:29-33`) and offers optional supplier and type pickers.

### 7.2 Resolution at ingest

`ISupplierSourceResolver` (new, `SupplierSourceResolver.cs` in `src/DocProc.Pipeline/Routing`) is a pure function. It is called in `IngestHandler.CreateDocumentsAsync` (`IngestHandler.cs:219-230`) for each item, so documents are created with their supplier already set. Candidate rules come from indexed lookups on the normalised conditions; it does not load the whole supplier table the way `MatchHandler.cs:38` does.

1. An authorised explicit hint wins (method `Hint`).
2. Rules whose conditions all hold become candidates. Sender conditions require DMARC pass, or SPF plus aligned DKIM, when `RequireSenderAuth` is set. Otherwise the rule is skipped, and `SUPPLIER_SOURCE_UNVERIFIED` (warning; never auto-approve) is raised if it would have matched.
3. Specificity: the number of conditions, weighted (`Sender` 4, `Recipient` 3, `FolderPrefix` 3 plus prefix length, `FileName` and `Subject` 2, `Channel` 1, `SenderDomain` 1). Then `Priority`.
4. If no rule matches, the channel's `DefaultSupplierId` applies as `DefaultWhenNoContentMatch`.
5. If the top two candidates name different suppliers with equal score, nothing is assigned and `SUPPLIER_AMBIGUOUS` is raised.

The result is written as `SupplierId`, `SupplierMatchMethod=SourceRule`, `SupplierSourceRuleId` and `SupplierMatchConfidence` (0.99 for Exclusive, 0.7 for DefaultWhenNoContentMatch), together with `ReceivingEntityId` and a timeline event `supplier_identified_by_source`.

### 7.3 Auto-apply

`IEffectiveConfigResolver` (section 10.3) loads supplier settings, group settings and layouts once the supplier is known:

- **Type.** A rule's `PinTypeKey` wins. Next, a single `allowedTypeKeys` entry applies under `fixDocumentType` (`always`, or `whenSourceMatched` when the method was SourceRule). The type is set with `TypeSource=Supplier`. Otherwise the identification rules of the supplier's layouts decide the type in Classify (`TypeSource=Identification`, section 8.5). With several allowed types, the keyword and LLM classification is restricted to that set plus `Unknown`.
- **Layout.** `ILayoutResolver` evaluates the identification rules of the supplier's (then its group's) Active layouts, strictly, and writes `SupplierLayoutVersionId`. It runs in Classify when the supplier is already known from the source, and in Match when content evidence names the supplier only there (section 8.4). A pinned type does not skip this: the layout still has to be identified, among the layouts of the pinned type. If the supplier is layout-managed and no rule holds, the document gets a blocking `NEW_LAYOUT` and goes to review (section 12.10); it is never processed with the "closest" layout. A supplier identified from the source (folder, mailbox, From) can be made layout-managed before its first layout exists with `settings.layouts.required` (6.4); its first document then also arrives as `NEW_LAYOUT` instead of taking the classifier-and-LLM path.
- **Defaults, attributes and locale.** They are applied by the `defaults` step and the parse transforms. `Supplier.DefaultCurrency`, which is never read today, takes effect at last.
- **Review policy.** `review.mode`, `autoApproveMinConfidence`, and the layout version's warm-up.
- **Outputs.** Supplier- and group-scoped bindings apply at publish.

All of this is snapshotted in `AppliedConfigJson`. The review header shows: "Supplier: Nordwind Großhandel GmbH, via From rule rechnungsversand@nordwind.example (DMARC pass). Layout nordwind-credit-note-2025 v4, identified by 'Gutschrift' (page 1, title) and VAT DE811111111". Clicking the identification chip highlights the evidence words on the page.

### 7.4 Match as corroboration (no reorder)

`MatchHandler` keeps its position after Classify. It becomes a chain of `ISupplierIdentificationProvider`: the source result from step 7.2, then `VatIbanProvider` and `NameProvider`, which wrap today's `SupplierMatcher`, unchanged and still covered by `SupplierMatcherTests`.

- With an **Exclusive** source supplier, if content evidence (VAT 0.98 or IBAN 0.95) names a different supplier, the source supplier is kept and a **blocking** `SUPPLIER_CONFLICT {ruleId, contentSupplierId, evidence}` is raised. Auto-approval is impossible.
- With a **DefaultWhenNoContentMatch** supplier, VAT or IBAN evidence replaces it. The switch is recorded in the timeline and in `SupplierMatchMethod`, and everything the old supplier's configuration decided is undone, because a supplier's identification rules never apply to another supplier's documents:
  - the old supplier's layout, if Classify had picked one, is dropped (`SupplierLayoutVersionId` cleared);
  - a type with `TypeSource=Identification` or `Proposed` that came from the old supplier's (or its group's) layouts is reverted to Unknown, and a type pinned only by the old supplier's source rule or `allowedTypeKeys` (`TypeSource=Supplier`) is reverted too;
  - the `NEW_LAYOUT` or `LAYOUT_AMBIGUOUS` raised for the old supplier is cleared (9.4);
  - `match.layout` then re-runs classification for the new supplier before Extract, in the order of 9.4: its layouts, then its group's, then (if it has none and they are not required) the tenant fallback rules, then keywords and the LLM. A type fixed by a human, a hint or the e-invoice XML stays.
- With no source supplier, matching works exactly as today. Once VAT or IBAN names the supplier (confidence 0.95 or higher), the `match.layout` step resolves that supplier's layouts (section 8.4).
- Fixes made in passing:
  - Candidates are loaded by indexed VAT, IBAN and domain instead of the whole supplier table.
  - Ordering is deterministic (by Id).
  - Two suppliers that share a VAT or IBAN raise `SUPPLIER_AMBIGUOUS`.
  - E-invoice signals are read from the typed projection, `Invoice.Supplier` or `PaymentTerms.Iban` (`MatchHandler.cs:80-98`), unchanged.
- `ReprocessStart` (`DocumentEndpoints.cs:31-37`) gains `Match`.

### 7.5 Example: Nordwind Großhandel GmbH for tenant Acme

```json
[
  { "supplier": "Nordwind Großhandel GmbH", "sender": "rechnungsversand@nordwind.example", "mode": "Exclusive", "requireSenderAuth": true, "priority": 10 },
  { "supplier": "Nordwind Großhandel GmbH", "recipient": "nordwind@tenant-a.local", "mode": "Exclusive" },
  { "supplier": "Nordwind Großhandel GmbH", "channel": "Scanner drop", "folderPrefix": "lieferanten/nordwind/gutschriften/", "mode": "Exclusive", "pinTypeKey": "CreditNote" },
  { "supplier": "Nordwind Großhandel GmbH", "channel": "Scanner drop", "folderPrefix": "lieferanten/nordwind/preislisten/", "mode": "Exclusive", "pinTypeKey": "price-list" },
  { "supplier": "Nordwind Großhandel GmbH", "senderDomain": "nordwind.example", "mode": "DefaultWhenNoContentMatch", "priority": 50 },
  { "supplier": "Nordwind Großhandel GmbH", "channel": "Scan bureau", "sender": "scans@bureau.example", "subjectRegex": "^\\[NORDWIND\\]", "mode": "Exclusive", "requireSenderAuth": true }
]
```

The last rule covers the shared intake service: one sender, with the supplier identified by a subject token. The `preislisten/` rule pins the type before Split, so a 30-page list dropped there is never cut by the heuristic splitter (5.8), and its layout is still identified among Nordwind's price-list layouts.

## 8. Supplier layouts: identification and field mappings (R3, R9, R10)

### 8.1 Format

A layout version holds two parts: `identification` (section 8.5) and the field mapping (`fields`, `extFields`, `defaults`, `requiredFields`, `llmHints`, `ruleOverrides`, `pipelineOverrides`). The mapping engine lives in `src/DocProc.Extraction.Profiles`, which today contains only a csproj and is renamed `DocProc.Mapping`. Correction (P2): renamed in P2, which put the table tool's `column-table` engine there (`DocProc.Mapping.Tables`). Field keys are definition paths. The strategy menu is the docs/04 set plus structured strategies:

- `label-right`, `label-below`, `label-row`, `region` (relative to the page or to an anchor), `regex-anywhere`, `column-table`, `key-value-block`;
- `xml-path` (the restricted `XmlNavigation` walker, not XPath evaluation), `json-path` (JSON Pointer subset), `cell` (`Sheet1!C7` or a header name), `source` (`${source.subject}`, `${source.fileName}`, `${source.folderPath}` with a regex capture), and `constant`.

Transforms: `trim`, `removeWhitespace`, `regex` (capture or replace), `replace`, `uppercase`, `parseNumber(locale)`, `parseDate(formats, locale)`, `table(lookupKey)`, `sign(negate|abs)`.

```json
{
  "layoutKey": "nordwind-credit-note-2025", "name": "Nordwind credit note (ERP, 2025 design)",
  "documentType": { "key": "CreditNote", "minVersion": 3 },
  "itemKinds": ["pdf", "image"],
  "identification": {
    "all": [
      { "text": "Gutschrift", "page": 1, "region": "title" },
      { "supplierValue": "vatId", "page": 1 }
    ],
    "none": [ { "text": "Rechnung", "page": 1, "region": "title" } ]
  },
  "fields": {
    "supplierCustomerRef": { "rules": [
      { "strategy": "label-right", "labels": ["Kunden-Nr.", "Kd.-Nr."], "page": 1, "maxDistance": 250, "pattern": "[0-9]{3}\\s?[0-9]{6}" },
      { "strategy": "region", "page": 1, "bBox": [640, 92, 905, 118], "tolerance": 25 },
      { "strategy": "xml-path", "path": "cac:AccountingCustomerParty/cac:Party/cac:PartyIdentification/cbc:ID" } ],
      "transforms": [ { "op": "removeWhitespace" } ] },
    "originalInvoiceReference": { "rules": [ { "strategy": "regex-anywhere", "pattern": "zu Rechnung Nr\\.?\\s*([A-Z0-9-]{6,})", "group": 1 } ] },
    "creditReason": { "rules": [ { "strategy": "regex-anywhere", "pattern": "(Retoure|Preisdifferenz)" } ],
                      "transforms": [ { "op": "table", "table": "nordwind-reason" } ] },
    "ext.storeNo": { "rules": [ { "strategy": "region", "page": 1, "bBox": [640, 60, 960, 80] } ],
                     "transforms": [ { "op": "regex", "pattern": "Markt\\s+(\\d+)", "group": 1 } ] },
    "lines": { "strategy": "column-table", "page": "*",
               "header": { "anchors": ["Art.-Nr.", "Bezeichnung", "Menge", "Einzelpreis", "Betrag"] },
               "stopAt": ["Zwischensumme", "Summe netto"],
               "columns": { "supplierSku": "Art.-Nr.", "description": "Bezeichnung", "quantity": "Menge",
                            "unitPrice": "Einzelpreis", "lineNet": "Betrag" },
               "transforms": { "unit": [ { "op": "table", "table": "uom" } ] } }
  },
  "extFields": [ { "key": "storeNo", "kind": "string", "label": { "de": "Marktnummer" }, "indexed": true } ],
  "defaults": { "currency": { "value": "EUR", "mode": "ifMissing" },
                "ext.costCentre": { "fromSupplierAttribute": "defaultCostCentre", "mode": "ifMissing" } },
  "requiredFields": ["supplierCustomerRef"],
  "llmHints": { "supplierCustomerRef": "Printed top right after 'Kunden-Nr.', format 123 456789." },
  "ruleOverrides": [ { "op": "add", "rule": { "id": "r_scr_fixed", "kind": "compare",
                       "params": { "left": "supplierCustomerRef", "op": "==", "right": "123456789" }, "severity": "blocking",
                       "message": { "en": "Customer number differs: misrouted document?" } } } ],
  "pipelineOverrides": [ { "op": "param", "step": "llm", "params": { "mode": "fill-missing", "effort": "low" } } ]
}
```

### 8.2 Execution

`SupplierMappingExtractor` (step kind `supplier-mapping`) runs over the normalised `TextLayer` (1000x1000 boxes), the embedded or bare XML, or the tabular or tree layer.

- It runs only for the layout version that `ILayoutResolver` selected; the identification rule has already held, so the field rules never run on a design they were not written for.
- Each hit becomes a `FieldValue` with `SourceMethod.Rule`, `page`, `bBox`, `rawText`, `path` and `ruleRef` (`nordwind-credit-note-2025@4#supplierCustomerRef[0]`). `ProvenanceMatch.ToSource(method)` is parameterised; it is hard-coded to Llm today (`ProvenanceMatch.cs:50`).
- Confidence is 0.97 when anchor and pattern both match, 0.85 for a region-only hit, and is then scaled by the layout version's success rate.
- **Drift check.** The identification rule held, but the field rules found less than the layout's usual coverage (by default, a required field covered by the layout found by none of its rules, or below 70 % of covered fields found). Then `MAPPING_DRIFT` (warning) is raised and the LLM fills the gaps. Consecutive drifts move the version to `Degraded`, and the LLM fills everything. Drift usually means the supplier changed positions but kept its title; the reviewer fixes the fields and saves a new version of the same layout.
- `requiredFields` and `ruleOverrides` are added to validation (section 11).

### 8.3 Lifecycle

- A new version is **Draft**. Saving it from review or from the editor makes it **Candidate**.
- **Dry-run replay** (`:replay`) has two parts, stored in `ReplayJson`:
  - The **identification test**: the rule must hold on the documents the version was built from, and must not hold on any approved document that belongs to another of the supplier's (or group's) layouts, nor on the pending documents of another Candidate layout. An overlap blocks activation and names the documents, so the author adds a distinguishing identifier (section 8.5). A rule with an `allowOcrErrors` identifier is tested with that tolerance.
  - The **field replay**: the field mapping runs only over the **replay set**, the approved documents (of the last N of the supplier and type, plus golden documents) on which this version's identification rule holds, and stores a per-field hit, mismatch and missing matrix. Documents of the supplier's other designs are not in the set, because the field rules were never meant for them. For version 1 of a layout with `Origin=NewLayoutReview` the replay set is the source document plus any siblings re-tested against it (12.10); one document is enough. For a new version of an existing layout the set also contains that layout's recent approved documents on which the new rule holds.
- **Activate** has one condition, used everywhere in this document (8.4, 12.6, 12.10): the identification test passes, and field agreement on a non-empty replay set is at or above the tenant threshold (default 90 % per field). Who may activate, and with which warm-up, is the layout activation policy (decision 2 in section 24, `TenantSettings.LayoutActivation`); by default that is a `MappingAuthor`. Alternatively the version runs in **Shadow**: it is applied but its values are not written, and agreement with the active path is recorded.
- The first `ReviewWarmup` documents under a new Active version always go to review. `ReviewWarmup` comes from the activation policy: by default 0 for version 1 of a layout saved from a `NEW_LAYOUT` review and activated by a `MappingAuthor` (the reviewer has just checked every value of the source document), and 5 for every other version (editor, LLM proposal, import, new version of an existing layout).
- Corrections on mapped fields count against `Stats`. Beyond the threshold the version becomes **Degraded**, and the LLM fallback applies.
- Pinned documents keep their pinned version even if it is later Retired or Degraded, because versions are immutable rows. Only new documents and "reprocess with current config" re-resolve.

### 8.4 Several layouts per supplier and type

Follow-up F3: a supplier can have invoices from different systems, or change its invoice design; each is still an invoice. Several Active layouts can exist per `(supplier or group, type)`, for example `nordwind-invoice-erp`, `nordwind-invoice-webshop` (another source system) and `nordwind-invoice-2023` (the old design, still printed by two branches). A new layout is added next to the existing ones; the old layout stays Active until a `MappingAuthor` retires it. Nothing retires a layout automatically: section 17 lists layouts that have not matched for 180 days as candidates to retire, and that report is advisory only.

`ILayoutResolver` (new, `LayoutResolver.cs` in `src/DocProc.Mapping/Identification`) is a pure function over the text layer (or tree or tabular layer), the source facts and the supplier's master data. It selects the layout, and with it the type, like this:

1. **Candidates.** The Active (and Shadow, for stats only) layout versions of the document's supplier, filtered by `ItemKinds`, `ReceivingEntityId` and, when the type is already fixed (`TypeSource` `Supplier`, `Human`, `Hint` or `Structured`), by that type. If the supplier has none, its group's layouts. Candidate versions (saved but not yet activated) are evaluated as well, but **never applied**: they only feed step 5.
2. **Evaluate** every candidate's identification rule strictly (section 8.5). The result per layout is true or false, plus evidence.
3. **Exactly one Active layout holds**: that layout applies. Its type becomes the document type with `TypeSource=Identification` (unless the type was already fixed), `SupplierLayoutVersionId` is written, and the timeline records `layout_identified {layout, version, evidence}`.
4. **Several Active layouts hold**: the highest `Priority` wins, then the rule with more positive identifiers (identifiers outside `none`, counted through nested groups; this count is what the flag catalogue calls specificity). If two layouts of the supplier still tie on both, nothing is applied and a blocking `LAYOUT_AMBIGUOUS {layouts}` is raised. The identification test of section 8.3 keeps this rare.
5. **No Active layout holds and the supplier is layout-managed** (it or its group has at least one Active layout, or `settings.layouts.required` resolves to true): a blocking `NEW_LAYOUT {supplierId, proposedType, suggestions, pendingLayoutVersionId?}` is raised.
   - A type is proposed: tenant identification rules, then the keyword set, then the LLM classifier (section 9.4). If one is found, it is set **provisionally** with `TypeSource=Proposed` and recorded as `proposedType`. A proposed type never counts as decided: the document cannot auto-approve, the review gate refuses it by flag name, and approval in review requires the reviewer to confirm or change the type (`:match-layout`), which sets `TypeSource=Human`.
   - Because the proposed type has a definition, Extract runs against it before anyone opens the document: the `layout-prefill` step replaces `supplier-mapping`, followed by `llm` (fill-missing, when a key is configured) and `defaults` (section 12.10). The reviewer starts with what the system could find. If no type could be proposed, the type stays Unknown, Extract skips (`TYPE_UNKNOWN`), and pre-fill runs only once the reviewer picks a type with `:match-layout`.
   - The document always goes to review. It can never auto-approve, whatever the confidence; no fuzzy best match is applied.
   - `LayoutSuggester` ranks the supplier's existing layouts by similarity (`SimilarityProfileJson`) and puts the top three into the flag's `suggestions`, for example "looks like nordwind-invoice-2023 (78 %): title changed from 'Rechnung' to 'RECHNUNG / INVOICE'". A suggestion is shown, never applied.
   - If the rule of a **Candidate** version holds (a reviewer already saved this design, but it is not activated yet), the flag carries `pendingLayoutVersionId`, the New layout panel offers "confirm against pending layout X" instead of creating another layout (12.10), and the `MappingAuthor`s of the tenant are notified that documents are waiting for X. The Candidate's field mapping is used only as the pre-fill basis, never as an applied layout.
6. **No layout holds and the supplier is not layout-managed** (no Active layout of its own or of its group, and `layouts.required` false): today's behaviour. Classification as in section 9.4 (tenant fallback rules, keywords, LLM), the LLM extracts, and the normal review gate applies. Saving from review creates the supplier's first layout.

E-invoices (`TypeSource=Structured`) carry their type in the XML and their std fields come from the parser, so a missing layout for them is not a new design: `NEW_LAYOUT` is not raised for `einvoice-xml` items, and the review gate's layout condition does not apply to them (section 11). A layout restricted to `einvoice-xml` adds `xml-path` rules for extras only.

Where it runs: `classify.identification` in Classify when the supplier is known from the source (most documents in a routed setup), and `match.layout` in Match when the supplier is found only by content, or replaced by content evidence (7.4). If the layout found in Match has a different type than the one keywords or the LLM gave in Classify, Match corrects the type before Extract runs (`TypeSource=Identification`, timeline `type_corrected_by_layout`). It never overrides a type fixed by a human, a hint, a source rule of the matched supplier or the e-invoice XML; in that case the layout is only searched among that type's layouts. Both steps raise `NEW_LAYOUT` and `LAYOUT_AMBIGUOUS`, and each owns the instances it raised, keyed by step id (9.4); when Match replaces the supplier, `match.layout` also clears the instances `classify.identification` raised for the old supplier.

A layout can be restricted to a `ReceivingEntityId`. Group-scoped layouts apply only when the supplier has no layout of its own; when it has, a group layout is ignored even if its rule would hold, so a supplier's own rules always win.

### 8.5 Identification rules

Follow-up F2: "I should be able to define one or more components of a document to point to a type ... this is an invoice because it has X and Y, i.e. the word 'Invoice' or the word 'Rechnung'. Since document types coming from a single supplier have the same format we can strictly pinpoint these identifiers."

An **identification rule** is a boolean combination of **identifiers**. Each identifier is one component of the document. C# types `IdentificationRule` and `Identifier`, evaluated by `IdentificationRuleEvaluator` in `src/DocProc.Mapping/Identification/`. The meta-schema is `schemas/identification-rule.v1.json`, snapshot-tested.

**Identifiers**

| Identifier | Holds when | Example |
|---|---|---|
| `text` | The exact word or phrase occurs: consecutive words on one line in reading order, whole words only, whitespace collapsed, case-insensitive unless `caseSensitive: true` | `{ "text": "Rechnung" }` holds for "Rechnung" and "RECHNUNG", not for "Rechnungskorrektur" |
| `regex` | A regex matches the joined text of the region (`NonBacktracking`, 100 ms) | `{ "regex": "Kunden-Nr\\.?\\s*\\d{3}\\s?\\d{6}" }` |
| `supplierValue` | A value from the supplier's master data is printed, compared normalised (`Identifiers.NormalizeCompact`): `vatId`, `taxId`, `iban`, `attributes.<key>`. Allowed only in Supplier and Group layout scope; a tenant fallback rule (which runs when the supplier may be unknown) that contains one is rejected at save | `{ "supplierValue": "vatId" }` holds for "DE 811 111 111" |
| `source` | A source fact matches: `itemKind`, `fileName`, `subject`, `mailbox`, `folderPath` (regex) | `{ "source": "fileName", "regex": "^PL_\\d{4}" }` |
| `xmlPath`, `cell` | Structured sources: an element exists or equals a value; a cell or column header equals a value | `{ "cell": "A1", "equals": "Preisliste" }` |

**Where** (optional on `text`, `regex` and `supplierValue`; it can also be written once on a combination group, as in the `any` example below):

- `page`: a number, `"last"` or `"any"` (default `"any"`).
- `region`: `title` (y 0 to 300 in the normalised 1000 x 1000 space), `header` (0 to 150), `footer` (850 to 1000), `body`; or `bBox: [x0, y0, x1, y1]` with `tolerance` (default 30). A word is inside when the centre of its box is.
- A location written on a group applies to every identifier in that group (and its nested groups) that has none of its own. An identifier's own `page`, `region` or `bBox` overrides the inherited value for that key; the keys are inherited one by one, so `{ "text": "Invoice", "page": 2 }` in a group with `"page": 1, "region": "title"` means page 2, title area.
- `allowOcrErrors: true` (on `text` only) is the single, bounded relaxation of exact matching, and it is restricted:
  - it applies only when the page's text layer comes from OCR, never to a born-digital text layer;
  - within that, only words of six or more letters may differ by exactly one character (substitution, insertion or deletion); it is a fixed bound, never a score or a threshold;
  - it is rejected at save on a rule's only positive identifier, so a rule always also needs one exact identifier to hold;
  - the Identification card shows it as "tolerates one OCR error", and the identification test (8.3) and the overlap check against the supplier's other layouts run with the tolerance on, so a relaxed "Rechnung" that would also hold on another design blocks activation.
  It is off by default.

**Combination.** `all` (X and Y), `any` (X or Y), and optionally `none` (none of these). When several of `all`, `any` and `none` appear at the same level, they are an implicit AND: every present group must be satisfied (`all` fully, at least one of `any`, nothing of `none`). Groups nest to depth 3 and hold at most 20 identifiers. A rule needs at least one positive identifier; `none` alone is rejected at save.

Example: Nordwind's ERP invoice. "Rechnung" in the page-1 title area **and** Nordwind's VAT id somewhere on page 1, and not a correction:

```json
{
  "all": [
    { "text": "Rechnung", "page": 1, "region": "title" },
    { "supplierValue": "vatId", "page": 1 }
  ],
  "none": [ { "text": "Rechnungskorrektur", "page": 1 }, { "text": "Gutschrift", "page": 1 } ]
}
```

Example: a supplier whose German and English branches print different titles. "Invoice" **or** "Rechnung" in the title:

```json
{ "any": [ { "text": "Invoice" }, { "text": "Rechnung" } ], "page": 1, "region": "title" }
```

Example: Nordwind's price list, which comes from a different system than its invoices:

```json
{ "all": [ { "text": "Preisliste", "page": 1, "region": "title" }, { "regex": "gültig ab\\s+\\d{2}\\.\\d{2}\\.\\d{4}", "page": 1 } ] }
```

**Strict semantics.**

- The result is true or false. There is no score, weight or threshold, and nothing is "nearly" matched. `allowOcrErrors` (above) is the only relaxation, it is bounded to one character, and it never produces a score. This is what makes per-supplier pinpointing safe: one supplier's documents keep their format, so a rule written against one document holds for every document in that design and for nothing else.
- Evaluation is deterministic and cheap (an index of normalised words per page, built once per document), so every candidate rule is evaluated; there is no early "good enough".
- The evidence (identifier, matched words, page, box) is returned for every identifier that held and is stored in `AppliedConfigJson.identification`, so the reviewer sees why a layout was chosen.
- A rule is evaluated against the document's pinned text layer. Reprocessing with the current configuration re-evaluates it; a pinned retry reuses the stored result.

**Scopes and precedence.** Identification rules exist at three scopes:

| Scope | Stored in | Decides | Evaluated for |
|---|---|---|---|
| Supplier layout | `SupplierLayoutVersion.IdentificationJson` | Layout and type | Documents of that supplier only |
| Supplier group layout | the same, `Scope=Group` | Layout and type | Documents of group members without own layouts |
| Tenant fallback | `TypeIdentificationRuleVersion.RuleJson` | Type only | Documents whose supplier is unknown at Classify, or is known but has no layouts (own or group) and `layouts.required` false; for a supplier with `layouts.required` and zero layouts they only propose |

In Classify the order is: supplier layout rules, group layout rules, tenant fallback rules, then the keyword rules (`classify.keyword`) and the LLM (`classify.llm`). The first scope with a rule that holds decides, and the later steps do not run. For a layout-managed supplier whose layouts all fail (including one with `layouts.required` and no layout yet), the later steps still run, but only to propose a type for the `NEW_LAYOUT` review (section 8.4); they decide nothing. A supplier rule never fires for another supplier's documents: supplier and group rules are only loaded once the document's supplier is set, and if Match replaces the supplier, everything they decided (layout, type, flags) is reverted and classification re-runs for the new supplier (7.4). A tenant fallback rule that would contradict a type fixed by a source rule or a human is not applied. When tenant rules of two different types hold, neither decides, and classification continues with keywords and the LLM; `Priority` does not break this tie, it only orders rules of the same type (6.6). `Overridden` counts both cases and the reviewer's type changes.

**Created in review.** In the "Identify" mode of the review screen (section 12.9) the reviewer clicks words or draws a box and chooses "This identifies the type". The selection becomes an identifier with its page and region proposed from where it sits; the reviewer toggles `all`/`any`, and the rule is tested live against the supplier's other documents before it is saved with the layout. Tenant fallback rules are created the same way from any document ("use for all suppliers without own layouts"), or edited in Settings > Document types > Identification. In tenant scope, `identifiers:propose` never proposes `supplierValue`: a selection that equals a master-data value of the current supplier becomes a `text` identifier, which the reviewer can turn into a regex, and the card warns that such a value rarely identifies a type for all suppliers.

**Checks at save.** Meta-schema, regex compilation, at least one positive identifier, `allowOcrErrors` never on the only positive identifier, and the identification test of section 8.3. A tenant fallback rule additionally:
- needs `ConfigAdmin` (16), and its activation is subject to `TenantSettings.ConfigApprovals.identificationRules` (15.2);
- is rejected if it contains a `supplierValue` identifier;
- is tested against the last 500 approved documents of the tenant, excluding suppliers in `ExcludedSupplierIds` and suppliers that have layouts. The editor shows how many documents of each type it would have matched and stores the report in `TestJson`; a rule that would have matched documents of another type shows them and cannot be activated without confirming.
- Each edit is a new Draft version; activating it retires the previous version, and documents keep the version they were typed by.

### 8.6 Large tables: price lists over many pages

A price list (section 5.8) is mostly one table of hundreds of rows over many pages. The layout's field mapping maps each column **once**; the `column-table` strategy applies it to every row on every page:

```json
"lines": { "strategy": "column-table", "page": "*",
  "header": { "anchors": ["Art.-Nr.", "Bezeichnung", "Inhalt", "VE", "Preis"], "repeat": "auto", "minAnchors": 3 },
  "columnsFrom": "header",
  "columns": { "supplierSku": "Art.-Nr.", "description": "Bezeichnung", "packDescription": "Inhalt",
               "unit": "VE", "price": "Preis" },
  "tierColumns": { "headerPattern": "ab\\s+(\\d+)", "minQuantityFromGroup": 1, "target": "tiers" },
  "rowAnchor": "supplierSku",
  "continuation": "appendToPrevious",
  "groupHeader": { "when": "noValueIn:price", "target": "category" },
  "skipRows": ["^Übertrag", "^Seite \\d+ von \\d+", "^Fortsetzung"],
  "pageMargins": { "top": 90, "bottom": 940 },
  "stopAt": ["Ende der Preisliste"],
  "transforms": { "price": [ { "op": "parseNumber", "locale": "de-DE" } ],
                  "packSize": [ { "op": "regex", "from": "packDescription", "pattern": "^(\\d+)\\s*x", "group": 1 } ],
                  "unit": [ { "op": "table", "table": "uom" } ] } }
```

- **Header repetition and page breaks.** With `repeat: "auto"` the strategy looks for the header row (at least `minAnchors` anchor words on one line) on every page. Where it finds it, the column boundaries are re-measured on that page, so a page printed slightly shifted still parses. Where a page has no header (lists that print it only on page 1), the boundaries of the last page with a header are reused, relative to the page width. Rows continue across page breaks; `pageMargins` cut off running headers and footers, and `skipRows` drops carry-over and page-number lines.
- **Rows.** A new row starts at a line with a value in the `rowAnchor` column. A line without one is a continuation (a second description line) and is appended to the previous row's description. A line with text but no price is a group heading and fills `category` for the rows below it.
  - Correction (P2): the last two sentences contradict each other for a second description line, which has neither an anchor nor a price. As built (`ColumnTableEngine`, review table tool): a line without a value in the `when` column (and no tier price) is a heading when it is printed as one run of text (no gap wider than a column gap) or across a divider, unless it has no anchor value, lies inside text columns only and follows a row, which makes it a continuation; so a heading printed only inside the description column directly under a row is read as a continuation line. A continuation appends to the row's text columns (`string`, `text`); a second value in another column rejects the row (`continuationConflict`). `when` defaults to the first amount or number column other than the anchor.
- **Tier prices.** `tierColumns` turns columns whose header matches `ab 10`, `ab 50` into `tiers` entries, with `minQuantity` taken from the header and `price` from the cell; empty cells produce no tier.
  - Correction (P2): the example's `ab\s+(\d+)` reads `ab 1.000` (a common German tier header) as 1, and a column chosen by its header alone becomes a tier even when the reviewer leaves it out in the table tool (§12.4 step 3). As built: the header pattern proposes a column's minimum quantity (the default `^(?:ab|from|von)\s*(\d(?:[\d.,\s]*\d)?)` captures the whole number, read in the request's locale), and a column of the mapping is a tier only when it names that quantity (`tierMinQuantity`); one without a field or a quantity is left out.
- **Table report.** The strategy records per page: rows parsed, rows rejected (a row band that failed a column's parse or pattern), and whether the header was found. The report is stored as the `DocumentTableReport` of the path `lines` (section 6.2), next to the tree rather than inside the `FieldValue` envelope, and read by the `tableComplete` rule (section 11), which raises `TABLE_ROWS_REJECTED {page, rows}` (blocking) and `TABLE_PAGE_NO_ROWS {page}` (blocking when the rule sets `everyPageInRange`, as `std.price-list` does; a warning otherwise).
- **Cost and size.** The strategy is linear in the number of words; a 40-page list with 2,000 rows runs in well under a second. Rows are written as one array value; per-row provenance stays in each row's FieldValues.
- **Spreadsheets.** Price lists often come as XLSX or CSV. Once the tabular layer exists (P6, section 12.5), the same `columns` map binds to sheet headers by name, header repetition does not arise, and the identification rule uses `cell` identifiers.

## 9. Extraction against definitions

### 9.1 Contract changes

The contracts live in `src/DocProc.Application/Abstractions/IDocumentExtractor.cs`.

- `ExtractionInput`:
  - `DocumentType Type` is replaced by `ResolvedDefinition Definition`: version, descriptors, compiled LLM schema and invoice compatibility.
  - Added: `ResolvedSupplierConfig? Supplier` (layout version, defaults, attributes, locale, llmHints), `SourceMetadata Source`, `StepParams`, and `ExtractedDocument Partial` (what earlier steps found).
  - `SupplierHints.LastApprovedInvoice` becomes `LastApproved` (a values view).
- `ExtractionOutcome(ExtractedDocument Document, ExtractionMethod Method, Flags, Cost)`. `ExtractedDocument` is a `JsonObject` FieldValue tree plus the definition id and version.
- A compatibility constructor taking an `Invoice` serialises it into the tree, so `EInvoiceDocumentExtractor` and `LlmInvoiceExtractor` compile unchanged in P0 and P1. The stored JSON of such an outcome is written from the invoice itself, not from the node tree: writing a `JsonObject` escapes characters the serialiser writes plainly (the `+` of a time-zone offset in a field's history becomes `+`), which would change stored documents by a byte. The tree stays the truth: the invoice is used only while the tree still holds what it held when it was built (or last read as an invoice), so a step that edits the tree in place or copies it with another tree (`with { Root = merged }`, section 9.2) stores and reads its edit. A read-only `ExtractionOutcome.Invoice` (the handed-over instance, or the tree read as an invoice) keeps the invoice consumers compiling too; it goes with the compatibility constructor.
- `IDocumentExtractor` keeps `Method`, `CanHandle` and `ExtractAsync`. It gains `string Kind` and is registered with keyed DI. `CanHandle` checks `Definition.IsInvoiceCompatible` instead of the enum (`EInvoiceDocumentExtractor.cs:21`, `LlmInvoiceExtractor.cs:28`).

### 9.2 The extract chain merges

`ExtractHandler` (`ExtractHandler.cs`) runs the resolved `processing.extract` steps in order. This replaces the static `PathOrder` (`:34`) and first-non-null-wins (`:56-69`).

- `ExtractedDocumentMerger` lets a step write only fields that are still Missing, unless the step declares `mode: override` for a field.
- When two steps disagree on a field already set with confidence of at least 0.8, the first value is kept, the status becomes `Conflict`, and both sources go into history.
- The `UNSUPPORTED_TYPE` gate (`:42-51`) remains only for documents with no resolved definition (Unknown). It becomes a blocking `TYPE_UNKNOWN` that routes the document to review. A `NEW_LAYOUT` document with `TypeSource=Proposed` has a resolved definition (the proposed type's), so Extract runs for it and pre-fills with `layout-prefill` (8.4); only a `NEW_LAYOUT` document for which no type could be proposed stays Unknown and skips Extract until `:match-layout`.

### 9.3 Step kinds

1. **`xml-einvoice`**. The UBL/CII parsers are unchanged for std fields.
   - New: an `EInvoiceExtras` capture of everything that is read and dropped today: `BuyerReference` (UBL `:41-43`, CII `:47-49`), `AccountingCost`, `ProjectReference`, `ContractDocumentReference`, `PartyIdentification`, which is not mapped today (`UblPartyMapper.cs:21-41`), and every `AdditionalItemProperty` name/value pair (`UblLineMapper.cs:25-27`, `ItemPropertyNames.cs`).
   - Supplier layouts bind these extras with `xml-path` rules.
   - `FieldSource.Path` records the element path (`XmlFields.cs:29-33`).
2. **`supplier-mapping`**: runs the field mapping of the layout selected by `ILayoutResolver`; see section 8. It yields nothing when no layout was selected.
   - **`layout-prefill`** replaces it on documents flagged `NEW_LAYOUT`, against the proposed type (`TypeSource=Proposed`) before review, and again against the reviewer's type when `:match-layout` changes the type or the basis layout (section 12.10). It does not use positions. It searches the whole document with the labels, value patterns and column headers of the supplier's other layouts (same type first, then the supplier's other types, then the type's `extraction.labels`), locates master-data values (VAT, IBAN, the supplier party) and values that were identical on every approved document of the supplier (for example `supplierCustomerRef`), and writes them as `Inferred` with their found position, so the reviewer only confirms them and the learner gets their positions.
3. **`llm`**.
   - **Invoice-compatible types** keep `LlmInvoiceExtractor`, today's prompt and examples, and `InvoiceExtractionMapper`. With no custom fields the request stays byte-identical to `invoice_extraction@1`, so `SchemaSnapshotTests` and the replay fixtures pass. Custom fields add the `x_custom` object, compiled once per `(tenant, type version)` as `invoice_extraction@1+x.<hash8>`, never per supplier. `DefinitionMapper` maps `x_custom`.
   - **Other types** use the new `LlmDefinitionExtractor`:
     - schema = `CompiledLlmSchema`;
     - prompt = new `extract-document.system.md`, which carries today's generic rules 1-3, 10 and 12 phrased against "the schema given to you", followed by the definition's `guidance` and examples;
     - Task (`ExtractionTaskBuilder`) = tenant and entity context, supplier `llmHints`, the supplier locale, supplier memory rendered from `LastApproved`, and the list of paths to fill in `fill-missing` mode;
     - parsing by a new `LlmAnswers.Parse(result, schema)` that returns a `JsonObject`, with the same refusal and truncation checks;
     - `DefinitionMapper` dispatches each value by kind to `ProvenanceLocator` (`LocateIdentifier`, `LocateNumber`, `LocateDate`, `LocateText`, `LocateAny` with enum variants, and `LocateLine` on the `anchor` field), with label hints from definition and mapping synonyms.
   - **`fill-missing` mode** keeps the type's schema, so the schema and the prompt cache stay per type version, and lists the paths to fill in the Task. When a layout version publishes, its `CoverageJson` (which fields its rules cover) produces one reduced schema per layout version, cached and hashed. That reduced schema is used only when `params.reducedSchema` is set. This saves tokens for well-mapped suppliers without creating one schema per document.
   - Per-type and per-supplier model, effort and maximum token overrides come through `LlmConfigResolver`, whose key changes from `LlmStage` to `(LlmStage, typeKey?)`.
4. **`defaults`**: writes defaults and supplier attributes as `Inferred` with `SourceMethod.Default`. It replaces the tenant-currency special case (`InvoiceExtractionMapper.cs:136-147`). A field can declare `requireExtracted`, which forbids defaults for it.
5. **`code-list`**, **`transform`**: unit aliases and value transforms, using the same op library as mappings.
6. **`http-lookup`** (P5): see section 10.
7. **`manual`**: a placeholder that marks `ExtractionMethod.Manual` when every value is Human.

`PurchaseOrder` and `DeliveryNote` documents, which are never extracted today, use `LlmDefinitionExtractor` from P5 onward.

### 9.4 Classification against tenant types

Classify runs its steps in this order and stops at the first that decides (section 8.5):

1. `TypeSource` already `Supplier`, `Human`, `Hint` or `Structured`: the type stands; only the layout is resolved among that type's layouts.
2. `classify.identification`, strict, always the first step of Classify (10.1):
   - **supplier known and layout-managed** (own or group Active layouts, or `layouts.required`): `ILayoutResolver` over its layout rules, then its group's. A rule that holds sets the layout and the type (`TypeSource=Identification`, confidence 1.0). No rule holds: `NEW_LAYOUT`, and the tenant rules and steps 3 and 4 run only to propose a type (`TypeSource=Proposed`, 8.4);
   - **supplier known without layouts** and `layouts.required` false, or **supplier unknown**: the tenant fallback rules (`TypeIdentificationRuleSet`). A rule that holds sets the type (`TypeSource=Identification`, `IdentificationRuleVersionId`).
3. `classify.keyword`: today's weighted keyword rules, per tenant. Not invoked when step 2 decided.
4. `classify.llm`: below `llmThreshold`, as today. Not invoked when step 2 or 3 decided.

Changes to the keyword and LLM steps:

- `KeywordRuleSet` is built per tenant from published types' `classification` blocks and cached by the hash of the type set. `KeywordRule.Type` becomes a string (`KeywordRule.cs:149`), `KeywordMatcher` takes the rule set as a parameter (`KeywordMatcher.cs:24`), and the static table (`KeywordRule.cs:156-253`) becomes the seed of the std templates.
- `LlmDocumentClassifier` gets a runtime schema whose enum is the candidate type keys plus `Unknown`, with the descriptions in the Task. `ClassificationOutcome.Type` becomes `DocumentTypeKey`. The candidate set is the supplier's allowed types when known, otherwise all active tenant types.
- `ClassifyHandler` depends on `IDocumentClassifier` (`ClassifyHandler.cs:30` injects the concrete class today) and keeps the type when `TypeSource` is `Supplier`, `Human`, `Hint` or `Structured`. Before the keyword call at `ClassifyHandler.cs:71` it runs `classify.identification`:
  - supplier known and layout-managed: `ILayoutResolver` (supplier, then group layouts);
  - supplier known, no layouts of its own or its group, and `layouts.required` false: `TypeIdentificationRuleSet` (cached per tenant by the hashes of the Active rule versions);
  - supplier unknown: `TypeIdentificationRuleSet`;
  - keywords and the LLM only after that, and only if nothing decided (or, under `NEW_LAYOUT`, to propose).
- Flag ownership. `NEW_LAYOUT` and `LAYOUT_AMBIGUOUS` are raised by two steps, `classify.identification` and `match.layout`, and each step owns the instances it raised, keyed by step id (10.3). `ClassifyHandler` adds them to its owned flags next to `TYPE_UNKNOWN` (`ClassifyHandler.cs:38`), and `MatchHandler` owns its own instances, so a reprocess from Classify, after a layout was saved, re-evaluates and clears both. When Match replaces the supplier, `match.layout` also clears the Classify instances raised for the old supplier (7.4). A reviewer's approval resolves an open `NEW_LAYOUT` (section 11).
- The splitter's keyword signals (`HeuristicDocumentSplitter.cs:162-167, 228-258`) take the tenant rule set. `LlmDocumentSplitter` fills its range-type enum from tenant types, or drops it.

## 10. Configurable handlers (R7)

### 10.1 Fixed phases, configurable steps

The runner (`PipelineRunnerHostedService.cs:113-127`), `PipelineStages.Next`, `DocumentStateMachine` and `DocumentStageHandler.Accepts(status)` are unchanged. Each phase handler runs the steps of its phase from the resolved pipeline:

| Phase | Configurable content | Built-in step kinds |
|---|---|---|
| Normalize | Accepted item kinds per channel; normalizer per kind | `normalize.pdf`, `normalize.image`, `normalize.einvoice`, `normalize.xml`, `normalize.json`, `normalize.tabular`, `normalize.office` |
| Split | `mode` auto / never / perPage / llm; LLM confidence threshold | `split.heuristic`, `split.llm` |
| Classify | Skip rules, threshold, candidate set | `classify.identification`, `classify.keyword`, `classify.llm` |
| Match | Provider order, conflict policy | `match.source`, `match.vatIban`, `match.name`, `match.layout` |
| Extract | Ordered extract, enrich and hook steps | `xml-einvoice`, `supplier-mapping`, `layout-prefill`, `llm`, `defaults`, `code-list`, `transform`, `http-lookup` |
| Validate | Rule set, review gate | `validate.schema`, `validate.rules`, `validate.duplicate`, `review-gate` |
| Publish | Bindings | `publish.outbox`, `publish.fanout` |

Supplier `pipelineOverrides` is an ordered patch with these operations:

- `{op:"disable", step:"llm"}`
- `{op:"param", step:"llm", params:{effort:"low"}}`
- `{op:"insertAfter", step:"map", with:{id:"erpVendor", kind:"http-lookup", params:{...}}}`
- `{op:"replace", step:"rules", with:{...}}`

Group overrides apply first, then supplier overrides. The `PipelineResolver` merges them into a `ResolvedPipeline`, hashed (`pipelineHash`) and cached per `(typeVersionId, layoutVersionId, settingsHash)`.

`classify.identification` is a **fixed, non-removable first step of Classify**. No type definition, group override or supplier override can `disable` or `replace` it, `insertAfter` cannot target a position before it, and nothing can be inserted ahead of it. Such an override is rejected at save with a validation error naming the step, and `PipelineResolver` refuses a stored pipeline that violates it (a defence for imported bundles). There is no per-type opt-out: at Classify time the type is not known yet, so a per-type switch would have no type to read. Only its own parameters are configurable, and none of them can skip supplier or group layout rules; a tenant that does not want tenant fallback rules simply has none Active, or excludes suppliers through `ExcludedSupplierIds`.

### 10.2 Plugins

Step kinds, rule kinds, strategies, transports and renderers are C# classes registered by key, each with a `ParamsSchema`:

```csharp
services.AddDocProcStep<IExtractStep, HttpLookupStep>("http-lookup", HttpLookupStep.ParamsSchema);
services.AddDocProcRule<SumEqualsRule>("sumEquals", SumEqualsRule.ParamsSchema);
```

This follows the `TryAddEnumerable` and factory pattern already used for `ILlmProviderFactory` (`PipelineServiceCollectionExtensions.cs:91-94`). `GET /step-kinds` and `GET /rule-kinds` expose the catalogue, so the UI renders parameter forms (rjsf) for new kinds with no UI code.

**`http-lookup`** calls a tenant destination through `TenantEndpointGuard`, with a 5 s timeout, a 256 KB response cap and the circuit breaker from `Microsoft.Extensions.Http.Resilience`. A JSONata expression builds the request, and a second one maps the response to field assignments with `Inferred` status. An example is resolving the ERP vendor id or a PO owner.

### 10.3 Runner and handler changes

- `ProcessDocument` gains an optional `ConfigHash`. Old messages without it, such as messages in flight during a deploy, resolve the current configuration and pin it. A retry whose pinned hash no longer resolves raises `CONFIG_CHANGED` and uses the pinned snapshot.
- `DocumentStageHandler.HandleAsync` (`:60-102`) builds a `StepContext` once (resolved definition, supplier config, resolved pipeline, source metadata via `IngestionItem`, `Ingestion` and `Channel`) and passes it to `RunAsync`.
- Each step owns its flag codes (`flags.Replace(document, owned, …)`), keyed by step id and rule id, and writes a timeline event `step_completed {stepId, kind, ms, cost}`. A code may be raised by more than one step (`NEW_LAYOUT` and `LAYOUT_AMBIGUOUS` by `classify.identification` and `match.layout`, 9.4); each step then replaces only the instances carrying its own step id.
- Global options (`PipelineOptions.LlmClassificationThreshold`, `KeywordClassifierOptions`, `HeuristicSplitterOptions`) become defaults that type or supplier parameters override.

A full step-graph engine (StepId, FlowVersion, reordering) is explicitly deferred until a concrete need appears.

## 11. Validation and approval safety

Validation has three layers. All of them produce `DocumentFlag` entries with a field path. The routing in `ValidateHandler.cs:288-327` is kept, as the `review-gate` step.

1. **Structural** (`validate.schema`) runs JsonSchema.Net on the values view against `CompiledJsonSchema`. JSON Pointer errors map to the path grammar (`/lines/2/lineNet` becomes `lines[2].lineNet`).
   - `FIELD_REQUIRED {path}` (blocking) replaces the hard-coded lists at `RequiredFieldsRule.cs:38-54` and `InvoiceExtractionMapper.cs:103-121`.
   - `FIELD_INVALID {path, keyword}` is blocking for required fields and a warning otherwise.
   - Bounds per document: one check (a field pattern, a field's schema keyword, one rule) raises at most 50 flags; the rest become one summary flag of the same code and severity at the first value left out, with `reason: capped` (they failed too) and `count`. A pattern stops matching at the cap (`reason: notChecked`). The field patterns and the `pattern` rules share one budget per validation (50,000 matches or 2 s of matching); a value past it is skipped, not failed, and one blocking `CHECKS_SKIPPED {reason: patternBudget, count}` sends the document to review.
   - The layout's `requiredFields` is applied as an overlay.
2. **Rule kinds** implement `IDocumentRule.Evaluate(DocumentView view, RuleParams p, ValidationContext ctx)`:
   - Generic kinds: `required`, `requiredIf`, `pattern`, `range`, `enumMember`, `compare`, `sumEquals` (for example `lines[*].lineNet.amount` equals `net.amount`, with a tolerance), `arithmetic` (`quantity*unitPrice-discountAmount = lineNet` per row), `dateOrder`, `datePlausibility`, `lookupExists`, `duplicate` (the role `duplicateKey`), `confidence`, `customerMatch` (receiving entities), and `expression` (a sandboxed JSONata predicate).
   - Phase in which each generic kind is registered (a type that uses a kind before then cannot publish, 5.7):
     - **P1**: `required`, `pattern`, `range`, `enumMember`, `dateOrder`, `datePlausibility`, `lookupExists`, `duplicate` (including `onMatch`), `confidence`, `customerMatch`. The coded invoice kinds come with `InvoiceRuleAdapter` in P0. The catalogue of kinds (`RuleKinds`, with each kind's `ParamsSchema`) is what publishing checks against; a declared kind whose evaluator is not registered yet does not run, and a document with such a rule is never auto-approved (`ValidationResult.RulesNotRun`).
     - **P5**: `requiredIf`, `compare`, `sumEquals`, `arithmetic`, `expression`, and the line-table kinds `tableComplete`, `unique`, `ordered`, `deltaToPrevious`. The review-gate keys `requirePassedRules`, `blockOnWarnings` and `lines` also arrive in P5.
   - Generic kinds for line-heavy types without totals, such as price lists (section 5.8):
     - `tableComplete {path, maxRejectedRows, everyPageInRange?, allowUnreported?}` reads the `DocumentTableReport` of the path, written by the `column-table` strategy or the review table tool (sections 6.2, 8.6). It raises `TABLE_ROWS_REJECTED` (blocking) and `TABLE_PAGE_NO_ROWS`, which is blocking when `everyPageInRange` is true and a warning otherwise. For rows that came from the LLM there is no report: the rule fails unless `allowUnreported` is set, in which case it passes on the report check and leaves the rows to the gate's `lines` conditions (`requireSource`, `requireLocated`);
     - `unique {path, scopeBy?}`: no two rows share a value (an article number listed twice with two prices);
     - `ordered {path, by, strict, then?}`: tier breaks ascend by quantity and their prices do not rise;
     - `deltaToPrevious {path, joinOn, maxChangePercent, maxChangedRowsPercent}`: compares row values with the last approved document of the same supplier and type (`SupplierHints.LastApproved`), joined on the anchor. It reports the changed rows in the flag params, so the review grid can filter to them. With no previous document it passes and says so.
   - **Coded invoice kinds** wrap today's rules, unchanged, through `InvoiceRuleAdapter` over `InvoiceProjection`: `invoice.totals`, `invoice.taxBreakdown`, `invoice.lineArithmetic`, `invoice.currency` and `invoice.customerMatch`. The std templates list them, so results are identical and the Domain validation tests (`InvoiceValidatorTests`) keep running unchanged.
     - In P0 the adapter wraps all eight rules of `InvoiceValidator.Default`, in its order: the five above plus `invoice.required`, `invoice.datePlausibility` and `invoice.confidence`. Their replacements (`validate.schema` with `FIELD_REQUIRED`, and the generic `datePlausibility` and `confidence` kinds) arrive in P1; without the three, P0's flags would not be identical. P1 retires them when the std templates switch to the replacements, with a parity test of the flags.
   - `ConfidenceRule` and `CurrencyRule` become generic through a `ProvenanceWalker` that replaces `Invoice.EnumerateAllFields`.
   - **Duplicates** use the existing index `(TenantId, SupplierId, DocumentNumberNormalized)` (`DocumentConfiguration.cs:32`) when the key is supplier plus `documentNumber`. For any other key the `documentNumber` component is read from that column and every other component from `DocumentFieldIndex`, which is why publish requires those fields to be `indexed` (5.7). This replaces `ValidateHandler.cs:118-131, 335-349` and the hard-coded `'invoiceNumber'` flag field (`:61`).
     - Nulls: a null component matches only a null component of the other document. A component bound to a required field that is missing is not compared; the check is skipped and says so, because `FIELD_REQUIRED` already blocks. When every component other than the supplier is null, the check is skipped too.
     - Sandbox, rejected and superseded documents are never matches.
     - `params.onMatch` is `block` (default; today's `DUPLICATE_SUSPECTED`, with the rule's severity) or `revision`. With `revision` a match raises `REVISION_OF {documentId, status}` with the rule's severity (`std.price-list` and `std.daily-specials` use warning plus `blockOnWarnings`, so the document goes to review). The reviewer resolves it with `POST /documents/{id}:resolve-revision {resolution}`: `supersedes` (the earlier document gets `SupersededByDocumentId`, `meta.supersedes` is set in the values view, and the new document is delivered normally), `additional` (both stand; recorded so the pair is not flagged again) or `duplicate` (the new document is rejected).
3. **Approval safety** (`review-gate`). A document auto-approves only if all of the following hold:
   - the type has `review.autoApprove` and at least one rule beyond structural checks;
   - there are no blocking flags, and confidence meets the threshold (tenant, then type, then supplier);
   - there is no `SUPPLIER_CONFLICT`, `SUPPLIER_AMBIGUOUS` or `SUPPLIER_SOURCE_UNVERIFIED`;
   - there is no `NEW_LAYOUT` or `LAYOUT_AMBIGUOUS` (both blocking, and checked by name as well, so a tenant cannot downgrade them by severity override), and the type is not `TypeSource=Proposed`;
   - for a layout-managed supplier, the layout was selected by its identification rule (recorded identification evidence, never a layout chosen by hand or by a suggestion), and the layout version is Active and past its warm-up (`ReviewWarmup`, set by the layout activation policy, decision 2 in section 24). An e-invoice (`TypeSource=Structured`, item kind `einvoice-xml`) needs no layout and is exempt from this condition; if a layout restricted to `einvoice-xml` was selected, that layout must be Active and past its warm-up;
   - every rule in the type's `review.requirePassedRules` ran and passed (a skipped rule does not count);
   - no rule in the type's `review.blockOnWarnings` raised a warning (for `std.price-list`: `r_delta`, unusual price changes, and `r_dup`, a revision of an earlier list). A human can still approve such a document in review;
   - when the type sets `review.lines`, every row's values come from an allowed source (`requireSource`), were located on the page where required (`requireLocated`), and no more than `maxLowConfidenceRows` rows are below the confidence threshold;
   - the supplier review mode is not `always`.

   The "at least one rule beyond structural checks" condition is what lets an invoice rely on `invoice.totals`. A type without totals has to name its substitute explicitly in `requirePassedRules`: for `std.price-list` that is table completeness, unique article numbers and positive prices (section 5.8). Until a tenant type names such rules, it cannot auto-approve.

   Tenant-defined types default to always-review, which keeps today's "non-invoices always reviewed" behaviour (`ValidateHandler.cs:290`). Approve is refused when a document has no definition or no canonical data; today an empty approval would be published.

   Correction (P2): the refusal is behind `configurableTypes` (principle 6), so a tenant that is not on the feature approves as before. `:approve` answers 409 with `code` `TYPE_REQUIRED` (the type is Unknown), `NO_DEFINITION` (the type has no published definition the tenant can use) or `NO_DATA` (no canonical data), and `DocumentDetail.approvalBlocked {code, message}` carries the same, so the review screen can say why before the click.

   Correction (P2, second review): "no canonical data" is a tree that holds no value, not only a missing tree. `:set-type` with prefill none stores the empty tree of the type (every field Missing, 12.1 step 3), and a gate that looked for a tree only let that document be approved and published empty. A tree holds a value when an envelope anywhere in it (rows and bags included) has one; rows of Missing fields do not count, nor the coded invoice record's own `kind`. It is the same test that switches map mode on by itself (12.3).

**Flags.** `FlagCodes.Catalogue` (`DocumentFlag.cs:63-83`) gains:

- `FIELD_REQUIRED`, `FIELD_INVALID`, `RULE_FAILED {ruleId, typeKey, message}`
- `CHECKS_SKIPPED {reason, count}` (blocking): checks did not run because the document reached a per-document limit (the pattern budget of layer 1 above).
- `SUPPLIER_CONFLICT`, `SUPPLIER_AMBIGUOUS`, `SUPPLIER_SOURCE_UNVERIFIED`
- Existing codes the text relies on, listed for completeness: `TYPE_UNKNOWN` (blocking; Classify raises it today, and Extract now raises it too in place of `UNSUPPORTED_TYPE`, which is retired, 9.2), `SUPPLIER_UNMATCHED` (unchanged; "create supplier from review", section 14) and `DUPLICATE_SUSPECTED` (unchanged; raised by the `duplicate` rule with `onMatch: "block"`).
- `NEW_LAYOUT {supplierId, proposedType, suggestions, pendingLayoutVersionId?}` (blocking; the same params as in 8.4): the supplier is known and layout-managed, but none of its Active layouts' identification rules holds. `pendingLayoutVersionId` is set when a Candidate layout's rule holds. It replaces the earlier draft's `MAPPING_NO_VARIANT` (info). Raised by `classify.identification` or `match.layout` (9.4). It is resolved by approval in review, either with "save as layout" (`learnLayout`), or with it unchecked, in which case the resolution is recorded as `approvedWithoutLayout`, nothing is learned, and the next document in that design gets `NEW_LAYOUT` again; or by a reprocess after a matching layout was activated.
- `LAYOUT_AMBIGUOUS {layouts}` (blocking): two Active layouts of the supplier hold with equal `Priority` and the same number of positive identifiers (8.4).
- `MAPPING_DRIFT` (warning), `ROUTE_DIFFERS`, `CONFIG_CHANGED`
- `REVISION_OF {documentId, status}` (severity of the `duplicate` rule with `onMatch: "revision"`): an earlier document with the same duplicate key exists; resolved by `:resolve-revision`.
- `TABLE_ROWS_REJECTED {page, rows}` (blocking), `TABLE_PAGE_NO_ROWS {page}` (blocking with `everyPageInRange`, otherwise a warning)
- `EXTERNAL_REJECTED`, `DELIVERY_FAILED`, `LLM_BUDGET_EXCEEDED`

`FlagCodes.Create` (`:88-96`) accepts `RULE_FAILED` and definition-scoped codes `X_<RULEID>` that come with a localised message instead of throwing. `flagMessage.ts` already falls back to the message text. The client keeps Ajv hints for instant feedback. `lineArithmetic.ts` is removed in favour of server rule results per path (`DocumentDetail.validation[]`).

## 12. Manual review mapping and learning (R5, R9, R10)

### 12.1 Unknown flow

1. Classify leaves the document `Unknown` with a blocking `TYPE_UNKNOWN` (`ClassifyHandler.cs:87-92`), unless the supplier configuration fixed the type. Extract skips by design and Validate creates the ReviewTask. The state machine is unchanged.
2. The review screen replaces the EmptyState (`ReviewScreen.tsx:346`) with a **Map this document** panel:
   - a type combobox (the supplier's types first, then the classifier's top three with confidences, then all active types);
   - a supplier picker (pre-filled from routing, plus "create supplier from this document");
   - "Start with: Empty / Let AI pre-fill". There is no "apply a layout" option: this panel is shown only for unknown suppliers and suppliers without layouts, and a layout whose identification rule did not hold is never applied (principle 9).
   - A document of a known, layout-managed supplier does not get this panel; it gets the **New layout** panel of section 12.10.
   - `POST /documents/{id}:assign-supplier {supplierId, remember?}` (the supplier picker) re-runs `classify.identification` for the assigned supplier through the reprocess path (from Classify, 202), exactly as if routing had found it: if one of its layouts holds, that layout is applied with its identification evidence (and a type the reviewer set, `TypeSource=Human`, restricts the search to that type's layouts); if the supplier is layout-managed and none holds, the document gets `NEW_LAYOUT` and the New layout panel replaces this one; otherwise this panel stays, with the tenant fallback rules re-evaluated. A document therefore takes the same identification path whether the supplier came from routing or from a reviewer.
   - Correction (P2): until identification exists (P4) there is nothing to re-run, and a reprocess from Classify would extract the document again (an invoice by the LLM). In P2 `:assign-supplier {supplierId}` therefore assigns the supplier at once (`SupplierMatchMethod=Human`, which the Match stage keeps on every later run), resolves `SUPPLIER_UNMATCHED`, re-runs the checks inline and answers 200; `remember` comes with source rules in P4, and so does the reprocess through `classify.identification`. "Create supplier from this document" is `POST /documents/{id}:create-supplier` (section 18), with the checks of `POST /suppliers`.
   - Correction (P2): the classifier offers no top three (one type and its confidence), and suppliers have no types before P4, so the type list is every type of the tenant with a published version. With an LLM classifier configured (the replay provider of the demo stacks, any real provider), a document of no type the classifier knows does not stay `Unknown`: the classifier's answer has no Unknown (`ClassifiedDocumentType`), and anything else is `Other`, whose type has no form. The panel is therefore shown for every document in review without a form or without data, `Other` and a failed extraction included, not only for `Unknown`; the classifier is unchanged.
   - Correction (P2, second review): the panel, like every review tool of P2 (the review actions' menu, map mode, the table tool), is new behaviour and is shown only to a tenant with `configurableTypes` (principle 6; `DocumentDetail.configurableTypes`). Any other tenant keeps the EmptyState at `ReviewScreen.tsx` ("This document type has no form to review yet"). The endpoints themselves are not gated: nothing before P2 called them, and an API client of any tenant may use them (an Unknown document typed as an invoice gets the coded record's empty record).
3. `POST /documents/{id}:set-type {typeKey, version?, prefill: "none"|"auto"}` sets the type key, `DocumentTypeVersionId` and `TypeSource=Human`. It writes a `setType` Correction and creates an empty canonical from the definition, with every field Missing (`DefinitionPatcher.CreateEmpty`). With `auto` it re-enters Extract through the existing reprocess path (`DocumentEndpoints.cs:508`) and returns 202. The screen remounts on the new version, using the existing `id:updatedAt` key.

### 12.2 Generated form

The new `web/packages/review` provides `specsFromDefinition(formSpec, canonical)`. It replaces `headerSections`, `partySpecs`, `lineColumns`, `isFieldApplicable` and `CanonicalInvoice` in `lib/canonical.ts:8-273`.

- `CanonicalForm` becomes a recursive `SectionRenderer`/`GroupRenderer`.
- `LineItemGrid` becomes `RepeatingGroupGrid`: any array, add and remove rows, virtualised above 200 rows.
- `FieldInput` becomes a widget registry keyed by kind.
- `useFieldLabel` reads definition labels, with i18n as fallback.
- `lineItemOf` (`fieldPath.ts:231-236`) is generalised.

Correction (P1): the compiled form spec (§5.5, artifact 3) carries neither the roles nor the members of a composite inside a composite (a party lists `address` by kind only), so the generated form cannot take either from it. The line check (quantity × unit price against line net) applies to a repeating group whose rows have `quantity`, `unitPrice` and `lineNet` (the invoice family), and nested members are expanded from the web package's copy of `FieldKinds.CompositeMemberKinds` (`packages/review/src/lib/formSpec.ts`), kept in step by the template snapshots the web tests render. A grid above 200 rows shows 100 rows a page instead of being virtualised: the grid's inputs are react-hook-form fields, and the repository has no virtualisation library. An invoice or credit note of a tenant without definitions (no `DocumentDetail.definition`) is drawn from a built-in form spec of the coded `Invoice` record (`legacyFormSpec`), so its review is unchanged.

### 12.3 Map from page

A "Map from page" toggle sits next to Words (`PageViewer.tsx:125-128`). It is on by default for `TypeSource=Human` documents with no values. Correction (P2): `DocumentDetail` does not carry `TypeSource`; map mode is on by itself for a document none of whose fields holds a value (what `:set-type` with prefill none leaves), and stays on after its first values are saved. A value taken from the page goes into the form and is saved with Save corrections, with its `source` (page, box, the text layer's words, the printed text), so several fields are taken and saved in one patch.

- The reviewer focuses a field (`focusedPath` already exists, `ReviewScreen.tsx:96`) or presses M. They click or shift-click words, or drag a rectangle. `FieldHighlightOverlay` gains `selectionMode`, `onRegionSelected(page, bBox, words)` and `onWordClick`. Word rects become pointer-active only in this mode (`FieldHighlightOverlay.tsx:68`). The image stays non-draggable.
- `toNormalized` (`overlay.ts:37-42`, unused today) converts the selection. It is hit-tested against `normalizedWords` and joined in reading order (y-bucket, then x). The client calls `GET /documents/{id}/pages/{n}/words?bbox=` for authoritative text plus parse candidates per kind, using the supplier locale. The preview reads, for example, "28.10.2026 → 2026-10-28" and can be edited before accepting.
- Enter assigns and moves to the next empty required field. Tab skips. Esc cancels.
- If there are no words under the box (a scanned page with a weak text layer), `POST /documents/{id}/regions:read {page, bBox}` crops the page PNG and runs `IOcrProvider` on that region only.
- Correction (P2): suppliers have no locale before their settings (P4), so `/words` and `regions:read` read the candidates in the request's `locale` (`de-DE`, `en-US`, …) or else the tenant's country. Boxes are in page units (0 to 1000 on both axes, like every stored box), a word carries its index in the stored text layer (what `source.words: [[page, i]]` names), and `source` says `pdfText`, `ocr` or `none` (a page without a text layer). The repository ships no `IOcrProvider` implementation (docs/01 plans Tesseract; docs/07 lists it as open), so `regions:read` answers `501` with `code: OCR_NOT_CONFIGURED` on a host without one; the region's words are placed back in page units and nothing is stored.
- **Match fields.** The reviewer drags a value chip from one field onto another; the correction op is `move`, with history kept.
  - Correction (P2): the correction is `{path, op: "move", from}` and needs no value: the value of `from`, converted to the kind of `path`, goes there with the place it was read from, and `from` is left Missing; both fields keep their history, and the move is one Correction row. Both ends are single fields, list entries or fields of rows; a member of a composite value or a bag key is corrected with `set`.

### 12.4 Table tool

1. The reviewer drags around the line table, or only around its header row.
2. `POST /documents/{id}/regions:table {page, bBox, columns?}` proposes column boundaries from word x-gaps and header words.
3. The reviewer moves the dividers, assigns each column to a field of the repeating group, excludes repeated header and footer rows, and can choose "continue on next page".
4. Rows arrive as `append` ops.

**Hundreds of rows over many pages** (price lists, section 5.8). The reviewer maps the columns once, on the first page, and never page by page:

- "Apply to all pages" sends `POST /documents/{id}/regions:table {page: 1, bBox, columns, pages: "1-*", repeatHeader: "auto", continuation: "appendToPrevious", groupHeader?: {target: "category"}, tierColumns?, skipRows?}`. The server runs the same `column-table` strategy as a layout (section 8.6): it finds the repeated header on each page and re-measures the columns there, reuses the page-1 boundaries on pages without a header, carries rows across page breaks, joins continuation lines, and fills `category` from group headings.
- The response is a table report, not only rows: rows per page, rejected rows per page with their text, pages without rows, and the first three rows of each page. The tool shows it as a page strip ("p. 1: 31 rows; p. 2: 34; p. 7: 2 rejected; p. 12: no rows"), and clicking a page jumps to it with the column dividers drawn.
- The reviewer fixes a rejected row in place, or adjusts a divider on that page only (`pageOverrides`), and re-runs; only the affected pages are recomputed.
- The result is written as one Correction with op `set` on `lines`, whose `SourceJson` holds the table specification and a copy of the report; the report itself replaces the `DocumentTableReport` of `lines` (6.2, `Origin=ReviewTool`), which is what `tableComplete` reads. Each row's FieldValues carry their page and box. Edits to single rows afterwards are ordinary `set`, `append` and `remove` ops. This avoids 800 `append` Corrections for one list.
- The specification is exactly what `:learn-layout` turns into the layout's `column-table` rule, so the second list from the same supplier needs no table work at all.
- The grid (`RepeatingGroupGrid`, virtualised) adds filters for "rejected or low-confidence rows", "rows changed against the previous list" (from `deltaToPrevious`) and "rows on page n".
- Correction (P2): this section names neither how the result is written nor several request members it relies on. As built: the request takes `headerRow {y0, y1}` (the header band on `page`), `rowAnchor` (the group's `anchor` by default), `path` (the type's `lineItems` role by default), `minAnchors`, `stopAt`, `pageMargins` and `locale` besides the members above; "continue on next page" is `pages`. The write is `regions:table` with `write: true`: the server reads the rows again from the mapping (so the stored report is its own) and writes them through `DefinitionPatcher.SetRows`, held to `maxItems`. `PATCH …/fields` also takes `set` on a whole group (`{path: "lines", value: [rows]}`), a reviewer replacing all rows, which deletes the group's report. The page strip after a reload reads `GET /documents/{id}/table-reports`, which section 18 did not list. Tier columns are on whenever the row has the tier group, and a column is a tier column when it names `tierMinQuantity` (8.6). Step 3's "excludes repeated header and footer rows" is `skipRows` and `stopAt` (the portal names a line by the text it starts with, a number standing for any number) and `pageMargins` for the pages after the mapping page; a box drawn only around the header row (step 1) reads each page down to the bottom margin, or the foot of the page, and each page of the report names the band it read (`top`, `bottom`). A page report lists at most 50 rejected lines with their text (the count is complete), and the answer at most the first 1,000 rows. Every run reads every page named; recomputing only the pages a `pageOverrides` change affects is not done.
- Correction (P2, second review): with only `skipRows`, `stopAt` and `pageMargins`, step 3's "excludes repeated header and footer rows" needed the reviewer's help on every list, and until then running titles and footers became categories, rows or text appended to a row, with nothing rejected. As built, the defaults do more:
  - **Repeated lines** (`repeatedLines: "skip"`, the default; `"keep"` reads them): a line above the first or below the last line of its page that reads as a row, printed at the same height with the same text on at least half of the document's pages (two at least), is a running title, page number or footer and is left out, unless it reads as a row itself (a value in the row anchor column and a readable price). Lines between rows are never running lines, however alike they look from page to page. A title printed on one page only is not recognised: it reads like any line until the reviewer leaves it out.
  - Correction (P2, third review) of the bullet above: "a number that is the page number or the page count stands for any number" was applied to each page's text by itself, so a footer carrying a date whose day equals one page's number (`gültig ab 01.04.2026` on page 1) read differently on that page and was appended to its last row. Two lines are compared token by token instead: a number matches when it is the same on both pages, or when on each page it is that page's own number. And a group heading that opens most pages at the same height is not a running line: a line above the page's first row that reads as a group heading (8.6) for another category than the current one is read as a heading, since leaving it out gave the rows under it the previous page's category. A running title shaped like a heading is therefore listed among the page's headings, to be left out by the reviewer; one that repeats the current category is left out, which changes nothing. Each page report lists the texts it left out (`repeatedTexts`).
  - Correction (P2, fourth review) of the bullet above: on a list that prints its header on page 1 only (8.6), the later pages are read from their top, so a running title printed above the rows on every page became a group heading on page 2 and the category of every row after it. Its place tells it apart: a repeated line whose copy on the mapping page is printed above the band read there (above the header row) is a running title and is left out on every page, whatever it looks like. Only a repeated line that has no such copy (its mapping-page copy lies inside the band, or it is not printed on the mapping page) may be read as a heading by the rule above; a title printed from page 2 on only, or "Rotweine trocken, Fortsetzung" opening the later pages, is still listed among the headings for the reviewer to leave out.
  - **Left-out columns**: a column without a field and without `tierMinQuantity` counts for nothing in any row decision; a line with words only there is skipped, not a heading, a continuation or a conflict.
  - **A lone anchor value** (only the row anchor column holds a value while the mapping fills others) is rejected as `anchorOnly`, not taken for an article.
  - The page report lists the texts of the group headings read (`headings`) and counts `repeatedLines` and `leftOutLines`; the page strip shows them for the page in view.
  - The proposal (step 2) reads its page with the request's `groupHeader`, which the portal sends when the row has a category.
  - **Limits**: the table tool runs in the API every tenant shares, which this section did not bound. A `skipRows`, `stopAt`, tier header or field pattern whose counted repetitions unroll to more than 500 atoms is refused (the NonBacktracking engine unrolls `{n,m}`, and nested ones multiply: a 23-character pattern can become ten thousand nodes and hundreds of MB per line); the same bound applies at publish (5.7) to field patterns, extraction hints and `pattern` rules. A pattern that runs out of time stops the read (400) instead of counting as no match on every line. One read runs within a time budget (10 s) and stops when the client goes away (422 `TABLE_READ_TOO_SLOW`), and `regions:table` and `regions:read` run a few at a time per tenant (429 beyond).
  - Correction (P2, third review): the limits above bounded the read, not the write. `set` on a whole group (`PATCH …/fields`) was held only to each group's own `maxItems`, and `tiers` has none, so one correction could write millions of nested rows. As built, a group inside a row without `maxItems` holds at most 100 rows, a tree at most 100,000 rows across all its groups (both checked before any row is built, for `set`, `append` and the table tool's write); `PATCH …/fields` takes at most 5,000 corrections and an 8 MB body, `regions:table` 2 MB (with `pages` at most 200 characters) and `regions:read` 16 KB.
  - Correction (P2, fourth review): within those limits one request was still quadratic. A remove cleared the row count, so alternating removes and appends recounted the whole tree once per pair, and every correction read a field's history and wrote it out again, one entry longer each time. As built, the row count is taken once per request and kept up to date through removes and appends, and a correction appends to the stored history as it is.

### 12.5 Any file

When `pageCount` is 0, PageViewer's empty state (`PageViewer.tsx:90-96`) is replaced by a `StructuredSourceViewer`, fed by `GET /documents/{id}/source`:

| Input | View | Source recorded |
|---|---|---|
| XML | Tree view | `path` (restricted navigator syntax) |
| JSON | Tree view | `path` (JSON Pointer) |
| CSV, XLSX | Virtualised grid | `cell` (`Sheet1!C7`), plus whole columns mapped to line fields |
| E-mail body | Text blocks with a text layer | as for text |

This needs new normalizers (`NormalizeHandler.cs:42-48` throws "unsupported format" today):

- tabular layer from CSV (Sylvan.Data.Csv) and XLSX (ExcelDataReader);
- tree layer for XML that is not an e-invoice (`NormalizeHandler.cs:111-120, 174-175` drop it today), under the `SecureXml` limits;
- tree layer for JSON;
- DOCX, HTML and TXT converted to PDF by a Gotenberg container, then following the PDF path.

`ItemKinds` (`Support/ItemKinds.cs`) and the accepted kinds per channel become configurable.

### 12.6 Save and learn

- `PATCH /documents/{id}/fields` takes `{path, op, value?, from?, source?, reason}`. `CanonicalPatcher` becomes `DefinitionPatcher` (12.8).
- `FieldValue<T>.Correct(value, by, at, FieldSource? source)` stores Human provenance with its location (done in P0); without a source it stores `FieldSource.Human` as before. The reason stays on the Correction row: the field history has no reason, and adding one would change the stored canonical JSON. Each Correction row stores `Op` and `SourceJson`.
- **Remember for this supplier** (`POST /documents/{id}:learn-layout`, which replaces the earlier draft's `:learn-mapping`) saves an identification rule and a field mapping as a layout. The identification rule comes from the identifiers the reviewer marked (section 12.9); if they marked none, the learner proposes one (the type keyword found in the page-1 title area, plus the supplier's VAT id when it is printed) and the dialog asks the reviewer to confirm it. The field mapping comes from the `AnchorLearner` in `DocProc.Mapping`, run over this document's sourced corrections and confirmed pre-filled values:
  - It looks for the nearest label words to the left on the same baseline, or above within 1.5 line heights, preferring definition and mapping synonyms, and emits `label-right` or `label-below`.
  - It derives a value pattern from the kind and the observed shape (`123 456789` gives `[0-9]{3}\s?[0-9]{6}`) and keeps a tolerant `region` rule as fallback.
  - A table assignment becomes `column-table`, and path or cell sources become structured rules.
  - It stores a similarity profile (page-1 header tokens, label positions, page size, item kind) for `LayoutSuggester` only.
  - Optionally, `LlmStage.Synthesize` (which exists but has no handler today) proposes more robust rules from the definition, the positioned text and the confirmed values.
  - It also proposes source rules, such as "mails from rechnungsversand@nordwind.example are Nordwind" or "folder X is type Y".
- The dialog shows the identification rule with its test result, the rules per field with mini previews, and the replay matrix. Saving creates a new layout (`Origin=Review`, version 1, Candidate), or a new version of an existing layout that merges only the changed fields and, if edited, the identification rule. It is activated under the one activation condition of section 8.3 (identification test passes, field agreement on its replay set), by whoever the layout activation policy allows (decision 2 in section 24), with the warm-up that policy sets.

### 12.7 Re-split and re-type in review

`POST /documents/{id}:split {parts:[{pages:[1,2], typeKey?}, {pages:[3], typeKey?}]}` reuses the sibling creation in `SplitHandler` (`SplitHandler.cs:333, 347-350`):

- The first part keeps the document id. The others become new documents of the same item.
- Each part enters Classify, or Extract with `TypeSource=Human` when it has a type.
- A Correction with op `split` records the operation.

This handles an Unknown PDF that actually contains a credit note and a delivery note.

Correction (P2): a typed part goes to Extract without the Match stage, so it keeps the document's supplier (an untyped part is matched again; a supplier a reviewer assigned is kept either way). The parts list the document's pages in order, each once, every part a run of consecutive pages, because a document is a page range of its item (`PageStart`, `PageEnd`). The document's data, list columns, field index and pipeline flags described its old pages and are cleared; its corrections stay with it as its audit, its open review tasks are skipped, and nothing is published (only unsettled documents are split).

The reverse, `POST /documents/{id}:join {documentIds}`, joins parts of the same item that are not settled back into one document, with the pages in item order. The first document keeps its id, the others are cancelled with a link to it, a Correction with op `join` records the operation, and the joined document re-enters Classify. It repairs a price list that the heuristic splitter cut at a group heading (5.8).

Correction (P2): "cancelled" needs a state and a link the data model did not have: the joined-away parts get the terminal status `Cancelled` and `Documents.JoinedIntoDocumentId` (§6.2), keep their data for the audit, lose their list columns and field index, and are never duplicates. (Fourth review: migrating down past `ReviewActions` makes those parts, their versions in the history and their events `Rejected`, since the build before has no `Cancelled`; the link survives in the `document.joined` event.) "The first document" is the one the request names (`{id}`), which takes every page in item order; the parts must make one unbroken run of the item's pages. Joined back into the whole item, the document is the whole file again (its normalised file restored, cancelled parts do not count as parts of a split), so it is read exactly as before the split.

### 12.8 DefinitionPatcher

`CanonicalPatcher` becomes `DefinitionPatcher`. What it keeps from `CanonicalPatcher` (`CanonicalPatcher.cs`):

- the path grammar (`:413-437`);
- editing the JSON tree while leaving untouched fields byte-identical (`:81-98`);
- all-or-nothing application, the history push and the `AppliedCorrection` audit (`:46`);
- the shorthand converters, now keyed by kind instead of CLR type (`:268-359`).

What changes:

- The reflection walk (`RootTypeFor`, `FindProperty`, `IsFieldValue`, `ElementTypeOf`) is replaced by the descriptor index, behind an `IFieldSchemaResolver` seam introduced in P0 with a reflection implementation first (`ReflectionFieldSchemaResolver`, `src/DocProc.Application/Documents/FieldSchema.cs`). The P0 schema nodes still carry the CLR type of each field and value, because the shorthand converters and the `FieldValue<T>` (de)serialisation are keyed by CLR type until they are keyed by kind; the descriptor implementation supplies kinds instead.
- New operations: `append`, `remove` and `move`, and creating fields that are missing.
- The patcher can edit taxBreakdown rows and charges, because they are declared groups with FieldValue leaves.
- After every patch it validates inline (structural checks plus rules), returns the flags, and refreshes the RoleProjector.
- The 409 "Nothing to correct" (`DocumentEndpoints.cs:540-544`) applies only to documents with no type.
- Correction (P1): `CreateEmpty` is `DefinitionTree.CreateEmpty` in `src/DocProc.Application/Documents` (the Extract stage needs it as well as the patcher), and the descriptor patcher is its own class (`DefinitionPatcher`) beside the coded `CanonicalPatcher` rather than a second `IFieldSchemaResolver`: the resolver's schema nodes carry CLR types, which a generic leaf does not have. A document is patched by its definition when it is stored as version 2, or when its type is definition-driven (§5.6) and it has no tree yet; every other document keeps the coded patcher.

### 12.9 Identify mode: "this identifies the type"

An **Identify** toggle (shortcut I) sits next to "Map from page" in the PageViewer toolbar. It uses the same selection machinery as map mode (12.3), but the selection becomes an identifier instead of a field value.

1. The reviewer clicks a word ("Rechnung"), shift-clicks a phrase, or drags a box around a logo line or a title.
2. `POST /documents/{id}/identifiers:propose {page, bBox?, words?, scope?: "layout"|"tenant"}` returns a proposed identifier with its location taken from where the selection sits: `{"text": "Rechnung", "page": 1, "region": "title"}` when the words are in the title band, otherwise a `bBox` around the selection with the default tolerance. If the selected text equals a master-data value of the supplier, the proposal is `{"supplierValue": "vatId"}` instead, which survives a VAT id printed with different spacing. With `scope: "tenant"` (the reviewer is building a tenant fallback rule, step 5) a `supplierValue` is never proposed; the selection becomes a `text` or `regex` identifier (8.5).
3. A chip appears in the **Identification** card of the side panel. Per chip the reviewer can widen the location (this area, anywhere on this page, any page), make it case-sensitive, turn it into a regex, allow one OCR error (only on an OCR page and never on the only positive chip; the chip then reads "tolerates one OCR error"), or move it to "must not contain" (`none`). A toggle above the chips switches **All of these** (`all`) and **Any of these** (`any`).
4. Every change runs `POST /identification-rules:test {rule, scope: {supplierId}, documentIds?|lastN: 50}` (the request shape of section 18; `scope: {groupId}` for a group layout and `scope: {tenant: true}` for a tenant rule). The card shows "Holds for this document ✓" and, against the supplier's last 50 documents: how many it matches per existing layout and type. A rule that also matches 38 documents of `nordwind-invoice-2023` is shown red with "add an identifier that tells them apart" and those documents' differing title words as suggestions.
5. The rule is saved with the layout on "Approve and remember" (`:learn-layout`, 12.6). A reviewer with `ConfigAdmin` can instead choose "Use for all suppliers without own layouts", which saves it as a Draft `TypeIdentificationRuleVersion` of a tenant rule for the chosen type (rejected if a chip is a `supplierValue`); the editor shows its test against the last 500 approved documents of the tenant before it is activated, and activation follows `ConfigApprovals.identificationRules` (15.2).

The marked evidence of the current layout is drawn in a distinct colour whenever Identify mode is on, so a reviewer looking at a correctly identified document sees why it was identified.

### 12.10 Matching a new layout

Follow-up F3: a known supplier sends a design that none of its layouts recognises. The document arrives in review with the blocking `NEW_LAYOUT`. It was not processed with the nearest layout; the system only proposes. When a type could be proposed, the document arrives already pre-filled against it (`TypeSource=Proposed`, 8.4); otherwise it arrives empty and the pre-fill runs when the reviewer picks the type.

**Example.** Nordwind starts sending invoices from a new web shop. The From rule identifies Nordwind (DMARC pass), but the title reads "RECHNUNG / INVOICE" in a different place and Nordwind's VAT id is printed in the footer of page 2, so neither `nordwind-invoice-erp` nor `nordwind-invoice-2023` holds.

1. **Queue.** The inbox shows a "New layout" badge with the supplier, and the review queue groups pending `NEW_LAYOUT` documents per supplier (`GET /documents?flag=NEW_LAYOUT&supplierId=`, section 18), so a reviewer handles one design once. Documents whose flag names a pending Candidate layout are grouped under that layout ("waiting for activation of nordwind-invoice-webshop").
2. **New layout panel** (instead of the Unknown panel of 12.1):
   - "Nordwind Großhandel GmbH sent a document in a design we have not seen."
   - **Type**: preselected from the proposal (tenant rules, keywords, LLM) and ordered: the types of the supplier's existing layouts first, then the classifier's top three, then all active types.
   - **Suggestions** from `LayoutSuggester`, for example "Similar to nordwind-invoice-2023 (78 %)". Choosing one only pre-selects the type and seeds the field pre-fill with that layout's labels; it does not apply its positions.
   - **Pending layout.** When the flag carries `pendingLayoutVersionId` (a Candidate whose rule holds here), the panel leads with "This matches nordwind-invoice-webshop, saved by j.weber on 3 Nov and waiting for activation" and offers **Confirm against pending layout** instead of creating another layout. Confirming pre-fills from the Candidate's field mapping (as a basis, shown with "pending layout" chips), and on approval adds this document to the Candidate's replay set; it never creates a second layout for the same design.
   - **Save as**: "a new layout of this supplier" (default; the existing layouts stay Active) or "a new version of layout X" (for a redesign that replaces X everywhere; X's old version is retired once the new one activates). Choosing "new version of X" immediately tests the new identification rule against X's last 50 approved documents. If it does not hold for some of them, the panel warns "X's current design (38 of the last 50 documents, most recent 2 days ago) would no longer be recognised; those branches would get NEW_LAYOUT" and recommends "new layout" instead; saving as a new version then needs an explicit confirmation, which is recorded.
3. `POST /documents/{id}:match-layout {typeKey, basedOnLayoutId?, saveAs: "newLayout"|"newVersion", targetLayoutId?, pendingLayoutVersionId?}` sets the type (`TypeSource=Human`, a `setType` Correction) and returns 202. It re-runs the pre-fill below through the reprocess path only when the type differs from the proposed one, or a basis layout (`basedOnLayoutId` or `pendingLayoutVersionId`) was chosen; confirming the proposed type keeps the pre-fill the document arrived with. The screen remounts.
4. **Pre-fill.** Each value carries a chip naming where it came from, and every value the system could place on the page is highlighted there:
   - **Supplier master data**: the supplier party (name, VAT id, IBAN, address) and attributes bound to fields (ERP vendor id, the default cost centre). Each value is searched on the page with `ProvenanceLocator`; when found, it gets its position.
   - **Labels and patterns of the supplier's other layouts** (the `layout-prefill` step, 9.3): "Rechnungs-Nr.", "Kd.-Nr." and their value patterns are searched anywhere in the document, positions ignored; column headers seed the table tool's dividers.
   - **Known custom-field values**: values that were identical on all of the supplier's approved documents (for example `supplierCustomerRef = 123456789`) are searched by value and placed.
   - **The LLM** reads the document against the chosen type in `fill-missing` mode, when the tenant has an LLM key configured (`TenantLlmPolicies`); otherwise this source is skipped and the panel says so.
   - The type's `defaults` and the supplier's `settings.defaults`.
5. **Adjust.** The reviewer corrects values by selecting text on the page (12.3), maps or fixes the table (12.4), and marks the identifiers (12.9). The learner proposes an identification rule from the title words and the VAT position; the Identification card shows it already tested.
6. **Approve.** "Approve and save as layout" is checked by default. Approval and `:learn-layout` run in one request: the document is approved, and the identifiers and field positions become version 1 of `nordwind-invoice-webshop` (`Origin=NewLayoutReview`). Its status follows the layout activation policy (decision 2 in section 24) under the one activation condition of section 8.3: the identification test passes (the rule holds here and matches no approved document of the supplier's other layouts) and the field replay over its replay set (this document, plus siblings already re-tested against it) agrees. With the recommended policy it is Active straight away when the reviewer is a `MappingAuthor`, with warm-up 0; otherwise it is a Candidate, and the tenant's `MappingAuthor`s are notified. Unchecking "save as layout" approves the document only; `NEW_LAYOUT` is resolved as `approvedWithoutLayout` (section 11).
7. **Siblings.** Other pending `NEW_LAYOUT` documents of the same supplier are re-tested against the new rule (`GET /supplier-layouts/{id}/versions/{v}/matching-documents?state=pending`). If the layout is Active, the panel asks "3 more documents in review match this layout: reprocess them now?", which calls `POST /documents:bulk-reprocess {filter: {documentIds}, fromStage: "Classify", configMode: "current"}`, so they are identified and filled by the new layout. If it is a Candidate, the siblings are attached to it instead (their flags get `pendingLayoutVersionId`), the Candidate's card on the Suppliers sheet lists them, and activation offers the same reprocess for all of them. Siblings are never reprocessed against a Candidate, which would only raise `NEW_LAYOUT` again.
8. **The next document** in that design is identified by the rule, filled by the layout's field mapping and validated. It goes to review only when a validation fails, or while the layout version is inside its review warm-up (`ReviewWarmup`, set by the layout activation policy, decision 2 in section 24; 0 by default for a layout activated from this flow by a `MappingAuthor`, section 8.3). While the layout is still a Candidate, the next document gets `NEW_LAYOUT` with `pendingLayoutVersionId` and the "confirm against pending layout" option of step 2.

## 13. Output mappings and delivery (R4)

### 13.1 Engine

- JSON targets use JSONata through Jsonata.Net.Native. A P3 spike confirms function coverage and enforceable limits. The fallback is jsonata-js inside Jint, which has native time, memory and recursion limits.
- Custom functions:
  - `$table(key, value, default?)` (LookupTable, honouring ValidFrom);
  - `$fmtDate(date, pattern)`, `$fmtNum(n, decimals, decSep?, groupSep?)`;
  - `$today()`, the current date (`yyyy-MM-dd`) in the tenant time zone (`TenantSettings.TimeZone`), so conditions about validity do not depend on the UTC date of `$now()`;
  - `$field(path)` returning `{status, confidence, method, normalized}`;
  - `$required(value, message)`, which aborts the render with a clear, permanent error.
- Limits: 2 s per render on a worker task with cancellation, 5 MB output, a recursion depth cap, and no I/O functions.
- Text formats (CSV, fixed-width, XML) use Fluid (Liquid, sandboxed) in P6. Its input is a JSONata pre-shaped object, and XML output is validated against `TargetXsd` with `XmlResolver = null`.
- The UI offers a visual field map (target path, source field, formatter, lookup) that compiles to JSONata, and a code mode.
- Preview is always rendered by the server, so preview and delivery cannot drift.

### 13.2 Example: "erp-ap-import" v2

```jsonata
(
  $isCredit := meta.typeKey = "CreditNote";
  $s := $isCredit ? -1 : 1;
  {
    "header": {
      "docNo": roles.documentNumber,
      "docType": $isCredit ? "GS" : "RE",
      "docDate": $fmtDate(roles.documentDate, "dd.MM.yyyy"),
      "companyCode": entity.code,
      "currency": roles.currency,
      "vendor": {
        "id": $required(supplier.attributes.erpVendorId, "Supplier has no ERP vendor id"),
        "customerRef": doc.supplierCustomerRef,
        "vatId": doc.supplier.vatId
      },
      "refDocNo": doc.originalInvoiceReference,
      "reason": $table("erp-credit-reason", doc.creditReason, "SON"),
      "storeNo": doc.ext.storeNo,
      "costCentre": doc.ext.costCentre,
      "gross": $s * $abs(roles.total.amount),
      "dmsId": meta.documentId,
      "revision": meta.version
    },
    "rows": [ doc.lines.{
      "item": supplierSku,
      "text": $substring(description, 0, 40),
      "qty": $abs(quantity),
      "uom": $table("erp-uom", unit, "ST"),
      "net": $s * $abs(lineNet.amount)
    } ]
  }
)
```

The `[ ... ]` around `doc.lines` forces an array even for a single line; the editor lints for this singleton case.

Rendered for the Nordwind credit note:

```json
{"header":{"docNo":"GS-2026-0042","docType":"GS","docDate":"28.10.2026","companyCode":"1000","currency":"EUR",
 "vendor":{"id":"V-10042","customerRef":"123456789","vatId":"DE811111111"},"refDocNo":"RE-2026-9917","reason":"RET",
 "storeNo":"0417","costCentre":"4711","gross":-119.00,"dmsId":"6c1f...","revision":7},
 "rows":[{"item":"4711","text":"Kaffee Crema 1kg","qty":2,"uom":"ST","net":-100.00}]}
```

When `TargetSchemaJson` is set, every render is validated with JsonSchema.Net at preview and before sending. A violation is a permanent failure, raises `DELIVERY_FAILED` and shows the schema errors.

### 13.3 Destinations and transports

`IDeliveryTransport` implementations are keyed by transport.

- **http**
  - Config: `{method, urlTemplate, headers, timeoutSeconds, successCodes, auth}`.
  - Auth: `none | apiKeyHeader | basic | bearer | oauth2ClientCredentials` (token cached until expiry) ` | hmacSha256` (signature over timestamp and body). mTLS comes later.
  - Every call goes through `TenantEndpointGuard`, moved from `src/DocProc.Llm/Providers` to `src/DocProc.Infrastructure/Http` (done in P0) and still used by the LLM path: https only, public addresses only, re-checked after DNS resolution, no proxy, no redirects. `DocProc.Infrastructure` references `DocProc.Llm`, so the guard's LLM-specific helpers (`EnsureAllowed`, `ToLlmException`) moved to `LlmEndpointGuard` in `src/DocProc.Infrastructure/Llm`, and the two provider adapters now reference `DocProc.Infrastructure`.
  - The demos' `mock-erp` (P0, docs/09 "Mock ERP") listens on plain http at a private address (`http://mock-erp:8080` in compose, `http://localhost:8086` under the AppHost), which the guard refuses. P3 therefore adds a development-only allowance: hosts listed in a `Delivery:DevelopmentHosts` setting, honoured only in the Development environment, may be reached over http. Production keeps the guard unchanged.
  - Headers sent: `Idempotency-Key`, `X-DocProc-Document-Id`, `X-DocProc-Revision`.
  - **Multipart** bodies can include the original file (`{"parts":[{"name":"data","mapping":"self"},{"name":"file","content":"original"}]}`).
  - Optional `responseMapping` (JSONata on the response body) captures `DocumentExternalRef.ExternalId`.
- **blob / file drop**: a path and file-name template; the file is written to a temporary name and then renamed.
- **sftp** (SSH.NET, P6): same path and file-name templates.
- **email** (P6): the payload as an attachment.
- **pull**: the payload is stored. Clients read `GET /api/v1/feeds/{destinationId}?after=` and acknowledge with `POST /feeds/{destinationId}:ack`, using the API key scope `deliveries:read`, restricted to that destination.

### 13.4 Multi-call recipes

When a receiver needs several calls, `OutputBinding.RecipeJson` lists ordered steps:

```json
{ "steps": [
  { "name": "vendor", "method": "GET", "url": "https://erp.example/api/vendors?vat={{supplier.vatId}}",
    "capture": { "vendorNo": "value[0].number" }, "skipWhen": "$exists(supplier.attributes.erpVendorId)" },
  { "name": "invoice", "method": "POST", "url": "https://erp.example/api/purchaseInvoices", "body": "mapping",
    "capture": { "erpId": "id" } },
  { "name": "attachment", "method": "POST", "url": "https://erp.example/api/purchaseInvoices/{{$vars.erpId}}/attachments",
    "body": { "multipart": [ { "name": "file", "content": "original" } ] } }
] }
```

- `RecipeStateJson` persists the step reached and the captured variables, so a retry resumes at the failed step.
- Each step gets its own idempotency key, of the form `key:stepName`.
- `$vars` is available to the mapping.

### 13.5 Bindings, fan-out and triggers

- `PublishHandler` (`PublishHandler.cs:27-60`) resolves the matching bindings for the event and the document's type, supplier, group, channel and entity, plus any `Condition`.
  - Bindings without an `ExclusiveGroup` all fire (fan-out).
  - Within an exclusive group the most specific binding wins: supplier, then group, then channel, then entity, then type, then tenant, then `Priority`.
- For each binding it renders now and stores a snapshot in `BlobContainers.Exports`, so the audit trail is complete and redelivery is reproducible. It inserts the `OutboxDelivery` rows and sets `OutboxMessage.FannedOutAt` in one `SaveChanges`, then enqueues `ProcessDelivery(TenantId, DeliveryId, Attempt)`.
- **Outbox in the decision transaction.** Approve and reject now insert the `OutboxMessage` inside their own transaction; before P0 they committed and then enqueued on a lossy in-process queue (`DocumentEndpoints.cs:371-375, 426-430`). Auto-approval is a decision too: the validate stage adds the row in the save that sets `AutoApproved`. The Publish enqueue becomes a nudge (`DocumentOutbox`, `src/DocProc.Infrastructure/Persistence/DocumentOutbox.cs`).
- `PublishSweeperHostedService` finds settled documents whose current settlement has no handed-on row: either no row of its event type written at or after the settlement, or only rows with `FannedOutAt` null. These are one condition, not two lists: a null `FannedOutAt` row of an earlier settlement (a rejection that was reprocessed) is not a lost publish, so the sweeper never looks at rows of documents that are no longer settled. It fixes lost publishes for every tenant; before P0 only demo tenants were resumed (`DemoDataHostedService.cs:215-298`). It runs next to the runner (`Pipeline:PublishSweepInterval`, first sweep one interval after start), leaves settlements younger than `Pipeline:PublishSweepGrace` to the normal nudge and ignores those older than `Pipeline:PublishSweepLookback`, so a first start on an old database does not republish history. It queues `ProcessDocument(Publish)` with `Origin = "publish-sweeper"`; the publish stage is idempotent per settlement and records `recoveredBy: "publish-sweeper"` in its timeline entry, which is how the P0 demo shows the recovered approval. Two publishers of one settlement (the nudge and a sweep, or the sweepers of the API and the worker, which both run one in compose) cannot both publish it: an existing row is claimed through its `FannedOutAt` token, and a missing row (a decision from before P0) is inserted together with an update of the document under its `Version` token (section 6.2).
- **Known effect until Service Bus lands.** In the Production topology the API runs no runner (`Pipeline:RunInProcess` defaults to false there), so the nudge of an approve or reject goes to an in-process queue nothing reads. Every human decision is then published by the worker's sweeper, at least `Pipeline:PublishSweepGrace` later, with the sweeper's warning and `recoveredBy: "publish-sweeper"`. Nothing is lost, but there "recovered" is the normal case; the signal means a lost message only where the deciding host runs a runner (Development, compose, the AppHost, and auto-approvals in the worker). The durable transport removes the effect (docs/03 "Queue modes").
- The platform mapping `std.event` renders today's thin `DocumentEventMessage`, for receivers that want only the event.

### 13.6 Delivery semantics

- `DeliveryHandler` runs in `PipelineRunnerHostedService` next to `ProcessDocument` and `ProcessIngestion`.
- Results are classified as:
  - delivered: 2xx;
  - retryable: network errors, 408, 425, 429 and 5xx, honouring `Retry-After`;
  - permanent: other 4xx, a target-schema violation, a render error, or `$required` failing.
- Backoff is exponential with jitter (1 min, 5 min, 15 min, 1 h, 3 h, 6 h, then every 12 h) up to 15 attempts or 72 h. After that the delivery is DeadLettered, `DELIVERY_FAILED` is raised and a timeline event is written.
- `DeliverySweeper` picks up rows by `(Status, NextAttemptAt)`. On SQL Server it claims with `UPDLOCK, READPAST`; on SQLite in dev it uses an optimistic `Version` check.
- **Ordering.** `KeyedWorkGate` serialises on `destination:document`. A pending delivery of an older document version is set to `Skipped` once a newer version exists.
- **Re-approval** after a reprocess follows the binding's `OnReapproval`: `Resend` (new revision, new idempotency key, the default), `Skip`, or `Update` (`UpdateMappingId`, a different method such as PUT, and `meta.event = document.updated`).
- `POST /deliveries/{id}:redeliver {mode: "snapshot"|"rerender", mappingVersion?}`.
- Every attempt writes a `DeliveryAttempt` row and a timeline event `delivery_succeeded` or `delivery_failed`. These appear in the document's Deliveries tab and in `GET /documents/{id}/events`.

### 13.7 Batches

With `Mode = Batch`, `BatchJson` is `{"schedule":"CRON_TZ=Europe/Berlin 50 17 * * 1-5","maxItems":500,"fileNameTemplate":"ap-{{$now}}.csv"}`.

- Deliveries are created as `Pending` with a `BatchId`.
- `BatchSchedulerHostedService` closes the window and renders the mapping once over `{"batch":{"documents":[ …values views… ]}}`. This usually uses Liquid for a CSV in P6, or JSONata for a JSON array.
- It sends one file. Item deliveries are marked Delivered together with the batch.

### 13.8 Acknowledgements from the receiving system

Asynchronous status comes back in one of two ways:

- **Callback**: `POST /api/v1/integrations/callbacks/{destinationId}`. It is authenticated by the destination's callback secret (HMAC header or bearer token, two active secrets for rotation). A JSONata `callbackMapping` turns the body into `{documentId|externalId, status, message}`.
- **Polling**: `statusPoll {urlTemplate, intervalMinutes, mapping}` on the destination, run by the delivery worker while the external ref is `Pending`.

`DocumentExternalRef.Status` then becomes `Accepted`, `Posted` or `Rejected`. A rejection (for example "duplicate in ERP" or "vendor blocked") raises `EXTERNAL_REJECTED {destination, message}` on the document and creates a ReviewTask of kind `IntegrationIssue`. The document stays settled. The reviewer either corrects the data, reprocesses from Validate and re-approves (a new revision is delivered according to `OnReapproval`), or resolves the issue with a note.

### 13.9 Integration templates

Platform `OutputMapping` rows (`TenantId` NULL) ship:

- `std.event` (today's thin event);
- `generic-ap-json` (header plus rows by role, as above);
- `generic-ap-csv` (P6, Liquid).

Tenants copy them. Templates for specific accounting systems are added only after they have been checked against that vendor's current API documentation in a sandbox. They are a roadmap item, not something this design assumes.

A platform `generic-article-prices-json` (P5) ships with the price-list templates; it is the mapping of section 13.10 without the tenant-specific lookups.

### 13.10 Example: price lists to an article-price import

Price lists usually go somewhere else than invoices: the ERP's article master or purchasing price conditions, a merchandise system, or a web shop. Acme's bindings:

| Binding | Type | Output mapping | Destination | Notes |
|---|---|---|---|---|
| `ap-invoices` | `Invoice`, `CreditNote` | `erp-ap-import` v2 (13.2) | `erp-ap` (http) | |
| `article-prices` | `price-list` | `article-price-import` v1 | `erp-articles`, `POST https://erp.example/api/article-prices` | `OnReapproval: Update` |
| `pos-specials` | `daily-specials` | `pos-promotions` v1 (below) | `pos-hub`, `POST https://pos.example/api/promotions` | `Condition: roles.validUntil >= $today()`, so a list approved after its day has ended (by a reviewer, despite `r_not_expired`) produces no delivery |

`article-price-import` v1, written against the receiver's import format (a header plus one row per article, tier prices as scales, discontinued articles as a separate list):

```jsonata
(
  $root := $;
  {
    "header": {
      "vendorNo": $required(supplier.attributes.erpVendorId, "Supplier has no ERP vendor id"),
      "priceListId": roles.documentNumber ? roles.documentNumber : supplier.attributes.erpVendorId & "-" & roles.validFrom,
      "validFrom": $fmtDate(roles.validFrom, "yyyyMMdd"),
      "validTo": roles.validUntil ? $fmtDate(roles.validUntil, "yyyyMMdd") : "99991231",
      "currency": roles.currency,
      "priceType": doc.priceBasis = "Gross" ? "B" : "N",
      "mode": doc.listScope = "Changes" ? "UPSERT" : "REPLACE",
      "dmsId": meta.documentId,
      "revision": meta.version
    },
    "prices": [ doc.lines[$not(status = "Discontinued")].{
      "vendorArticleNo": supplierSku,
      "ean": gtin,
      "text": $substring(description, 0, 40),
      "group": category,
      "orderUnit": $table("erp-uom", unit, "ST"),
      "contentQty": packSize,
      "contentUnit": $table("erp-uom", packUnit, "ST"),
      "priceUnit": priceQuantity ? priceQuantity : 1,
      "price": price.amount,
      "validFrom": $fmtDate(lineValidFrom ? lineValidFrom : $root.roles.validFrom, "yyyyMMdd"),
      "scales": [ tiers.{ "fromQty": minQuantity, "price": price.amount } ]
    } ],
    "delist": [ doc.lines[status = "Discontinued"].supplierSku ]
  }
)
```

Notes on the expression:

- `$not(status = "Discontinued")` rather than `status != "Discontinued"`: in JSONata a comparison with a missing value is false, so `!=` would drop every row that has no status.
- `$root` keeps the document root reachable inside the row block, for the list-level validity.
- `[ tiers.{…} ]` yields `[]` for a row without tiers; a missing `gtin` drops the `ean` key, which this receiver accepts. A receiver that requires the key gets `"ean": gtin ? gtin : null`.
- The same mapping serves `daily-specials` if a tenant binds it there, because it reads `roles.validFrom`, `roles.validUntil` and `doc.lines`, which both types share. Acme instead sends specials to its POS hub with `pos-promotions`.

Rendered for a Nordwind price list (412 rows; three shown):

```json
{"header":{"vendorNo":"V-10042","priceListId":"PL-2027-01","validFrom":"20270101","validTo":"99991231","currency":"EUR",
 "priceType":"N","mode":"REPLACE","dmsId":"8d2a...","revision":3},
 "prices":[
  {"vendorArticleNo":"4711","ean":"4006381333931","text":"Kaffee Crema 1kg","group":"Kaffee","orderUnit":"KAR","contentQty":6,
   "contentUnit":"ST","priceUnit":1,"price":50.00,"validFrom":"20270101","scales":[{"fromQty":10,"price":48.50},{"fromQty":50,"price":46.00}]},
  {"vendorArticleNo":"4712","ean":"4006381333948","text":"Espresso Bohne 1kg","group":"Kaffee","orderUnit":"KAR","contentQty":6,
   "contentUnit":"ST","priceUnit":1,"price":56.40,"validFrom":"20270101","scales":[]},
  {"vendorArticleNo":"5120","text":"Filtertüten Gr. 4","group":"Zubehör","orderUnit":"KAR","contentQty":40,
   "contentUnit":"PCK","priceUnit":100,"price":31.90,"validFrom":"20270201","scales":[]}],
 "delist":["3990","3991"]}
```

Size and delivery:

- 412 rows render to about 110 KB. The type's `maxItems` (10,000 rows until P6, section 5.8) keeps every list that passes validation under the 5 MB render cap (13.1): 10,000 rows of the longest shape render to about 4.5 MB. From P6, when the receiver limits the request size or the template raises `maxItems` to 20,000, the binding's recipe (13.4) splits the array: `{"steps":[{"name":"prices","method":"POST","url":"https://erp.example/api/article-prices","body":"mapping","chunk":{"path":"prices","size":1000,"repeat":["header"]}}]}`. Each chunk has its own idempotency key `key:prices:<n>`, and a retry resumes at the failed chunk.
- `mode: REPLACE` lets the receiver replace the supplier's prices from `validFrom` on; a `Changes` list is sent as `UPSERT`. A corrected and re-approved list is resent with `OnReapproval: Update`, whose mapping version sends the same body to `PUT …/article-prices/{priceListId}`.
- The `TargetSchemaJson` of the receiver is checked before sending, as for invoices (13.2); a schema violation in row 380 is a permanent failure that names the row.

`pos-promotions` v1, Acme's tenant mapping for today's specials, written against the POS hub's promotion format (one promotion per list, one item per article, validity as local times):

```jsonata
(
  $root := $;
  {
    "promotionId": $required(supplier.attributes.erpVendorId, "Supplier has no ERP vendor id") & "-"
                   & $fmtDate(roles.validFrom, "yyyyMMdd") & (roles.documentNumber ? "-" & roles.documentNumber : ""),
    "vendorNo": supplier.attributes.erpVendorId,
    "title": doc.title ? doc.title : "Tagesangebote " & supplier.name,
    "validFrom": roles.validFrom & "T00:00:00",
    "validTo": roles.validUntil & "T23:59:59",
    "timeZone": "Europe/Berlin",
    "currency": roles.currency,
    "replaces": meta.supersedes,
    "items": [ doc.lines[$not(status = "Discontinued")].{
      "vendorArticleNo": supplierSku,
      "ean": gtin,
      "text": $substring(description, 0, 40),
      "unit": $table("erp-uom", unit, "ST"),
      "promoPrice": price.amount,
      "regularPrice": previousPrice.amount,
      "minQty": minOrderQuantity
    } ],
    "dmsId": meta.documentId,
    "revision": meta.version
  }
)
```

Rendered for a Nordwind specials list of 3 November with 18 rows (two shown); the second row has no printed old price, so `regularPrice` is absent, which the POS hub accepts:

```json
{"promotionId":"V-10042-20261103","vendorNo":"V-10042","title":"Tagesangebote 03.11.","validFrom":"2026-11-03T00:00:00",
 "validTo":"2026-11-03T23:59:59","timeZone":"Europe/Berlin","currency":"EUR","replaces":null,
 "items":[
  {"vendorArticleNo":"4711","ean":"4006381333931","text":"Kaffee Crema 1kg","unit":"KAR","promoPrice":44.90,"regularPrice":50.00},
  {"vendorArticleNo":"6230","text":"Kakao Pulver 500g","unit":"ST","promoPrice":3.49}],
 "dmsId":"a41c...","revision":2}
```

- `promotionId` is stable per supplier, day and list number, so a list that supersedes an earlier one of the same day (`REVISION_OF` resolved as `supersedes`, section 11) carries `replaces` with the earlier document id and the hub replaces the promotion instead of adding a second one.
- `timeZone` is a constant of Acme's mapping; a tenant with several time zones takes it from `entity.attributes`.
- The binding's `Condition` uses `$today()`: a list whose `validUntil` is before today in the tenant time zone produces no delivery and the binding is listed as "not applicable" in the document's Deliveries tab.

## 14. Master data from the systems we plug into

- **Supplier import.** `POST /suppliers:import` takes CSV or JSON plus a column map. It runs as a dry-run diff first (create, update, conflict), then applies. Match keys are `attributes.erpVendorId`, then `vatId`, then IBAN.
- **Scheduled sync.** A `MasterDataSource` has an http, blob or upload transport and a JSONata mapping from external records to `{name, vatId, taxId, ibans, attributes, entityAttributes}` or to lookup entries.
  - `MasterDataSyncHostedService` runs it on its cron schedule and writes a `MasterDataRun` with a diff.
  - Conflict policy is per attribute: `externalWins` (the default for ERP ids), `localWins`, or `flag`.
  - Flagged conflicts go to a "Master data conflicts" list.
- Lookup tables (cost centres, tax keys, units) sync the same way, with `ValidFrom` handling.
- **Create supplier from review.** On `SUPPLIER_UNMATCHED`, the reviewer clicks "Create supplier from this document". The form is pre-filled from the extracted supplier party (name, VAT, IBAN, address). If an `http-lookup` vendor search is configured, it suggests the ERP vendor id. Optionally the reviewer adds a source rule for the sender.
  - Correction (P2, permission decision): this did not say what a reviewer may set, and `POST /suppliers` is a tenant admin's. A reviewer creates the supplier with its name and the identifiers printed on the document (VAT id, tax id, IBANs) and its currency; email domains, aliases and `alwaysReview`, which steer the Match stage and the review of every later document of the tenant, stay with a tenant admin (403 for a reviewer), so review and supplier master data keep their separation of duties. That the identifiers are printed on the document is not checked.

## 15. Configuration lifecycle

### 15.1 Versioning and pinning

- Types, supplier layouts (identification rule and field mapping together), tenant identification rules and output mappings have immutable published versions.
- Supplier settings are snapshotted into each document's `AppliedConfigJson`.
- Deliveries pin `MappingVersionId`.
- Drafts use `DraftRevision` with `If-Match`; a conflicting save returns 412.
- **Rollback** republishes or re-activates a previous version's content as a new version. It is audited.

### 15.2 Audit and approvals

- Every create, edit, publish, activate, retire, import or sync writes a `ConfigAuditEntry` with a JSON diff, visible under Settings > Audit and per entity.
- `TenantSettings.ConfigApprovals` (`{types, layouts, identificationRules, outputMappings, destinations}`) enables four-eyes approval per entity type. Publishing or activating then creates a `ConfigApproval`, and a second member with the role approves it. `layouts` covers supplier and group layout versions; `identificationRules` covers tenant fallback rule versions, which re-type documents of every supplier without own layouts; `outputMappings` covers output mapping versions and bindings.
- The recommended production default is four-eyes for destinations, output mappings and identification rules.

### 15.3 Regression suites and sandbox

- Any approved document can be marked as a **golden document**, either from review or automatically for the first approved document of each supplier and type.
- When a type version, layout version (field mapping or identification rule), tenant identification rule, output version, prompt version or model changes, `RegressionRun` re-runs the affected golden documents in sandbox mode with the candidate configuration and compares field values and output hashes. Publish shows the report, and blocks when `TenantSettings.RequireGreenRegression` is set.
- **End-to-end sandbox.** `POST /sandbox/runs {file, source: {sender, recipient, folderPath, subject}, config: "draft"|"published"}` creates an `IsSandbox` document. It runs routing, normalize, extract, validate and output rendering with no side effects, and returns the trace (rule hit, layout and identification evidence, or `NEW_LAYOUT` with the identifiers that failed, steps, flags, rendered payloads per binding). Operators use it before go-live.

### 15.4 Export, import and promotion

- `GET /config/bundle?include=types,layouts,identificationRules,sourceRules,outputs,bindings,lookups,flows` exports a signed JSON bundle. Secrets are excluded and replaced by placeholders.
- `POST /config/bundle:import {bundle, mode: "dry-run"|"apply"}` maps keys and supplier match keys (VAT, `erpVendorId`), creates everything as drafts and candidates, and runs the regression suite.
- This is the promotion path from staging to production.

### 15.5 In-flight behaviour

- Pinned documents finish on their pinned versions.
- A message without `ConfigHash`, for example one created by an older build during a deploy, resolves the current configuration and pins it.
- Bulk reprocessing (`POST /documents:bulk-reprocess {filter, fromStage, setType?, configMode: "pinned"|"current"}`) runs as a background job with progress and a per-tenant concurrency cap, for moving historical documents into a newly defined type or layout.

## 16. Security, privacy and cost controls

- **No tenant code.** JSONata and Liquid run sandboxed with time, size and depth caps. Regex uses `NonBacktracking` with timeouts. XML access goes through the restricted navigator under the `SecureXml` limits, not XPath.
- **Outbound HTTP** goes only through `TenantEndpointGuard`. Secrets live only in `ISecretStore`, never in `ConfigJson`.
- **Prompt injection.** Tenant text (guidance, descriptions, `llmHints`) is length-capped and placed in delimited Task sections, never in the system prompt.
- **Spoofing.** `RequireSenderAuth` is on by default for sender rules. Content corroboration raises `SUPPLIER_CONFLICT`. Free-mail domains are on a platform deny list for `SenderDomain`. Forward unwrapping is limited to trusted forwarder domains.
- **Permissions** (`src/DocProc.Api/Auth/Roles.cs`). TenantAdmin implies all of these:
  - `ConfigAdmin`: types, processing, lookups (P1: a policy satisfied by TenantAdmin and SuperAdmin; there is no role that grants it alone yet);
  - `IntegrationAdmin`: destinations, outputs, bindings, master data;
  - `MappingAuthor`: activating layouts (including layouts saved from a `NEW_LAYOUT` review), retiring layouts, exclusive source rules;
  - `MappingEditor`: proposing candidate layouts and identification rules, and default-mode source rules; granted to Reviewers by default;
  - tenant fallback identification rules need `ConfigAdmin`, because they affect every supplier without own layouts.
- **API key scopes** (`ApiKeys.cs:7-15`, `Tenancy.cs:72-76`) add `ingest:hint`, `deliveries:read` (optionally restricted to a destination) and `config:read`.
- **Sensitive fields.** A field marked `sensitive` is masked in timeline payloads, flag params, delivery response excerpts, LLM usage logs and the audit diff. It is still delivered in payloads.
- **Retention.** `TenantSettings.Retention.deliveryPayloadDays` (default 90) and `sandboxDays` (7) are enforced by `RetentionCleanupHostedService`, which deletes snapshots in the Exports container.
- **Deletion.** GDPR deletion of a document cascades to `DocumentFieldIndex`, payload snapshots, `DeliveryAttempt` excerpts and golden documents. `DocumentExternalRef` keeps ids only.
- **Destination secret rotation.** `PUT /destinations/{id}/secret` supports a two-secret overlap for callbacks.
- **LLM cost** is controlled at three levels:
  - Per type: `processing.llm.maxCostPerDocument`.
  - Per tenant: a monthly cap in `TenantLlmPolicies`.
  - Per supplier layout: effort and model overrides.

  When a cap is reached, the step is skipped with `LLM_BUDGET_EXCEEDED` and the document goes to review. Alerts fire when a type's Unknown rate or LLM spend exceeds its 14-day baseline by more than 50 %.

## 17. Onboarding and operations view

`GET /insights/automation?from=&to=`, backed by a nightly `SupplierDailyStats` roll-up plus live queries, reports:

- per supplier: document count, auto-approval rate, corrections per document, layouts per type with status (none, Candidate, Active, Degraded), open `NEW_LAYOUT` documents and LLM cost;
- **suppliers without layouts ranked by volume**, which tells the product owner whom to onboard next;
- Unknown rate per channel, source rule and type;
- degraded and drifting layouts;
- open `NEW_LAYOUT` documents per supplier (a design to onboard), layouts that have not matched for 180 days (candidates to retire; advisory only, nothing retires automatically), Candidate layouts with documents waiting for their activation, and tenant identification rules that reviewers often override;
- delivery failure rate and dead letters per destination;
- open master-data conflicts.

The portal screen is Insights > Onboarding, with actions on each row: "open sample", "create layout", "add source rule".

## 18. API changes

All endpoints are under `/api/v1` and are regenerated into `web/packages/api-client`, following the API-first rule in docs/05.

**Changed**

- `DocumentSummary.type` and `DocumentDetail.type` become string type keys (`DocumentModels.cs:18, 77`) and gain `typeName` and `typeVersion`.
  - `DocumentSummary` also gains `validFrom`, `validUntil`, `lineCount` (from the new denormalised columns, 6.2), `supersededBy`, `reviewPriority` and `reviewDueAt` (the open ReviewTask's `Priority` and `SlaDueAt`), so the inbox can show a price list's validity and row count and sort specials by due time.
  - The list filter accepts any tenant key instead of `Enum.IsDefined` (`DocumentEndpoints.cs:129, 718`), plus `?field.<key>=value` for indexed fields (at most 5 such filters, one value each, since each is one more `EXISTS` over `DocumentFieldIndex`), `?flag=<code>` (for example `NEW_LAYOUT`), `?supplierId=`, `?pendingLayoutVersionId=` and `?sort=reviewDue` (priority, then due time).
  - `DocumentDetail` gains `definition {key, version, formSpec}` (with an ETag), `supplierMatch {method, ruleId, describe, confidence}`, `appliedConfig`, `layout {id, key, version, status, scope, identification {matched, evidence[]}}`, `validation[]`, `deliveries[]` and `externalRefs[]`.
  - `canonical` stays the stored tree.
  - Correction (P1): section 19 offers indexed custom fields as inbox columns and filters, which needs two things this list did not have: which fields of a type are indexed, and their values per row. `DocumentTypeDetail` gains `indexedFields [{path, kind, label}]` (of the published version, else the latest), and the list takes `?fields=<path>,<path>` (at most 10) and then returns `DocumentSummary.fields {path: value}` as `DocumentFieldIndex` holds the values (identifiers normalised); without the parameter `fields` is null.
- `PATCH /documents/{id}/fields`: `FieldCorrection` becomes `{path, op, value?, from?, source?, reason}`.
- `POST /documents/{id}:reprocess`: `fromStage` adds `Match`, plus `configMode: "pinned"|"current"`.
- `POST /ingestions`: adds `supplierId` and `documentTypeKey` (scope `ingest:hint`).
- Channels: `connectorKey`, `defaultSupplierId`, `defaultDocumentTypeKey` and `defaultReceivingEntityId`, with config validated per connector. `GET /connectors` lists connector keys with their config schemas.
- Suppliers: adds `groupId`, `attributes`, `settings` and `entityAttributes[]`. `emailDomains` is deprecated.
- Settings: adds `supplierAttributeSchema`, `features`, `configApprovals` (`{types, layouts, identificationRules, outputMappings, destinations}`), `requireGreenRegression`, `retention`, `trustedForwarderDomains`, `timeZone`, `layouts.requiredByDefault` and `layoutActivation` (decision 2 in section 24).

**New**

- **Types**
  - `GET /document-templates`
  - `GET|POST /document-types`, with `{from:{template, mode:"adopt"|"copy"}}` or `{definition}` (the catalogue now includes `std.price-list` and `std.daily-specials`)
  - `GET /document-types/{key}`
  - `GET|POST /document-types/{key}/versions`
  - `PUT /document-types/{key}/versions/{v}` (draft, If-Match)
  - `POST …/versions/{v}:validate`, `:publish`, `:approve`
  - `GET …/versions/{v}?include=formSpec,jsonSchema,llmSchema`
  - `GET …/versions/{v}:diff?against=`
  - `POST /document-types/{key}:upgrade-base`
  - `POST /document-types/{key}/versions/{v}:upgrade-documents`
  - `GET /rule-kinds`, `GET /step-kinds`
  - `GET /document-types/{key}/resolved-pipeline?supplierId=`
- **Lookups**: `/lookup-tables` CRUD; `PUT /lookup-tables/{key}/entries` (CSV upload).
- **Routing and suppliers**
  - `/suppliers/{id}/source-rules` CRUD
  - `POST /routing:test {channelId?, sender?, recipients?, folderPath?, subject?, fileName?}`, which returns the winning rule, the runners-up, the conflicts and the type
  - `/supplier-groups` CRUD
  - `/receiving-entities` CRUD
  - `POST /suppliers/{id}:clone-config`
  - `POST /suppliers:import`
  - `/master-data-sources` CRUD, plus `:run` and `:dry-run`
- **Supplier layouts and identification rules**
  - `GET|POST /suppliers/{id}/layouts?type=`, `GET|POST /supplier-groups/{id}/layouts`
  - `GET /supplier-layouts/{id}`, `PATCH /supplier-layouts/{id} {name, priority, receivingEntityId, itemKinds, status}`
  - `GET|POST /supplier-layouts/{id}/versions`, `PUT …/versions/{v}` (draft; the body holds `identification` and the field mapping)
  - `POST …/versions/{v}:replay {documentIds?|lastN}` (field matrix over the replay set plus identification test, 8.3), `:shadow`, `:activate`, `:retire`
  - `GET /supplier-layouts/{id}/versions/{v}/matching-documents?state=pending|approved` (documents the version's rule holds on: the sibling re-test of 12.10 and, with `approved`, the "new version of X" check)
  - `POST /identification-rules:test {rule, scope: {supplierId?|groupId?|tenant: true}, documentIds?|lastN, typeKey?}`, returning per document `{matched, layoutId?, typeKey, evidence[]}` and the overlaps with other layouts
  - `GET|POST /document-types/{key}/identification-rules` (tenant fallback), `PATCH /identification-rules/{id} {name, priority}`, `GET|POST /identification-rules/{id}/versions`, `PUT /identification-rules/{id}/versions/{v}` (draft, If-Match; rejects `supplierValue`), `POST /identification-rules/{id}/versions/{v}:test` (the 500-document test), `:activate` (subject to `configApprovals.identificationRules`), and `POST /identification-rules/{id}:disable`
  - `GET /suppliers/{id}/layouts:resolve?documentId=`: dry-runs `ILayoutResolver` on a document and returns every candidate's result, for support
- **Review**
  - `POST /documents/{id}:set-type {typeKey, version?, prefill: "none"|"auto"}`
  - `POST /documents/{id}:assign-supplier {supplierId, remember?:{conditions…, mode}}` (202; re-runs `classify.identification` for that supplier from Classify, 12.1)
  - `POST /documents/{id}:split`, `POST /documents/{id}:join {documentIds}`
  - Correction (P2): `POST /documents/{id}:create-supplier` ("create supplier from review", section 14), with the body and checks of `POST /suppliers`; section 14 and the P2 plan need it and this list had no route for it. A reviewer may not set `emailDomains`, `aliases` or `alwaysReview` (section 14).
  - Correction (P2, fourth review): `POST /suppliers` bounded the name only, so a reviewer could store any number of IBANs of any length through `:create-supplier`, and every Match stage run of the tenant loads every supplier. As built, a supplier write takes at most 20 IBANs (each checked as an IBAN's format), 20 e-mail domains (each a domain name) and 50 aliases, and the review actions a body of at most 64 KB (docs/05).
  - `POST /documents/{id}:resolve-revision {resolution: "supersedes"|"additional"|"duplicate"}` (`REVISION_OF`, section 11)
  - `POST /documents/{id}:revalidate`
  - `GET /documents/{id}/pages/{n}/words?bbox=`
  - `POST /documents/{id}/regions:read`, `POST /documents/{id}/regions:table` (adds `pages`, `repeatHeader`, `continuation`, `groupHeader`, `tierColumns`, `skipRows`, `pageOverrides`; returns rows plus the per-page table report, section 12.4)
  - Correction (P2): `GET /documents/{id}/table-reports` (the stored `DocumentTableReport`s, the page strip after a reload), and `regions:table` takes `write: true` to store the rows as one correction (section 12.4).
  - `GET /documents/{id}/source`
  - `POST /documents/{id}:learn-layout {saveAs: "newLayout"|"newVersion", targetLayoutId?, layoutKey?, identification, fields?: [paths], sourceRules?, confirmNotRecognised?}` (replaces `:learn-mapping`); also accepted as part of `:approve {learnLayout: {...}}`, so approval and saving are one transaction
  - `POST /documents/{id}/identifiers:propose {page, bBox?, words?, scope?: "layout"|"tenant"}` (`tenant` never proposes `supplierValue`)
  - `POST /documents/{id}:match-layout {typeKey, basedOnLayoutId?, saveAs, targetLayoutId?, pendingLayoutVersionId?}` (202; re-runs Extract with `layout-prefill` only when the type or the basis changes, 12.10)
  - `GET /documents/{id}/layout-suggestions` (ranked by `LayoutSuggester`; informational)
  - `POST /documents/{id}:golden`
  - `GET /documents/{id}/output?mappingId=&version=`
  - `POST /documents:bulk-reprocess`
- **Outputs**
  - `/destinations` CRUD, plus `:test` and `PUT /destinations/{id}/secret`
  - `/output-mappings` CRUD, plus versions and `:publish`
  - `POST /output-mappings:preview {expression|mappingVersionId, documentId|sampleValues}`, returning `{output, errors, schemaErrors, referencedPaths}`
  - `/output-bindings` CRUD
  - `GET /deliveries?documentId=&destinationId=&status=` (PageCursor), `GET /deliveries/{id}`, `POST /deliveries/{id}:redeliver`
  - `GET /feeds/{destinationId}`, `POST /feeds/{destinationId}:ack`
  - `POST /integrations/callbacks/{destinationId}` (anonymous, secret-authenticated)
- **Governance**
  - `GET /config/audit`
  - `GET /config/bundle`, `POST /config/bundle:import`
  - `GET|POST /golden-documents`
  - `POST /regression-runs`
  - `POST /sandbox/runs`, `GET /sandbox/runs/{id}`
  - `GET /insights/automation`
- **Admin (platform)**: `/admin/document-templates`, `/admin/output-templates`, `/admin/code-lists`.

## 19. UI changes

**Shared.** A new `web/packages/review` holds the generated form, `RepeatingGroupGrid`, PageViewer with map mode and Identify mode, `FieldHighlightOverlay` with selection, the `IdentificationCard` (chips, all/any toggle, live test), the multi-page table tool with its page strip, and `StructuredSourceViewer`. They move out of `customer-portal/src/components/review`, so the admin console uses the same code.

- `app-shell` `queryKeys` gains `documentTypes`, `supplierLayouts`, `identificationRules`, `outputs`, `deliveries` and `insights`.
- Parameter forms (step and rule params, destination, channel and master-data config) use `@rjsf/core` with a small shadcn widget theme. The review form stays on react-hook-form plus FieldSpec, because it needs provenance badges, focus, highlighting and correction diffs.
- Monaco (`@monaco-editor/react`) is used for the JSON and JSONata tabs. The `jsonata` npm package is used only for client-side syntax checks.

**Customer portal**

1. **Review screen** (`ReviewScreen.tsx`)
   - The Invoice/CreditNote gate and the cast (`:60-61`) are removed.
   - The header shows type@version, the supplier with its match-method chip, the layout and its version with an identification chip (7.3), and the role-bound number, date and total.
   - The Unknown panel, map mode, table tool and structured viewer are as described in section 12.
   - Identify mode and the Identification card (12.9); the **New layout** panel for `NEW_LAYOUT` documents, with type picker, suggestions, "confirm against pending layout X", "save as new layout / new version of X" (with the warning when X's current design would no longer be recognised), pre-fill source chips on every value, and the sibling dialog after approval (12.10).
   - A `REVISION_OF` panel showing the earlier document side by side, with "replaces it", "additional list" and "duplicate" (section 11).
   - `ReviewActions` gains: Change type, Assign or create supplier, Split, Join parts, Remember for supplier (layout), Approve and save as layout, Mark as golden, Re-send output.
   - For line-heavy types the grid shows row filters (rejected, low confidence, changed against the previous list, page n) and the table report strip.
   - New tabs: Deliveries (status, destination, attempts, payload view, external ref, redeliver), Output preview, and Data (JSON, read-only).
2. **Settings > Document types**: a catalogue of platform templates (Adopt / Copy) and tenant types. The type editor is a full page with these tabs:
   - Fields: a tree with dnd-kit reordering, kind picker, labels per locale, required, indexed, sensitive, extraction labels and pattern, roles;
   - Rules, Processing (the step list per phase, including the `review` block with `requirePassedRules` and `lines`), Classification and LLM guidance;
   - Identification: the tenant fallback identification rules for this type, each with its chips, its versions, its test against recent documents ("would have matched 212 price lists, 0 other documents"), excluded suppliers, status, approval state and override count;
   - Preview: the generated review form on a sample document;
   - Versions: diff, validate, and publish with the dependents and regression report;
   - JSON.
3. **Suppliers sheet** (`routes/suppliers.tsx:248-373`), with these tabs:
   - Routing: source rules plus a "Test routing" box (renamed from "Identification", so the word is kept for identification rules);
   - Settings: types, locale, review policy, and **Layouts required** (inherit / yes / no, showing the tenant default), which makes a source-routed supplier layout-managed before its first layout exists (6.4);
   - Attributes: per entity, with a form generated from `SupplierAttributeSchema`;
   - Layouts: grouped by type, one card per layout ("Invoice: nordwind-invoice-erp, nordwind-invoice-webshop, nordwind-invoice-2023"), each with its identification rule in words ("All of: 'Rechnung' in the page-1 title; our VAT id. None of: 'Gutschrift'"), versions with status, origin, stats (identified, applied, corrected), last match, and the replay matrix with the identification test. The visual editor opens a sample document and draws the identification evidence and every field rule's hit box. Candidate versions show the pending documents attached to them and an "Activate and reprocess N documents" action. Actions: add layout from a document, new version, retire, change priority, test against a document;
   - Outputs: supplier-scoped bindings;
   - Activity.

   There is also a Supplier groups list.
4. **Settings > Integrations**:
   - Destinations: a typed form per transport, write-only secret, Test, callback setup;
   - Output mappings: three panes. Left, the source tree (the type's fields plus `roles`, `supplier`, `entity`, `source`, `meta`) with values from a sample document. Middle, the visual map or the JSONata code view. Right, the server-rendered preview with schema errors. Plus versions and publish;
   - Bindings, including recipes and batch schedule;
   - Lookup tables (grid, CSV import, ValidFrom);
   - Master data sources and conflicts;
   - Delivery log (filters, redeliver, dead letters).
5. **Settings > Channels** (`ChannelsTab.tsx`): typed per-connector forms replace the raw JSON (`:220-233`), with full edit and default supplier, type and entity pickers.
6. **Settings > Governance**: audit log, approvals, bundle export/import, golden documents and regression runs, sandbox runs.
7. **Inbox** (`documents.index.tsx:26`): types come from the API; indexed custom fields are offered as columns and filters; a "New layout" filter and badge (`?flag=NEW_LAYOUT`), grouped by supplier; price lists show validity (`validFrom`/`validUntil` of `DocumentSummary`) and row count (`lineCount`) instead of a total; daily specials show an "urgent" badge and sort by their review due time (`?sort=reviewDue`); superseded lists are greyed out with a link to the replacing list.
8. **Insights > Onboarding**: section 17.
9. **UploadDialog**: supplier and type pickers, and the PortalUpload channel.

**Admin console**

- `documents/$documentId.tsx` embeds the shared review package: read-only by default, editable for platform staff.
- `tenants/$tenantId.tsx` tabs gain Document types, Supplier layouts and Integrations.
- New Platform templates and Output templates editors.
- The hard-coded lists in `lib/documents.ts:24` and `lib/review.ts:10-29` come from the API.

**i18n.** Chrome strings stay in the bundles. Field, section and enum labels come from definitions. `resources.test.ts:60-68, 100` checks chrome keys plus label completeness of the shipped templates.

## 20. Changes to existing code, file by file

| File | Change |
|---|---|
| `src/DocProc.Domain/Documents/DocumentEnums.cs` | `DocumentType` enum becomes `WellKnownTypeKeys` plus `DocumentTypeKey`; `TypeSource` gains `Supplier`, `Hint`, `Identification` and `Proposed` |
| `src/DocProc.Domain/Fields/FieldValue.cs`, `FieldSource.cs` | `Correct(..., FieldSource? source)`; `FieldSource.Path`, `RuleRef` and `Step`; `SourceMethod` gains `Rule` and `Default` |
| `src/DocProc.Domain/Invoices/Invoice.cs`, `InvoiceLine.cs`, `Common/Party.cs` | `[JsonExtensionData]`; std paths frozen by the parity test |
| `src/DocProc.Domain/Validation/*` | `IDocumentRule`, `RuleRegistry`, `InvoiceRuleAdapter`, generic rule kinds (including `tableComplete` with `allowUnreported`, `unique`, `ordered`, `deltaToPrevious`, `duplicate` with `onMatch`); `InvoiceValidator.Default` becomes the std template rule list |
| `src/DocProc.Domain/Flags/DocumentFlag.cs` | New codes (including `NEW_LAYOUT`, `LAYOUT_AMBIGUOUS`, `REVISION_OF`, `TABLE_ROWS_REJECTED`, `TABLE_PAGE_NO_ROWS`); `UNSUPPORTED_TYPE` retired in favour of `TYPE_UNKNOWN`; `Create` accepts `RULE_FAILED` and `X_*` codes with a message |
| `src/DocProc.Definitions` (new) | Format, meta-schema, compiler, templates (including `Templates/std.price-list.json` and `Templates/std.daily-specials.json`), descriptors |
| `schemas/identification-rule.v1.json` (new) | Meta-schema of identification rules, snapshot-tested |
| `src/DocProc.Application/Abstractions/IDocumentExtractor.cs`, `IDocumentClassifier.cs`, `IEInvoiceParser.cs` | Generic contracts (section 9.1); extras from the parser |
| `src/DocProc.Application/Pipeline/PipelineMessages.cs` | `ProcessDocument.ConfigHash`; `ProcessDelivery`; `DocumentEventMessage` v2 |
| `src/DocProc.Application/{Outputs,Documents,Configuration}` (new) | `IDocumentDataViewBuilder`, `RoleProjector`, `IEffectiveConfigResolver`, `IOutputRenderer`, `IDeliveryTransport` |
| `src/DocProc.Infrastructure/Persistence/Entities/{Documents,Ingestion,Channels,Suppliers,Tenancy}.cs` | Additive columns (section 6) |
| `.../Entities/{DocumentTypes,Layouts,Outputs,Routing,Governance,MasterData}.cs` (new) | New entities and EF configurations (`Layouts.cs`: `SupplierLayout`, `SupplierLayoutVersion`, `TypeIdentificationRule`, `TypeIdentificationRuleVersion`; `DocumentTableReport` in `Documents.cs`) |
| `.../CanonicalDocumentSerializer.cs` | Generic tree read and write; typed overloads kept for projections |
| `.../DocProcDbContext.cs`, `Tenancy/RowLevelSecurity.cs` | Query filters; nullable-tenant predicate (section 6.10) |
| `src/DocProc.Infrastructure/Classification/{KeywordRule,KeywordMatcher,KeywordDocumentClassifier,HeuristicDocumentSplitter}.cs` | Rule set as a parameter, string keys |
| `src/DocProc.Infrastructure/{Mail,Folders,Http}` (new or moved) | `MailKitImapMailSource`, folder sources, `TenantEndpointGuard` (moved from `DocProc.Llm/Providers`) |
| `src/DocProc.Pipeline/Intake/UploadIntake.cs` | `SourceMetadata` |
| `src/DocProc.Pipeline/Stages/IngestHandler.cs` | `SupplierSourceResolver` per item |
| `.../NormalizeHandler.cs`, `Support/ItemKinds.cs` | `INormalizer` registry; configurable kinds |
| `.../SplitHandler.cs` | Split policy (pinned type, then the split prefilter over the whole item, then the tenant default, 5.8); review split and join entry points |
| `.../ClassifyHandler.cs` | Keep the type on `TypeSource.Supplier`/`Human`/`Hint`/`Structured`; `classify.identification` (layout rules for a layout-managed supplier, tenant fallback rules for an unknown supplier or one without layouts and not `layouts.required`) before the keyword call (`:71`), keywords and LLM not invoked once it decides; `TypeSource.Proposed` under `NEW_LAYOUT`; its own `NEW_LAYOUT` and `LAYOUT_AMBIGUOUS` instances owned (`:38`); `IDocumentClassifier`; candidate set |
| `.../MatchHandler.cs`, `SupplierMatcher.cs` | Provider chain, corroboration, indexed candidates, ambiguity detection, `match.layout` for suppliers found by content or replaced (removes the unconditional null at `:44`); owns its own `NEW_LAYOUT` and `LAYOUT_AMBIGUOUS` instances (keyed by step id), and on a supplier switch clears the old supplier's layout, Identification/Proposed/Supplier type and Classify flags before re-classifying (7.4) |
| `.../ExtractHandler.cs` | Step chain, merger, RoleProjector, gate removal, generic supplier memory |
| `.../ValidateHandler.cs` | Rule steps, role-based duplicate check (nulls, `onMatch`), `review-gate` (`blockOnWarnings`, e-invoice exemption, warm-up from the activation policy); ReviewTask `Priority` and `SlaDueAt` from `review.priority`/`dueBy` |
| `.../PublishHandler.cs` | Fan-out, snapshots |
| `.../DocumentStageHandler.cs` | `StepContext` |
| `src/DocProc.Pipeline/Runner/PipelineRunnerHostedService.cs` | Dispatch `ProcessDelivery` |
| `src/DocProc.Pipeline/{Routing,Config,Delivery,Hosted}` (new) | Resolver, pipeline resolver, delivery handler, pollers, sweepers, batch scheduler, master-data sync, retention |
| `src/DocProc.Pipeline/Extraction/EInvoiceDocumentExtractor.cs` | `CanHandle` by definition compatibility |
| `src/DocProc.Extraction.EInvoice/{Ubl,Cii}/*`, `Internal/XmlFields.cs` | Extras capture; element path in the source |
| `src/DocProc.Extraction.Profiles` becomes `DocProc.Mapping` | Mapping engine (multi-page `column-table` writing `DocumentTableReport`), AnchorLearner, `Identification/{IdentificationRule, IdentificationRuleEvaluator, LayoutResolver, LayoutSuggester, IdentifierProposer}.cs`, `LayoutPrefillExtractor` |
| `src/DocProc.Llm/Schema/{DefinitionSchemaCompiler (new), CanonicalSchemas}.cs` | Runtime schemas, `x_custom` |
| `src/DocProc.Llm/Extraction/{LlmInvoiceExtractor, LlmDefinitionExtractor (new), DefinitionMapper (new), LlmAnswers, ProvenanceMatch, LlmDocumentClassifier, LlmDocumentSplitter}.cs` | Section 9 |
| `src/DocProc.Llm/Prompts/{PromptCatalogue, ExtractionTaskBuilder}.cs`, `extract-document.system.md` (new) | Generic prompt, supplier hints, fill-missing list |
| `src/DocProc.Api/Documents/CanonicalPatcher.cs` becomes `DefinitionPatcher.cs` | Section 12.8 |
| `src/DocProc.Api/Documents/{DocumentModels,DocumentQueries}.cs` | DTOs |
| `src/DocProc.Api/Endpoints/DocumentEndpoints.cs` | New actions; outbox inside the decision transaction; projector; reprocess stages |
| `.../IngestionEndpoints.cs`, `ChannelEndpoints.cs`, `SupplierEndpoints.cs`, `ApiKeyEndpoints.cs`, `Auth/Roles.cs`, `Application/Security/ApiKeys.cs` | Hints, actor split, typed config, scopes, permissions |
| `src/DocProc.Api/Endpoints/{DocumentType,SupplierLayout,IdentificationRule,Routing,Output,Delivery,Governance,Insights,MasterData}Endpoints.cs` (new) | Section 18 |
| `src/DocProc.Worker/WorkerHost.cs` | Register the hosted services |
| `docker-compose.yml`, `src/DocProc.AppHost/AppHost.cs` (kept in parity by `ComposeParityTests`) | `mock-erp` (WireMock) in P0; GreenMail users `nordwind@tenant-a.local`; `./fixtures/dropzone` mount for the folder channel; `sftp` and `gotenberg` in P6 |
| `web/apps/customer-portal/src/{lib/canonical.ts, components/review/*, routes/*}` | Move to `packages/review`; new routes |
| `web/apps/admin-console/src/{lib/documents.ts, lib/review.ts, routes/*}` | API-driven lists; shared review |

## 21. Migration of existing types and data

Every step is idempotent, additive and behind feature flags until P6.

Each schema change comes with the phase of section 23 that first uses it (corrected after the P0 review: this section listed every new column under P0, against section 23).

1. **Migrations.** P0 (`P0PersistenceSeams`, `OutboxFannedOut`, `TenantPolicyHistoryAndMoves`) adds only these:
   - `Ingestions.SourceSender` and `SubmittedBy` (step 4), `Corrections.Op` (default `set`) and `Corrections.SourceJson`, `OutboxMessages.FannedOutAt` (step 7).
   - `TenantSettings.Features`, every flag off, written into every tenant's stored settings.
   - The nullable-tenant RLS function and the policy re-creation (section 6.10), then the history-table filters and the strict tables' update block.
   - Every data statement runs in platform scope and through `EXEC`, so both migration paths reach the rows behind the policy and the idempotent script compiles (`RowLevelSecurity.InPlatformScope`).
   - The rest comes with the phase that first uses it:
     - P1: `Documents.DocumentTypeVersionId`, `AppliedConfigJson`, `ValidFrom`, `ValidUntil`, `LineCount`; `Corrections.DocumentTypeVersionId`; `ReviewTasks.Priority` (default Normal); the type catalogue tables; `TenantSettings.TimeZone` with the price-list templates (existing tenants get the time zone their batch schedules already use, otherwise UTC, and are asked to confirm it). Correction (P1): there is no `TenantSettings.TimeZone` to add, `Tenants.TimeZone` exists (§6.2); `DocumentFieldIndex` is the other P1 table (migration `FieldIndexAndInboxColumns`, with `PolicyV5`).
     - P2: `DocumentTableReports`. P3: the output and delivery tables.
     - P4: `Documents.SupplierMatchMethod`, `SupplierSourceRuleId`, `SupplierLayoutVersionId`, `ReceivingEntityId`, `IsSandbox`; the `IngestionItems` and `IngestionChannels` routing columns (step 6); `Suppliers.AttributesJson`, `SettingsJson`, `GroupId` (step 5); `TenantSettings.Layouts.RequiredByDefault` (false) and `LayoutActivation` (the recommended option of decision 2).
     - P5: `Documents.IdentificationRuleVersionId` and `SupersededByDocumentId`.
   - `Ingestions.ReceivedAt` already exists (`InitialCreate`); nothing to add or back-fill.
2. **Seed.**
   - P0: the platform templates of the legacy keys are embedded resources (`src/DocProc.Definitions`), read through `IEffectiveConfigResolver` (`StandardTemplateConfigResolver`), which answers every tenant with the adopted std template of the document's type. Nothing is written per tenant.
   - P1, with the catalogue tables: code lists come from embedded resources, the keyword blocks are generated from `KeywordRuleSet.Rules`, and each tenant gets Adopted types with the legacy keys `Invoice`, `CreditNote`, `PurchaseOrder`, `DeliveryNote`, `Statement`, `Reminder` and `Other`, at version 1, Published.
3. **Backfill documents** (P1, with the catalogue): `DocumentTypeVersionId` from `Type` in SQL (`UPDATE … JOIN` on the adopted type's v1); Unknown stays NULL. `SupplierMatchMethod` is set to `Legacy` when the column arrives (P4).
   - `CanonicalJson` is not rewritten. Version 1 rows remain valid because the paths are identical.
   - Correction (P1 demo): this left out the documents of types that had no extraction path, such as the demo delivery note: they have no `CanonicalJson` at all, and they are not back-filled either when a tenant switches `configurableTypes` on. Nothing in the design covered them, so the review screen offered no form until a reprocess. The API now answers such a document, while it is correctable, with the empty tree of its type version (`canonicalSchemaVersion` 2); the first correction stores it (§12.8), and a reprocess with `configMode: current` (§15.1) moves it onto a newer version.
   - `Correction.FieldPath` needs no change, and `SourceJson` stays null for old rows.
4. **Ingestion actor split.** `SourceFrom` is copied to `SubmittedBy` for ApiUpload and PortalUpload channels, where it held the actor. `SourceSender` stays null for historic rows.
5. **Suppliers.**
   - Each `EmailDomains` entry becomes a `SupplierSourceRule(SenderDomain, DefaultWhenNoContentMatch, RequireSenderAuth=false, Priority 50)`, which reproduces today's 0.85 domain signal.
   - `AlwaysReview` and `TenantSettings.AlwaysReviewSupplierIds` become `settings.review.mode = always`.
   - `DefaultCurrency` becomes `settings.defaults.currency`. It is supplier-wide, not a layout: a layout without an identification rule is not allowed, and a defaults-only layout would make every supplier layout-managed and send all its documents to `NEW_LAYOUT`.
   - No supplier becomes layout-managed by the migration. Suppliers get layouts only from review or the editor, so today's behaviour holds until a tenant creates the first layout of a supplier.
   - The old columns are read as fallbacks until P6.
6. **Channels.** `Type` becomes `ConnectorKey` with the same strings. `SourceKey` is computed from `ConfigJson.address`/`path`. The cross-tenant unique index is created, and the migration fails loudly on conflicts.
7. **Outbox.** Existing rows get `FannedOutAt = CreatedAt` and no deliveries, because nothing ever consumed them.
8. **P6 cleanup.** Drop `ExtractorProfiles` (after a zero-row check), `Documents.ExtractorProfileId`, `Supplier.EmailDomains`, `AlwaysReview`, `DefaultCurrency`, `TenantSettings.AlwaysReviewSupplierIds`, `Ingestion.SourceFrom`, and the `PurchaseOrder` and `DeliveryNote` records (their templates cover them).

## 22. Tests

- **Parity and compatibility**
  - std template leaf paths equal `Invoice.EnumerateAllFields()`;
  - `SchemaSnapshotTests` keeps `invoice_extraction.v1.json` byte-identical, and new snapshots cover each compiled template schema and `document-type-definition.v1.json`;
  - every replay fixture still matches by request hash;
  - the existing Domain validation tests (`InvoiceValidatorTests`) stay as they are, and `DocumentRuleTests` proves that `InvoiceRuleAdapter` with the std invoice rule list raises the same flags as `InvoiceValidator.Default`;
  - the review and corrections tests with invoice fixtures, and the e2e screenshot specs, pass with the generated form (the gate for removing the hand-written form).
- **Round trip**: a property unknown to the records survives deserialise, serialise and reprocess.
- **Patcher**: set, append, remove and move on generic types; source recorded; inline validation flags.
- **Routing**: precedence and specificity (including folder prefix length); DMARC gating; free-mail deny; `SUPPLIER_CONFLICT`, `SUPPLIER_AMBIGUOUS` and `ROUTE_DIFFERS`; per-attachment `fileNameRegex`; forward unwrapping. `SupplierMatcherTests` stay unchanged.
- **Stage behaviour**: Classify skip with `TypeSource.Supplier`; the existing status-order tests stay unchanged, because the stage order is unchanged.
  - `ClassifyHandlerTests` (mocked `ILayoutResolver`, `TypeIdentificationRuleSet`, keyword matcher and LLM classifier): call order identification, then keyword, then LLM; when a supplier layout rule holds, and when a tenant rule holds, the keyword matcher and LLM classifier mocks are never called; under `NEW_LAYOUT` they are called only to propose and the type is `Proposed`; a known supplier without layouts goes to the tenant rules, one with `layouts.required` and zero layouts gets `NEW_LAYOUT`; `Structured` keeps its type.
  - `SplitHandlerTests`: a 30-page price list with a group heading on page 7 stays one document when the type is pinned by a source rule, and when it is not pinned but the supplier's price-list layout (or a tenant price-list rule) holds in the split prefilter; without either, the heuristic splitter runs; `:join` puts the parts back together.
  - `PipelineResolverTests`: an override that disables or replaces `classify.identification`, or inserts a step before it, is rejected at save, at type, group and supplier level, and an imported pipeline that violates it is refused.
- **Identification rules** (`IdentificationRuleEvaluatorTests`, table-driven over text-layer fixtures):
  - `text`: whole-word and phrase matching ("Rechnung" holds for "RECHNUNG", not for "Rechnungskorrektur"), `caseSensitive`, whitespace collapse, reading order across a line break (does not join);
  - `page` (`1`, `last`, `any`), named regions and `bBox` with tolerance, by box centre;
  - `regex` with the NonBacktracking timeout; `supplierValue` with spaced and compact VAT ids and IBANs; `source`, `xmlPath` and `cell`;
  - `all`, `any`, `none`, nesting and inherited location; `all` plus `any` plus `none` at one level as an implicit AND; an identifier's own `page` overriding the group's while the group's `region` is inherited; rejection of `none`-only rules and of rules over the size limits;
  - `allowOcrErrors` only on OCR layers, only within its bound, rejected at save on the only positive identifier, and applied in the overlap test;
  - `supplierValue` rejected at save in a tenant rule version;
  - determinism: the same input always gives the same result and evidence.
- **Layout resolution** (`LayoutResolverTests`): one layout holds; two layouts of different types (invoice and price list of one supplier); tie-break by `Priority`, then the number of positive identifiers; `LAYOUT_AMBIGUOUS`; no layout holds for a layout-managed supplier gives `NEW_LAYOUT {supplierId, proposedType, suggestions}` and never a layout, even when the similarity score of one layout is 0.99; a supplier with no layouts keeps today's path; `layouts.required=true` (on the supplier, and inherited from the tenant default) with zero layouts gives `NEW_LAYOUT`; group layouts ignored when the supplier has its own; another supplier's rule never evaluated; supplier replaced in Match drops the earlier layout; supplier A's layout rule sets the type, Match switches to B (no layouts), and the type is re-decided by the tenant rules, keywords and the LLM for B, not inherited from A; re-type in Match before Extract, but never over a Human, Hint, source-pinned or Structured type; e-invoices never raise `NEW_LAYOUT`; tenant fallback rules only for unknown or layout-less suppliers, and two conflicting tenant rules decide nothing; a Candidate version whose rule holds is never applied, the next document gets `NEW_LAYOUT` again with `pendingLayoutVersionId`; `NEW_LAYOUT` raised by `match.layout` for a content-found supplier, owned by that step, and cleared by a reprocess after the layout was activated, while a Classify instance for the replaced supplier is cleared by Match.
- **Review gate**: `NEW_LAYOUT` and `LAYOUT_AMBIGUOUS` block auto-approval even with a tenant severity override; `TypeSource=Proposed` never auto-approves; `requirePassedRules` with a skipped rule blocks; `lines.requireSource` and `requireLocated`; `blockOnWarnings`: a price list with a 40 % jump on one article goes to review, one with a uniform 3 % increase auto-approves; an e-invoice of a supplier that has PDF layouts auto-approves without a layout; a layout version inside its warm-up blocks, with the warm-up taken from the default tenant activation policy.
- **New-layout flow** (API tests plus a Playwright spec): a `NEW_LAYOUT` document with a proposable type arrives pre-filled (`TypeSource=Proposed`, `layout-prefill` ran in Extract), and one without stays Unknown until `:match-layout`; `:match-layout` with the proposed type does not re-run Extract, with another type it does; pre-fill from master data, from another layout's labels, from a known constant custom-field value and (with the replay-fixture LLM) from the LLM, each with its provenance; approve with `learnLayout` creates version 1 of a new layout while the old layout stays Active; approve without it resolves the flag as `approvedWithoutLayout`; the activation policy (Candidate versus Active) per role; activation of a `NewLayoutReview` version 1 with a replay set of one document; the Candidate path (a second document in the same design while the layout is a Candidate gets `pendingLayoutVersionId`, "confirm against pending layout" creates no second layout, siblings are attached and reprocessed only on activation, `MappingAuthor`s notified); sibling re-test through `matching-documents` and `bulk-reprocess`; "new version of X" with a changed rule reports X's documents that would no longer match and needs `confirmNotRecognised`; the next fixture document in the new design is identified and auto-processed **with the default tenant settings** (no warm-up set by hand).
- **Supplier assignment in review**: `:assign-supplier` to a layout-managed supplier whose layout holds applies it with evidence; to one whose layouts all fail raises `NEW_LAYOUT`; to one without layouts keeps the Unknown panel and re-evaluates the tenant rules.
- **Identification test**: an overlapping rule blocks activation and names the documents; the replay of a new layout covers only documents its own rule holds on.
- **Identifier proposals** (`IdentifierProposerTests` and API tests of `identifiers:propose`): words in the title band give a `region: "title"` identifier; elsewhere a `bBox` with the default tolerance; text equal to the supplier's VAT id (spaced or compact) gives `supplierValue: "vatId"` in layout scope and a `text` identifier in tenant scope.
- **Identification rule test API** (`identification-rules:test`): supplier, group and tenant scopes; per-document results with evidence; overlaps with other layouts reported with the documents' differing title words.
- **Tenant fallback rules** (API tests): saving needs `ConfigAdmin`; activation follows `configApprovals.identificationRules`; the save-time test over the last 500 approved documents reports matches per type and skips `ExcludedSupplierIds` and suppliers with layouts; `Overridden` counts a reviewer's type change; a document pins `IdentificationRuleVersionId`, and editing the rule does not change it.
- **Identify mode** (Playwright spec): clicking a word and boxing a phrase create chips with the proposed location; the all/any toggle; moving a chip to "must not contain"; the live test result and the red overlap state; "use for all suppliers without own layouts" hidden without `ConfigAdmin`.
- **Mapping engine**: each strategy and transform on fixtures; drift detection; AnchorLearner golden cases (label-right, label-below, region fallback, column-table); `column-table` over a 30-page price-list fixture with repeated headers, a page without a header, continuation lines, group headings and tier columns, asserting the row count and the table report.
- **Price lists**: the `std.price-list` and `std.daily-specials` templates compile and pass the meta-schema, v1 with only P1 rule kinds and `autoApprove: false`, v2 with the P5 kinds; a type referencing an unregistered kind fails publish; an automatic v1 to v2 upgrade keeps `autoApprove` off; `tableComplete` (including `TABLE_PAGE_NO_ROWS` blocking with `everyPageInRange` and a warning without it, and `allowUnreported`), `unique`, `ordered` and `deltaToPrevious` on fixtures (including no previous list); the review table tool over a 30-page fixture writes one Correction and the expected `DocumentTableReport`; auto-approve passes for a clean list from a layout and fails for one with a rejected row, with LLM-read rows, or past its validity (specials); a specials list whose rows the LLM read and located auto-approves, one with an unlocated row does not; a specials list with no printed date gets `validFrom` and `validUntil` equal to the received date in the tenant time zone; a list above `maxItems` fails `validate.schema` and goes to review; the inbox columns `validFrom`, `validUntil`, `lineCount`, and `Priority`/`SlaDueAt` from `dueBy` (end of day in the tenant time zone).
- **Duplicates for price lists**: same supplier, `validFrom`, `listScope` and number gives `REVISION_OF` and review; a numberless list matches only another numberless list; a second specials list on the same day goes to review and each of `supersedes`, `additional` and `duplicate` has its effect (superseded lists skipped by `deltaToPrevious` and `LastApproved`); publish rejects a `duplicateKey` naming an unindexed field.
- **Outputs**: JSONata golden tests (singleton arrays, `$table` with ValidFrom, `$required`, `$today()` in the tenant time zone; `article-price-import` over a 412-row list, rows without `status`, tiers and empty tiers; `pos-promotions` over a specials fixture, rows without `previousPrice`, and `replaces` after a supersede); the `pos-specials` binding `Condition` produces no delivery for a list whose `validUntil` is yesterday; a 10,000-row list at `maxItems` with maximum-length rows renders under 5 MB and is delivered; sandbox limits (time, size, depth); target-schema failures; retry classification including Retry-After; ordering and Skipped; OnReapproval; recipe resume; batch rendering; callback mapping.
- **Security**: RLS null-tenant read and write blocks (Testcontainers.MsSql); SSRF guard on destinations and http-lookup; secrets never echoed.
- **End to end in compose**: e-mail into GreenMail, routing, mapping, validation, JSONata, then the WireMock ERP receiving the payload, a forced 503, retries and success. This scenario is added to `PipelineEndToEndTests` and to an AppHost test. Two more scenarios, both with the default tenant settings: a Nordwind invoice in an unseen design arrives, gets `NEW_LAYOUT`, is matched and saved in review by a `MappingAuthor`, and the second document in that design is delivered without review (the warm-up of 0 comes from the recommended activation policy, decision 2); and a multi-page price list dropped in `./fixtures/dropzone/lieferanten/nordwind/preislisten/` stays one document and is delivered to the mock ERP's article-price endpoint.

## 23. Phased delivery plan

The plan has two tracks after P1:

- Track A follows the reviewer and supplier workflow.
- Track B builds outputs.

Sizes are rough team-weeks for a team of four (three backend, one frontend) and need validating.

The **minimal viable release** is P0 to P4, about 20 elapsed weeks with both tracks in parallel (P4 grows by one week for identification rules and the new-layout flow). It gives the product owner:

- custom fields;
- manual mapping of Unknown documents;
- supplier routing by folder, mailbox and From;
- layouts learned from review, recognised by strict identification rules, several per supplier and type, with unrecognised designs sent to review as `NEW_LAYOUT` and saved as new layouts;
- price lists and daily specials captured (multi-page table tool in review, layouts from P4) and delivered, still always reviewed (template v1, `autoApprove: false`, section 5.8);
- delivery to one ERP in its own format.

Price lists auto-approve from P5, when the rule kinds that replace a totals check exist and the templates move to v2 (a `ConfigAdmin` then switches auto-approval on).

Every phase is demonstrated with `docker compose up`, using the GreenMail mailbox, the WireMock `mock-erp` service (added in P0) and the `./fixtures/dropzone` folder channel.

- **P0, safety seams (3 weeks).**
  - Feature flags.
  - `[JsonExtensionData]`.
  - `IFieldSchemaResolver` seam (reflection implementation first).
  - `RoleProjector`.
  - `FieldValue.Correct(source)`; `FieldCorrection.source/op`; `Correction.SourceJson`.
  - `SubmittedBy`/`SourceSender` split.
  - Outbox written inside the approve and reject transactions, plus `PublishSweeper`.
  - `TenantEndpointGuard` moved to Infrastructure.
  - Nullable-tenant RLS.
  - Seeded std templates with the parity test, and `IEffectiveConfigResolver` returning the std templates.
  - `IDocumentRule` with `InvoiceRuleAdapter`.
  - `ExtractionOutcome` compatibility constructor.
  - `mock-erp` in compose and AppHost.
  - Demo: the existing invoice flow runs unchanged on the seeded std templates (parity), and an approval interrupted by a forced API restart between commit and enqueue is still published by the `PublishSweeper` (visible in the document timeline).
- **P1, types as data and custom fields (5 weeks).**
  - Type catalogue tables and API.
  - `DocumentTypeKey` string switch, in one consolidated change.
  - Compiler.
  - `DefinitionPatcher` (descriptor implementation, append and remove).
  - Generated review form in `packages/review`.
  - Document types editor (fields, labels, required, indexed, publish with dependents check).
  - `validate.schema`.
  - `DocumentFieldIndex` with inbox filters.
  - `x_custom` in the invoice LLM call.
  - PurchaseOrder and DeliveryNote get a form.
  - Generic rule kinds of P1 (`required`, `pattern`, `range`, `enumMember`, `dateOrder`, `datePlausibility`, `lookupExists`, `duplicate` with `onMatch`, `confidence`, `customerMatch`, section 11).
  - `std.price-list` and `std.daily-specials` templates **v1** (roles `validFrom`, `validUntil`, `lineSku`; array `maxItems` 10,000; only P1 rule kinds; `review.autoApprove: false`), adoptable from the catalogue. They are always reviewed in this phase because the gate's `autoApprove` condition fails (5.8).
  - Denormalised `ValidFrom`, `ValidUntil`, `LineCount` and the inbox columns for them; `ReviewTask.Priority`.
  - Demo: Acme adopts `std.price-list` and forks its credit note with the custom field `supplierCustomerRef` (5.3); a credit note with that field is extracted, shown in the generated form and filterable in the inbox.
- **P2, track A: manual mapping of Unknown documents (4 weeks).**
  - `:set-type` with empty or auto prefill.
  - Map mode, the `/words` endpoint, region OCR and the table tool, including "apply to all pages" (header repetition, page breaks, continuation lines, group headings, tier columns), the table report and page strip, and the single-Correction write (12.4).
  - Move correction (match fields).
  - `:assign-supplier`, and create supplier from review.
  - `:split` and `:join` in review.
  - Approve refused without a definition.
  - Demo: a 14-page Nordwind price list (412 rows) uploaded through the portal arrives Unknown (the tenant keyword set comes in P5), is set to `price-list` in review, and all 14 pages are captured with one table-tool mapping on page 1 ("apply to all pages"); the page strip shows 412 rows and no rejected rows; the list is approved by hand.
  - Correction (P2): with the LLM classifier of the demo stacks (and of any tenant with a provider), the list arrives as `Other`, not `Unknown`: the keyword rules find no type, and the LLM classifier, asked then, has no Unknown in its answer (§12.1). It also costs an LLM split call, because 14 pages are more than the heuristic splitter decides alone (`MaxConfidentPageCount`, 6). The demo runs as described from there (docs/09 "Demo: manual mapping").
- **P3, track B: output mappings and delivery (5 weeks, in parallel with P2).**
  - Output tables; values view with roles, supplier, entity and source.
  - Jsonata.Net.Native spike, then the renderer.
  - Target-schema check.
  - Fan-out with snapshots and the `ProcessDelivery` handler.
  - http transport (auth variants, `responseMapping`, multipart), blob drop and pull feed.
  - Retries, dead letter, ordering, OnReapproval, redeliver.
  - Integrations UI and the Deliveries tab.
  - `std.event` and `generic-ap-json` templates.
  - Bindings scoped by type send price lists to their own destination; the `article-price-import` mapping of 13.10 is built as a tenant mapping in the demo (no engine work beyond the above).
  - Demo: the price list approved in the P2 demo is rendered by `article-price-import` (preview in the Integrations UI, 412 rows, tier prices as scales) and delivered to `mock-erp` `POST /api/article-prices`, while invoices keep going to `erp-ap`; a forced 503 on the article-price stub is retried and then succeeds.
- **P4, routing, supplier configuration, layouts and identification (7 weeks).**
  - IMAP poller (with forward unwrapping and auth results) and folder watcher.
  - `SupplierSourceRule` with `routing:test`.
  - Resolver at ingest; `TypeSource.Supplier` Classify skip; Match corroboration.
  - Supplier settings (including `layouts.required` with its tenant default), attributes, groups and receiving entities.
  - `DocProc.Mapping` engine with layouts (`SupplierLayout`, `SupplierLayoutVersion`), multi-page `column-table` with the table report, and drift detection.
  - Identification rules: `IdentificationRuleEvaluator`, `schemas/identification-rule.v1.json`, `ILayoutResolver` in `classify.identification` (fixed first step of Classify) and `match.layout`, the split prefilter, `TypeSource.Identification` and `Proposed`, `NEW_LAYOUT` and `LAYOUT_AMBIGUOUS` in the review gate (owned per step), `LayoutSuggester` (suggestions only).
  - AnchorLearner, `:learn-layout` (identification rule plus field mapping; proposes source rules too), replay over the replay set with the identification test, lifecycle with the warm-up from `TenantSettings.LayoutActivation` (decision 2), `MappingAuthor`, the Candidate path (`pendingLayoutVersionId`, notifications).
  - Identify mode and `identifiers:propose`; `identification-rules:test`; the New layout panel, `:match-layout` and the `layout-prefill` step (master data, other layouts' labels, known values, LLM when configured); sibling re-test.
  - Demo: Nordwind invoices arrive through the GreenMail mailbox and are identified by "Rechnung" in the title and Nordwind's VAT id. A Nordwind invoice from its new web shop then arrives in a different design, is held with `NEW_LAYOUT` (not processed with the old layout), matched to Invoice in review with its supplier data, invoice number and customer number already filled, and saved as `nordwind-invoice-webshop`. With the default tenant settings (the recommended activation policy of decision 2 gives warm-up 0 to a layout a `MappingAuthor` saved from this flow, section 8.3), the next web-shop invoice is identified, filled and delivered to `mock-erp` without review, and invoices in the old design still are too. In a second demo tenant, where the reviewer is not a `MappingAuthor`, the saved layout stays a Candidate and the next web-shop invoice arrives with "confirm against pending layout".
  - e-invoice extras with xml-path.
  - Golden documents, sandbox runs and config bundle export/import.
  - Supplier sheet tabs.
- **P5, configurable steps, custom types end to end, rules and master data (6 weeks).**
  - Step kinds per phase with supplier `pipelineOverrides`.
  - `http-lookup`.
  - Rule kinds (sumEquals, compare, requiredIf, arithmetic, expression) and definition flags.
  - `LlmDefinitionExtractor` and `DefinitionMapper`.
  - Tenant keyword set and LLM classifier enum.
  - Tenant fallback identification rules (`TypeIdentificationRule`, the Identification tab of the type editor, "use for all suppliers without own layouts" in review).
  - Price lists end to end: rule kinds `tableComplete`, `unique`, `ordered`, `deltaToPrevious`, plus `compare` and `expression` from the list above; templates v2; review gate `requirePassedRules`, `blockOnWarnings` and `lines`; the `dueBy` review priority for daily specials; `REVISION_OF` and `:resolve-revision`; `generic-article-prices-json`; the `pos-promotions` example mapping; the optional `llm` `lines: perPage` mode.
  - Demo, price lists: the folder rule `lieferanten/nordwind/preislisten/` (pin `price-list`, 7.5) is added. The first 14-page Nordwind price list (412 rows, repeated headers, tier prices "ab 10" and "ab 50") dropped there is routed to Nordwind and stays one document. Nordwind is layout-managed since P4 but has no price-list layout, so it arrives with `NEW_LAYOUT`, pre-filled against `price-list` from Nordwind's master data. The reviewer maps the table once with the table tool, marks "Preisliste" in the title and the "gültig ab" line as identifiers, and approves with "save as layout", which a `MappingAuthor` activates as `nordwind-price-list`. Next month's list, dropped in the same folder, is identified by that layout, read entirely by its `column-table`, checked against the printed row count and the previous list (12 rows changed, all within 25 %), auto-approved with no warm-up, and delivered to `mock-erp` `POST /api/article-prices` as the payload of 13.10. A copy of it with one article up 40 % goes to review with that row filtered.
  - Demo, specials: a two-page "Tagesangebote" list from the same supplier sorts to the top of the queue as urgent, is approved, and goes to the `pos-promotions` WireMock stub as the payload of 13.10; a copy dated yesterday is blocked as expired, and a second list on the same day arrives with `REVISION_OF`.
  - Demo, tenant fallback rule: a document from Südhof Feinkost, a supplier without layouts, is opened in review; the reviewer marks "Preisliste" in the title and, as `ConfigAdmin`, chooses "use for all suppliers without own layouts". The save-time test shows "would have matched 37 price lists, 0 other documents". After activation the next Südhof list, and one from an unknown sender, are typed `price-list` by the rule (`TypeSource=Identification`, `IdentificationRuleVersionId` set), with keywords and the LLM not called, while Nordwind's documents are still decided by Nordwind's own layouts.
  - Processing UI.
  - Master-data import and scheduled sync.
  - Callbacks and status polling with `EXTERNAL_REJECTED`.
  - Insights > Onboarding.
  - LLM budgets.
- **P6, any file, advanced outputs, governance and cleanup (6 or more weeks).**
  - CSV/XLSX, XML/JSON and e-mail-body normalizers; Gotenberg for DOCX, HTML and TXT.
  - Structured viewer and the structured mapping strategies.
  - Liquid renderer (CSV, XML with XSD check), SFTP and e-mail transports.
  - Batches and recipes (including chunked delivery of large price lists, 13.10).
  - XLSX and CSV price lists on the tabular layer, with `cell` identifiers and header-name column maps (8.6). If the first pilot tenant's price lists are spreadsheets, this item moves forward; it is part of decision 5 in section 24.
  - Four-eyes approvals and the audit UI.
  - Template upgrade three-way merge.
  - Admin console shared review and platform template editors.
  - Retention cleanup and sensitive-field masking.
  - Drop the legacy columns and records.


## 24. Decisions for the product owner

Five decisions change the build. Each is listed with its options and a recommendation.

**Decided by the product owner on 2026-11-02** (these override the recommendations below where they differ):

| # | Decision | Answer |
|---|---|---|
| 1 | Source versus content | Per rule, as recommended: an `Exclusive` rule keeps the source supplier and raises a blocking `SUPPLIER_CONFLICT`; otherwise VAT and IBAN win. Sender rules require sender authentication (DMARC) by default, with an override per rule. |
| 2 | Activating learned layouts | Propose, then activate: a reviewer's save is a Candidate; a `MappingAuthor` activates it after the replay against the supplier's earlier documents. **The first 5 documents under every new layout version go to review**, including version 1 of a layout saved from a `NEW_LAYOUT` review (warm-up 5, not 0). |
| 3 | Output transform language | JSONata with the visual field mapper compiling to it, rendered on the server; Liquid later for CSV, XML and fixed-width text formats. |
| 4 | First receiving systems | One JSON POST per document over the generic http transport, plus file drop and a pull feed. Multi-call recipes, batch files, SFTP, XML/CSV output and acknowledgement callbacks stay in the later phases. |
| 5 | Non-PDF inputs | PDF and images in the first release; spreadsheets, custom XML/JSON and office formats in the later phases as planned. |

Open consequence of decision 2 (found while delivering P0, not yet rewritten): sections 8.3, 12.10 (steps 6 and 8), 22 ("New-layout flow", "End to end in compose") and the P4 and P5 demos in 23 still describe the recommended Option A, where version 1 of a layout saved from a `NEW_LAYOUT` review has warm-up 0, and say that the next document in the new design is delivered without review "with the default tenant settings". Under the decided policy that default is a warm-up of 5, so the first five documents under the new version go to review. Those passages, and whether the demos run five documents or set the demo tenant's warm-up explicitly, are to be settled when P4 is planned in detail.

1. **Source versus content precedence, and sender authentication.** Options: the source always wins; content (VAT, IBAN) always wins; per rule. Recommendation: per rule, as designed in 7.2 and 7.4 (`Exclusive` keeps the source supplier and raises a blocking `SUPPLIER_CONFLICT` on contrary content evidence; `DefaultWhenNoContentMatch` yields to VAT and IBAN), with `RequireSenderAuth` on by default for sender rules.
2. **Who activates learned layouts, and with which warm-up** (`TenantSettings.LayoutActivation`). This is the "activation policy" referred to in 8.3, 11, 12.6 and 12.10. A layout is always activated under the one condition of 8.3 (identification test passes, field agreement on its replay set); the decision is who may do it and how many documents then still go to review:
   - **Option A, `MappingAuthor` only.** Reviewers' saves become Candidates; a `MappingAuthor` activates them (straight away when they save from review themselves). Warm-up: 0 for version 1 of a layout saved from a `NEW_LAYOUT` review (`Origin=NewLayoutReview`), because the saving reviewer has just checked every value of the source document; 5 for all other versions.
   - **Option B, reviewer self-activation.** Any reviewer with `MappingEditor` may activate a layout they saved. Warm-up: 3 for `NewLayoutReview` versions, 5 for all others, so a second person sees the first documents.
   - **Option C, always Candidate.** Nothing is activated from review; every layout is activated in the Suppliers sheet. Warm-up: 5 for every version.

   Recommendation: **Option A**, with the warm-ups above. It delivers the promised behaviour by default: after a `MappingAuthor` saves a new design in review, the next document in that design is filled automatically and goes to review only when a validation fails. Tenants that want more caution raise the warm-up per policy or per version; tenants without a `MappingAuthor` on the review team get the Candidate path of 12.10. Four-eyes for layouts is separate (`ConfigApprovals.layouts`, 15.2).
3. **The output transform language.** Options: JSONata; Liquid; XSLT; a visual mapper only. Recommendation: JSONata for JSON targets with the visual field map compiling to it (13.1), and Liquid in P6 for text formats; no XSLT.
4. **The first receiving systems and their protocols.** Options: a named accounting system's API; a generic http JSON import; file drop. Recommendation: the pilot tenant's ERP through the generic http transport with a tenant mapping (`generic-ap-json`, `article-price-import`) plus blob drop as fallback; vendor-specific templates only after a sandbox check against that vendor's current API (13.9).
5. **The scope of non-PDF formats for manual mapping.** Options: PDF and images only; plus XML and JSON; plus CSV and XLSX; plus office formats. Recommendation: all of them in P6 as planned (12.5), with CSV and XLSX moved forward if the pilot tenant's price lists arrive as spreadsheets (8.6).

## 25. Documents this supersedes

The edits needed in the existing documents, in short:

- docs/00 drops "never hand-authored".
- docs/02 §1 and §4 become "types as data".
- docs/03 gains source routing, configurable steps and the publish fan-out.
- docs/04 becomes "supplier layouts": identification rules plus field mappings.
- docs/05 replaces the fixed webhook and export plans.
- docs/06 covers definition-compiled schemas.
- docs/07 adopts this plan.
