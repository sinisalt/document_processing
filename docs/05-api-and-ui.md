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

**Platform-scope writes.** The review actions (`:claim`, `:release`, `:approve`, `:reject`, `:reprocess`,
`PATCH /fields`) also work in platform scope: an Operator or SuperAdmin without `X-Tenant-Id` may act on any
tenant's document, which is how the global review queue of the admin console is worked. Every row such a
call writes (review task update, `DocumentEvent`, `Correction`, the `Publish` message) carries the
*document's* `TenantId`, never a platform marker, so the tenant sees the decision, the timeline and the
corrections through its own scope exactly as if one of its reviewers had made them. Uploads are the
exception: `POST /ingestions` needs a tenant and answers `400` in platform scope.

**Concurrency.** Every state change is a conditional update at the database (`UPDATE … WHERE Id = @id AND
Status = @statusTheCallerSaw`, or `… AND AssignedTo IS NULL AND Status = 'Open'` for a claim), executed
with `ExecuteUpdateAsync`; the events, task closures and queue messages are written only when that update
changed a row. Six simultaneous `:approve` calls therefore yield one `200`, one `document.approved` event
and one `Publish` message, the other five `409 Document changed`; six simultaneous `:claim` calls yield one
`200` and five `409 Task already claimed`. A `DbUpdateConcurrencyException` (from the `Version`
concurrency token on `Document`, `ReviewTask` and `Ingestion`) is mapped to `409` globally in `Program.cs`,
so a lost optimistic update on a tracked write such as `PATCH /fields` is never a `500`.

| Endpoint | Policy | Notes |
|---|---|---|
| `POST /ingestions` | Ingest | `multipart/form-data` with one or more file parts (max 20 files, 50 MB each, whole body ≤ 1 GB streamed, optional `channelId` field; default: the tenant's ApiUpload channel, created as "API upload" on first use) or `application/json` `{fileName, contentType, base64, channelId?}` (one file, body capped at ~70 MB because it is buffered; non-file form values keep the framework's 4 MB default). Stores each file content-addressed in the raw container, creates the `Ingestion` (+ one `IngestionItem` per file, kind from the content type) and enqueues `ProcessIngestion`. `202` with `{id, status, items[{id, fileName, sizeBytes, sha256}]}` and `Location`. |
| `Idempotency-Key` on `POST /ingestions` | | Per tenant, 24 h window. The files are hashed first and the key looked up *before* any blob is written: inside the window the same request (channel + file names + hashes) replays the first response, a different one is `422`; a key older than 24 h is free again and its stale row is replaced in the same transaction as the new one, so a key reused daily keeps working. `409 Upload in progress` is reserved for a genuine race: only a unique-index violation on `(TenantId, Key)` whose winning row is younger than 60 s maps to it; any other save failure is the error it is. Expired keys are purged opportunistically (on upload, at most once an hour per tenant). Two simultaneous *first* uploads of a tenant may each create an "API upload" channel; this is tolerated on purpose (a tenant may run several API channels, so there is no unique index), later uploads use the oldest and the extra channel can be deleted through `/channels`. |
| `GET /ingestions`, `GET /ingestions/{id}` | Read | Newest first, cursor-paginated; the detail lists items with the documents split from them. |
| `GET /documents` | Read | Filters `status` (comma list), `type`, `supplierId`, `flag` (unresolved flag code), `from`/`to` (issue date), `q` (document number or supplier name contains); `cursor` + `limit` (default 50, max 200), newest first. `{items: DocumentSummary[], nextCursor}`. |
| `GET /documents/{id}` | Read | `DocumentDetail`: the summary columns + `canonical` (the stored JSON verbatim, so every `FieldValue` keeps value, status, confidence, source and history) + `flags` + `costs` + `ingestion` + `pageImageUrls`. |
| `GET /documents/{id}/pages/{n}.png`, `/original`, `/text` | Read | Stream the derived PNG (ETag = content hash, `304` on `If-None-Match`), the ingested file and the text-layer JSON; `404` problem when missing. All three send `X-Content-Type-Options: nosniff`. The original is served `inline` under the uploaded content type only for `application/pdf` and `image/*` except `image/svg+xml` (what the viewer renders); anything else is `Content-Disposition: attachment` as `application/octet-stream`, so an uploaded HTML or SVG file can never execute as a page of the API's origin. |
| `GET /documents/{id}/events` | Read | Timeline ordered by time, then id. |
| `POST /documents/{id}:approve` | Review | From `NeedsReview` or `Validated`; sets `ApprovedAt/By`, closes open review tasks (`Done`), writes `document.approved`, enqueues `Publish`. Body `{comment?}` optional. `409 Invalid transition` on any other state, `409 Document changed` when another caller changed the document between this call's read and its conditional update. |
| `POST /documents/{id}:reject` | Review | Body `{reason}` required; otherwise as approve, with `document.rejected`. |
| `POST /documents/{id}:reprocess` | Review | Body `{fromStage?}` ∈ Normalize, Classify, Extract (default), Validate. Allowed from Failed, NeedsReview, Rejected, Validated (`DocumentStateMachine.CanReprocess`); resets the status to the one before that stage (Extract → Matched), skips open review tasks, writes `document.reprocessed`, enqueues that stage. The stage must be one the document has reached: its timeline (`DocumentEvents`, the pipeline writes one transition per stage) must show the status the stage would reset to, otherwise `409 Stage not reached` names the latest stage that may rerun. A document that failed in Normalize can rerun Normalize, not Extract (which would run without a text layer). |
| `PATCH /documents/{id}/fields` | Review | Only while the document is `NeedsReview`, `Validated` or `Failed` (`409 Document not correctable` otherwise: settled documents must be reprocessed first, in-flight ones are still being written by the pipeline). `{corrections: [{path, value, reason?}]}` with canonical paths (`invoiceNumber`, `lines[2].quantity`, `supplier`, or a settable property inside a value such as `supplier.vatId`; computed properties such as `supplier.normalizedVatId` are not addressable). Each field goes through `FieldValue.Correct` (previous value and provenance kept in its history), a `Correction` row is written per change, the denormalised columns are refreshed and `document.corrected` is logged. Shorthands: a string for an identifier, a bare number for an amount (document currency). An identifier is always rebuilt from its verbatim text (`{verbatim, normalized}` objects are accepted, the normalized form is recomputed, blank is refused, `invoiceNumber.normalized` cannot be addressed). Currency codes (the `currency` field and `Money.currency`) are upper-cased and must be three letters (`Document.Currency` is `char(3)`), so `"EURO"` is a `400`, `"eur"` becomes `EUR`. Unknown path or wrong value → `400` validation problem naming the path. |
| `GET /review-tasks` | Review | Open and InProgress by default (`status=` to change), `assignedTo=me`, oldest first, each with its `DocumentSummary`. |
| `POST /review-tasks/{id}:claim` / `:release` | Review | Claim is a conditional update of an unassigned open task: exactly one of simultaneous claimants wins, the others get `409 Task already claimed` naming the holder; re-claiming one's own task is idempotent. Release by the assignee or a tenant admin undoes exactly the assignment the caller saw (`409 Task changed` when it was claimed, released or closed in the meantime). |

