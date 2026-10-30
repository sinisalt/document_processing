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
| `POST /tenants`, `PATCH /tenants/{id}`, `POST /tenants/{id}:suspend` / `:activate` | PlatformAdmin | Creation also adds the tenant's default `ApiUpload` and `PortalUpload` channels. Runs on a platform-scoped context, so no `X-Tenant-Id` is needed. |
| `GET /members`; `POST /members`, `PATCH /members/{userId}`, `DELETE /members/{userId}` | Read; TenantAdmin | The last TenantAdmin can be neither demoted nor removed (409). |
| `GET /channels`; `POST`, `PATCH /channels/{id}`, `DELETE /channels/{id}` | Read; TenantAdmin | `config` is a free JSON object per channel type; the secret reference is write-only (`hasSecret` is all a client sees). Delete is refused once ingestions reference the channel (409). |
| `GET /suppliers`, `GET /suppliers/{id}`, `GET /suppliers/{id}/activity`; `POST`, `PATCH`, `DELETE` | Read; TenantAdmin | `q` matches the folded name (`Identifiers.NormalizeName`, the same function stage 5 matches with) or the exact VAT id. Delete is refused while documents reference the supplier (409); the activity view is the twenty most recent documents. |
| `GET /settings`, `PUT /settings` | Read; TenantAdmin | Review policy (`autoApproveConfidenceThreshold` 0..1, `alwaysReviewSupplierIds` of this tenant, `retentionYears` 1..30, `shareExtractorProfiles`) plus the read-only tenant identity. |
| `GET /llm-policy`, `PUT /llm-policy` | TenantAdmin | Every configuration id must be one of the tenant's own; platform configurations are resolved by the gateway, not chosen here. |
| `GET /llm-providers`, `POST`, `POST /llm-providers/{id}:disable` / `:enable`, `DELETE` | TenantAdmin | `apiKey` is write-only: it goes to `ISecretStore` under `tenant/{tenantId}/{configId}`; the row keeps the reference, `keyLast4` and a SHA-256 fingerprint. Delete is refused while the policy references the configuration (409). |
| `GET /llm-usage?from=&to=&groupBy=day\|stage\|model` | Read | Calls, tokens, errors and estimated cost; inclusive UTC dates, last 30 days by default. |
| `GET /llm/providers`, `POST`, `POST /llm/providers/{id}:disable` / `:enable` | PlatformAdmin | Platform-scope configurations; the key goes to `platform/{configId}`. `:disable` is the kill switch. |
| `GET /llm/usage?from=&to=&groupBy=tenant\|model\|day` | PlatformAdmin | Across tenants, for invoicing platform-key spend. |

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
