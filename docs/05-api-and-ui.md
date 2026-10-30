# API and UI

## Principles

- **API first**: the React apps are just clients of the public API. Nothing is reachable from
  the UI that is not reachable from the API, and the OpenAPI document is generated from code
  and published with the build.
- **Tenant isolation at the API boundary**: every request carries a tenant context derived from
  the token (customer users) or an explicit `X-Tenant-Id` header that only superadmins may set.
  Row-level filtering is enforced in the data layer (EF Core global query filters keyed on the
  tenant id), not in individual endpoints.
- **Versioned** (`/api/v1`), JSON only, problem-details (RFC 9457) for errors, cursor pagination,
  idempotency keys on writes that create resources.
- **Events out**: webhooks with HMAC signatures and retries, driven by an outbox table.

## Roles

| Role | Scope | Can |
|---|---|---|
| SuperAdmin | platform | everything; manage tenants, channels, global supplier registry, extractor profiles, prompts, models, costs, queues |
| Operator | platform | work the global review queue, retry failed jobs, inspect any document (read), no tenant settings |
| TenantAdmin | one tenant | manage members, suppliers, channels of the tenant, review policy, API keys, webhooks |
| Reviewer | one tenant | review/correct/approve documents |
| Viewer | one tenant | read documents and exports |

Authentication: OpenID Connect against an external identity provider (Entra ID / Keycloak /
Auth0, decision deferred to the roadmap). Machine clients use per-tenant API keys scoped to
`ingest` and/or `read`.

## Resource model (v1)

```
/tenants                         (SuperAdmin)
/tenants/{id}/members
/tenants/{id}/channels           mailbox / folder / api channels
/tenants/{id}/suppliers
/tenants/{id}/settings           review policy, thresholds, retention
/tenants/{id}/llm-providers      BYOK configs: create (key goes to Key Vault), :test, :rotate, disable
/tenants/{id}/llm-policy         default config, per-stage overrides, platform-key fallback
/tenants/{id}/llm-usage          tokens and estimated cost per day / stage / supplier

/ingestions                      POST (upload), GET list, GET {id} with items
/documents                       GET list (filters: status, type, supplier, flag, date range, q)
/documents/{id}                  canonical data + flags + provenance
/documents/{id}/pages/{n}.png    page images for the viewer
/documents/{id}/original         raw file
/documents/{id}/text             positioned text layer (for the highlight overlay)
/documents/{id}/events           timeline
/documents/{id}:approve | :reject | :reprocess | :reclassify | :split
PATCH /documents/{id}/fields     corrections (JSON Patch over the canonical model)

/review-tasks                    queue, claim, release
/purchase-orders, /delivery-notes   typed views over documents, plus matching endpoints
/exports                         JSON / CSV / UBL; async for large ranges

/extractor-profiles              (SuperAdmin/Operator) list, get, activate, retire, test-against-document
/extractor-profiles/{id}/runs    success history
/llm/prompts, /llm/usage         prompt versions, token and cost reporting per tenant/model/key owner
/llm/providers                   (SuperAdmin) platform default configs per region, price tables, kill switch

/webhooks                        per tenant: subscriptions, deliveries, redeliver
/health, /metrics                liveness/readiness, Prometheus/OpenTelemetry
```

## React applications

One monorepo, two apps sharing a component and API-client package:

### Admin console (SuperAdmin, Operator)

- Dashboard: documents per day by path (XML / profile / LLM), STP rate, review backlog,
  failed jobs, LLM spend by tenant and model, channel health (last poll, last error).
- Tenants and channels: create tenant, provision mailbox, test connection, see recent ingestions.
- Global supplier registry and extractor profiles: fingerprint, rules, success rate chart,
  sample documents, "synthesize profile" and "test profile against document" actions,
  activate / retire.
- Global review queue (cross-tenant) for operators.
- Prompt and model management: prompt versions, which provider and model per stage, eval
  results against the gold set per provider/model.
- Platform AI providers: default configs per region, price tables, per-tenant BYOK overview,
  per-provider kill switch.
- Job inspector: pipeline timeline per document, retry/re-run, dead-letter list.

### Customer portal (TenantAdmin, Reviewer, Viewer)

- Inbox: documents by status (Needs review, Approved, Rejected, Processing), filters, search.
- Review screen: page viewer on the left with field highlights; canonical form on the right;
  flags listed at the top with one-click jump to the field; line-item grid with arithmetic
  check indicators; approve / reject / reassign; keyboard-first.
- Suppliers: list, aliases, default settings ("always review", expected currency), activity.
- Purchase orders and delivery notes: three-way match view (PO ↔ delivery note ↔ invoice).
- Settings: members and roles, mailbox address and forwarding instructions, folder/API
  credentials, webhooks, export formats, review policy thresholds, **AI providers (BYOK)**:
  add provider, test, pick models per stage, spend cap, usage chart.

### Shared UI decisions