## Web implementation

`web/` is one pnpm workspace (Node 22, Vite 8, React 19, TypeScript strict): `packages/api-client` (generated
`openapi-typescript` schema + `openapi-fetch` client, the contract), `packages/ui` (shadcn/ui components, Tailwind v4
tokens, dark mode via `next-themes`), `packages/app-shell` (everything both apps share above the screen level), and
the two apps `apps/customer-portal` and `apps/admin-console`, which only add screens. `web/e2e` holds the Playwright
suite.

### `@docproc/app-shell`

| Area | What it gives a screen |
|---|---|
| Auth | `AppProviders` → `AuthProvider` with two modes from `VITE_AUTH_MODE`. **`dev`** (the apps' `.env.development`): `SignInPage` lists `GET /dev/users` as cards and `POST /dev/token` on click; the token lives in `sessionStorage` with its expiry (`createTokenStore`, 30 s safety skew) and sign-out clears it. **`oidc`** (`.env.production`): `react-oidc-context` + `oidc-client-ts` against `VITE_OIDC_AUTHORITY` / `VITE_OIDC_CLIENT_ID`, authorization code + PKCE redirect flow, silent renew, `code`/`state` stripped from the URL after the callback. `useAuth()` exposes `{ user: { name, displayName, email, roles, permissions, tenantId, scope }, accessToken, status, isAuthenticated, signIn, signOut, hasRole, hasPolicy }`; `user` is `GET /api/v1/me` after sign-in. `hasPolicy` prefers the API's `permissions` and otherwise mirrors `Policies.RolesByPolicy` (`rolesByPolicy` in `auth/policies.ts`). |
| Routing | TanStack Router, file-based: `src/routes/__root.tsx` renders `AppProviders` + `AppShell`, every other file under `src/routes` is a screen; `@tanstack/router-plugin` writes `src/routeTree.gen.ts` (committed, regenerated on `dev`/`build`; on a merge conflict delete it and run `pnpm build`). `AppShell` is the gate: anonymous → `/sign-in?redirect=…` (same-origin paths only), signed in on `/sign-in` → back, otherwise `AppLayout` around the `Outlet`. The apps register their router type (`declare module … Register`), so screens get typed `Link`/`navigate`; the shell navigates by `href`. `router.tsx` sets `LoadingScreen`, `NotFoundPage` and `RouteErrorPage` as the defaults. |
| API + server state | `ApiProvider` creates the one `@docproc/api-client` with `getAccessToken` from the session and a `QueryClientProvider`. `useApi().client.GET("/api/v1/documents", { params: { query } })` is fully typed from `schema.d.ts`; wrap every call in `unwrap(...)` so a problem-details error becomes a thrown `ApiError` (`status`, `title`, `fieldErrors`). The global `onError` toasts (sonner): 409 shows the problem title and refetches active queries (concurrent edit, invalid transition, claimed task), 400 shows the first field error, 403 the title, 401 is handled by sign-out, 404 is left to the page; `meta: { silent: true }` on a query/mutation opts out. Defaults: `staleTime` 30 s, no refetch on focus, retries only for 5xx/network. `queryKeys.<resource>.list(filters) / detail(id) / all` is the key factory (`cursor`/`limit` are stripped from list keys); `useCursorList({ queryKey, queryFn })` pages any `{ items, nextCursor }` list with `useInfiniteQuery`. `TenantSwitcherProvider`/`TenantSwitcher` (admin header, SuperAdmin only) set `X-Tenant-Id`; changing it clears the query cache. |
| Layout | `AppLayout`: shadcn sidebar built from the app's `src/nav.ts` (`NavItem { to, labelKey, icon, policy?, roles?, exact? }` or sections; entries the user's policies do not satisfy are hidden), header with `role="banner"` holding the optional tenant switcher and `UserMenu` (avatar, name, e-mail, roles, scope, language en/de, light/dark/system, sign out), `#main-content` for the routed screen. `ErrorBoundary`/`ErrorPage`, `NotFoundPage`, `LoadingScreen`, `PageHeader`, `EmptyState`. |
| Components | `DataTable` (TanStack Table v9 `useTable` with `dataTableFeatures`: sorting + column visibility; `createDataTableColumnHelper<T>()` for typed columns; skeleton rows, empty state, optional toolbar/footer for cursor paging), `ConfirmDialog` (shadcn alert dialog, awaits `onConfirm`, stays open on failure), `StatusBadge` / `ReviewTaskStatusBadge` / `TenantStatusBadge`, `FieldStatusBadge` / `ConfidenceBadge` / `SourceBadge`. |
| Utilities | `formatMoney(amount, currency, locale)`, `formatDate`, `formatDateTime`, `relativeTime`, `formatPercent`, `formatBytes`, `initials` (locale defaults to the i18n language; `number \| string` accepted because the generated schema types doubles that way); field helpers `parsePath` / `getByPath` / `setByPath` (immutable) / `lineItemOf` over canonical paths such as `lines[2].quantity`; `FieldValue` types with `isLowConfidence(field, threshold)`, `hasValue` and the badge-variant maps. |
| i18n | i18next + react-i18next + browser language detector (`localStorage` key `docproc.lang`, then `navigator`), en and de, namespace `common` from the shell; each app calls `createI18n({ resources: { en: { portal }, de: { portal } }, defaultNS: "portal" })` from `src/i18n/index.ts`. Every visible string goes through `t()`; shell components address `common:` explicitly, screens read their app namespace by default. |

