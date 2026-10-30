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

### Tenancy and settings endpoints as built (v1)

The tenant-scoped resources are mounted at the root of `/api/v1`, not under `/tenants/{id}/...`:
the tenant is always the resolved scope (the token's `tenant_id`, or `X-Tenant-Id` for a
SuperAdmin), never a path or body parameter, so a tenant user cannot even spell another
tenant's URL. Custom actions use the `:verb` suffix. Lists are cursor-paginated
(`?cursor=&limit=`, `{ items, nextCursor }`); enums travel as their names, case-insensitive on
input; every error is a problem-details body, field errors a validation problem.

| Route | Policy | Notes |
|---|---|---|
| `GET /tenants`, `GET /tenants/{id}` | Platform (Operators may read) | `?status=&q=` filters; ordered by slug. |
| `POST /tenants`, `PATCH /tenants/{id}`, `POST /tenants/{id}:suspend` / `:activate` | PlatformAdmin | Creation also adds the tenant's default `ApiUpload` and `PortalUpload` channels. Runs on a platform-scoped context, so no `X-Tenant-Id` is needed. A slug taken between the existence check and the insert (concurrent creates) is the same 409 as one found by the check. |
| `GET /members`; `POST /members`, `PATCH /members/{userId}`, `DELETE /members/{userId}` | Read; TenantAdmin | The last TenantAdmin can be neither demoted nor removed (409). A `userId` may not contain `/`, whitespace or control characters (it must survive the round trip through the route); a duplicate add racing the check is a 409, not a 500. |
| `GET /channels`; `POST`, `PATCH /channels/{id}`, `DELETE /channels/{id}` | Read; TenantAdmin | `config` is a free JSON object per channel type, at most 16 KB serialised. The credential is the write-only `secret` field: it goes to `ISecretStore` under the server-generated reference `tenant/{tenantId}/channel/{channelId}` (a client never names a reference, so it cannot point a channel at another tenant's or the platform's key); `PATCH` with a value rotates it, with an empty string removes it; `hasSecret` is all a client sees, and delete removes the secret with the channel. A stored config that is not valid JSON reads as `{}` (logged) instead of failing the list. Delete is refused once ingestions reference the channel (409). |
| `GET /suppliers`, `GET /suppliers/{id}`, `GET /suppliers/{id}/activity`; `POST`, `PATCH`, `DELETE` | Read; TenantAdmin | `q` matches the folded name (`Identifiers.NormalizeName`, the same function stage 5 matches with) or the exact VAT id. A name is refused when it folds to more than 300 characters (`ß` becomes `SS`), the column width. Delete is refused while documents reference the supplier (409); the activity view is the twenty most recent documents. |
| `GET /settings`, `PUT /settings` | Read; TenantAdmin | Review policy (`autoApproveConfidenceThreshold` 0..1, `alwaysReviewSupplierIds` of this tenant, `retentionYears` 1..30, `shareExtractorProfiles`) plus the read-only tenant identity. |
| `GET /llm-policy`, `PUT /llm-policy` | TenantAdmin | Every configuration id must be one of the tenant's own; platform configurations are resolved by the gateway, not chosen here. |
| `GET /llm-providers`, `POST`, `POST /llm-providers/{id}:disable` / `:enable`, `DELETE` | TenantAdmin | `apiKey` is write-only (8 to 4096 characters, so `keyLast4` never shows most of it): it goes to `ISecretStore` under `tenant/{tenantId}/{configId}`; the row keeps the reference, `keyLast4` and a SHA-256 fingerprint. `models` names each stage once (two spellings of one stage are refused rather than merged). Delete is refused while the policy references the configuration (409); otherwise it removes the row and then the key from the secret store. |
| `GET /llm-usage?from=&to=&groupBy=day\|stage\|model` | Read | Calls, tokens, errors and estimated cost; inclusive UTC dates, last 30 days by default. |
| `GET /llm/providers`, `POST`, `POST /llm/providers/{id}:disable` / `:enable` | PlatformAdmin | Platform-scope configurations; the key goes to `platform/{configId}`. `:disable` is the kill switch. |
| `GET /llm/usage?from=&to=&groupBy=tenant\|model\|day` | PlatformAdmin | Across tenants, for invoicing platform-key spend. |

Enum-valued fields (`status`, `type`, `role`, stage names) accept a member name in any case and
nothing else: a numeric string such as `"0"` is a validation error, never the first member.

**Suspension is enforced at the boundary, for people as well as machines.** `TenantResolutionMiddleware`
checks the tenant a bearer token's `tenant_id` claim resolves to against the tenant table through
`TenantStatusCache` (one platform-scoped read per tenant every 30 seconds): a `Suspended` or `Closed`
tenant answers 403 `Tenant suspended` on every endpoint, a tenant id that does not exist 404 `Unknown
tenant` (not cached, so a tenant created a moment ago is seen at once). API keys were already refused
at validation time (`ApiKeyValidator`) and are not checked twice. Platform scope is unaffected, and a
SuperAdmin acting through `X-Tenant-Id` may still look into a suspended tenant. After `:activate` the
tenant's users are back within the cache window.

Still to come on these resources: `:test` and `:rotate` for providers (credential validation
records `validatedAt` / `lastError`), audit logging of who changed what, idempotency keys.

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