- **Do not build UI primitives.** Decision (2026-10-30): both apps use **shadcn/ui** components
  (Radix UI primitives + Tailwind) generated into the shared `packages/ui` package, and reach for
  an existing library before writing anything generic: TanStack Query for server state, TanStack
  Table for grids, TanStack Router or React Router for routing, react-hook-form + zod for forms,
  Radix primitives (dialog, dropdown, tooltip, popover, toast) via shadcn, PDF.js for the page
  viewer, i18next for translations, `openapi-typescript` / `openapi-fetch` for the generated
  client. Custom components are limited to the genuinely domain-specific pieces: the field
  highlight overlay on the page image, the line-item grid with arithmetic indicators, and the
  three-way match view. Anything else that looks like a button, table, form field, dialog or
  menu comes from the library.
- Vite + React + TypeScript; PDF.js for the viewer with an SVG overlay for bounding boxes.
- Generated TypeScript client from the OpenAPI document (no hand-written fetch code).
- i18n from day one (restaurant customers and their suppliers are multilingual).

## Document endpoints as implemented (v1)

`src/DocProc.Api/Endpoints/{IngestionEndpoints,DocumentEndpoints,ReviewTaskEndpoints}.cs`. Tenant scope is
the resolved `ITenantContext` only; the GETs also work in platform scope (Operator, SuperAdmin without
`X-Tenant-Id`), where the data layer returns every tenant. Enums travel as strings. The API only
*enqueues* pipeline work (`IPipelineQueue`); without a registered runner a logging no-op queue is used.

| Endpoint | Policy | Notes |
|---|---|---|
| `POST /ingestions` | Ingest | `multipart/form-data` with one or more file parts (max 20 files, 50 MB each, optional `channelId` field; default: the tenant's ApiUpload channel, created as "API upload" on first use) or `application/json` `{fileName, contentType, base64, channelId?}`. Stores each file content-addressed in the raw container, creates the `Ingestion` (+ one `IngestionItem` per file, kind from the content type) and enqueues `ProcessIngestion`. `202` with `{id, status, items[{id, fileName, sizeBytes, sha256}]}` and `Location`. `Idempotency-Key` (same tenant, 24 h) replays the first response; the same key with a different upload is `422`. |
| `GET /ingestions`, `GET /ingestions/{id}` | Read | Newest first, cursor-paginated; the detail lists items with the documents split from them. |
| `GET /documents` | Read | Filters `status` (comma list), `type`, `supplierId`, `flag` (unresolved flag code), `from`/`to` (issue date), `q` (document number or supplier name contains); `cursor` + `limit` (default 50, max 200), newest first. `{items: DocumentSummary[], nextCursor}`. |
| `GET /documents/{id}` | Read | `DocumentDetail`: the summary columns + `canonical` (the stored JSON verbatim, so every `FieldValue` keeps value, status, confidence, source and history) + `flags` + `costs` + `ingestion` + `pageImageUrls`. |
| `GET /documents/{id}/pages/{n}.png`, `/original`, `/text` | Read | Stream the derived PNG (ETag = content hash, `304` on `If-None-Match`), the ingested file (`Content-Disposition: inline`) and the text-layer JSON; `404` problem when missing. |
| `GET /documents/{id}/events` | Read | Timeline ordered by time, then id. |
| `POST /documents/{id}:approve` | Review | From `NeedsReview` or `Validated`; sets `ApprovedAt/By`, closes open review tasks (`Done`), writes `document.approved`, enqueues `Publish`. Body `{comment?}` optional. `409` on any other state. |
| `POST /documents/{id}:reject` | Review | Body `{reason}` required; otherwise as approve, with `document.rejected`. |
| `POST /documents/{id}:reprocess` | Review | Body `{fromStage?}` ∈ Normalize, Classify, Extract (default), Validate. Allowed from Failed, NeedsReview, Rejected, Validated (`DocumentStateMachine.CanReprocess`); resets the status to the one before that stage (Extract → Matched), skips open review tasks, writes `document.reprocessed`, enqueues that stage. |
| `PATCH /documents/{id}/fields` | Review | `{corrections: [{path, value, reason?}]}` with canonical paths (`invoiceNumber`, `lines[2].quantity`, `supplier`, or a property inside a value such as `supplier.vatId`). Each field goes through `FieldValue.Correct` (previous value and provenance kept in its history), a `Correction` row is written per change, the denormalised columns are refreshed and `document.corrected` is logged. Shorthands: a string for an identifier, a bare number for an amount (document currency). Unknown path or wrong value → `400` validation problem naming the path; approved/rejected documents → `409`. |
| `GET /review-tasks` | Review | Open and InProgress by default (`status=` to change), `assignedTo=me`, oldest first, each with its `DocumentSummary`. |
| `POST /review-tasks/{id}:claim` / `:release` | Review | Claim assigns the caller (`409` if someone else holds it); release by the assignee or a tenant admin. |