### Conventions for screens

- One file per route under `src/routes`; export `Route = createFileRoute("/path")({ component })`. Append the nav
  entry in `src/nav.ts` and the label under `nav` in `src/i18n/<lng>/<app>.json`.
- Server state only through TanStack Query with `queryKeys`; after a mutation invalidate the resource's `all` key
  (or the `detail(id)` key when the list cannot change). Never hand-write `fetch`: the typed client plus `unwrap`.
- Forms: react-hook-form + zod with the shadcn `Form` primitives; map an `ApiError.fieldErrors` entry back onto the
  form field whose name is the API's path.
- Components come from `@docproc/ui` (add missing shadcn ones with `pnpm dlx shadcn@latest add <name>` inside
  `packages/ui`, fix the generated `cn` import to `@/lib/utils`, export from `src/index.ts`). The `@/` alias inside that
  package is resolved for consumers by `docprocAlias()` (`packages/ui/vite.ts`) in each app's `vite.config.ts` and the
  second `paths` entry in its `tsconfig.json`.
- Tests: vitest (jsdom) next to the code for anything with logic; Playwright in `web/e2e` for flows against the
  dev-mode API (`E2E_API_URL`, `E2E_PORTAL_URL`, `E2E_ADMIN_URL`; `tests/sign-in.spec.ts` is the smoke test and skips
  with the reason when the servers are not there).
