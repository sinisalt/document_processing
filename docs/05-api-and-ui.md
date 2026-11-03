# API and UI

> **Superseded in part by [docs/11](11-document-types-and-mappings.md)** (2026-11-02): document types, supplier
> layouts, identification rules, source routing and output mappings are tenant configuration, not code. Where this
> document assumes fixed document types or automatically synthesised profiles only, docs/11 wins.

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
/documents/{id}/pages/{n}/words  words of a page with boxes; ?bbox= gives the selection's text and its readings
/documents/{id}/regions:read | regions:table    region OCR; the table tool (propose, read every page, write)
/documents/{id}/table-reports    stored table reports (the page strip)
/documents/{id}/parts            the documents of the same attachment (the join dialog)
/documents/{id}/events           timeline
/documents/{id}:approve | :reject | :reprocess | :set-type | :assign-supplier | :create-supplier | :split | :join
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

**Errors.** A validation problem's `errors` (`{ field: [message] }`) is English. Where the check has a stable
code, the problem also carries `errorCodes`: `{ field: { code, params } }` with invariant string parameters
(`requiredMaxLength`/`maxLength` `{maxLength}`, `notBlank`, `currencyCode`, `countryCode`, `timeZone`, `slug`
`{maxLength}`, `range` `{min, max}`, `positive`, `httpUrl` `{maxLength}`, `maxItems` `{maxItems}`, `iban` and
`emailDomain` `{value}`, the refused entry as sent; `EndpointSupport.ValidationCodes`). The apps format numeric
parameters for the UI language, except `value`, which they show as sent. The apps
show `common:apiErrors.<code>` in the UI language and the English message for a field without a code or a code they
do not know. Flags carry their own `params` the same way (docs/02 §5 "Flag parameters").

| Route | Policy | Notes |
|---|---|---|
| `GET /tenants`, `GET /tenants/{id}` | Platform (Operators may read) | `?status=&q=` filters; ordered by slug. |
| `POST /tenants`, `PATCH /tenants/{id}`, `POST /tenants/{id}:suspend` / `:activate` | PlatformAdmin | Creation also adds the tenant's default `ApiUpload` and `PortalUpload` channels. Runs on a platform-scoped context, so no `X-Tenant-Id` is needed. A slug taken between the existence check and the insert (concurrent creates) is the same 409 as one found by the check. |
| `GET /members`; `POST /members`, `PATCH /members/{userId}`, `DELETE /members/{userId}` | Read; TenantAdmin | The last TenantAdmin can be neither demoted nor removed (409). A `userId` may not contain `/`, whitespace or control characters (it must survive the round trip through the route); a duplicate add racing the check is a 409, not a 500. |
| `GET /channels`; `POST`, `PATCH /channels/{id}`, `DELETE /channels/{id}` | Read; TenantAdmin | `config` is a free JSON object per channel type, at most 16 KB serialised. The credential is the write-only `secret` field: it goes to `ISecretStore` under the server-generated reference `tenant/{tenantId}/channel/{channelId}` (a client never names a reference, so it cannot point a channel at another tenant's or the platform's key); `PATCH` with a value rotates it, with an empty string removes it; `hasSecret` is all a client sees, and delete removes the secret with the channel. The reference the API writes or deletes through is always recomputed from the row's tenant and channel id, never read from the row: a row written before references were server-generated may still carry a client-chosen one (`platform/{configId}`, another tenant's), and that legacy reference is logged, dropped from the row on the next rotate or clear, and never touched in the store. A stored config that is not valid JSON reads as `{}` (logged) instead of failing the list. Delete is refused once ingestions reference the channel (409). |
| `GET /suppliers`, `GET /suppliers/{id}`, `GET /suppliers/{id}/activity`; `POST`, `PATCH`, `DELETE` | Read; TenantAdmin | `q` matches the folded name (`Identifiers.NormalizeName`, the same function stage 5 matches with) or the exact VAT id. A name is refused when it folds to more than 300 characters (`ß` becomes `SS`), the column width. The lists are bounded, since every Match stage run of the tenant loads every supplier: at most 20 `ibans`, each an IBAN once spaces, dots and dashes are taken out (two letters, two check digits, 11 to 30 letters or digits; the check digits are not verified; `iban`), at most 20 `emailDomains`, each a domain name (`example.com`, no `@`; `emailDomain`), and at most 50 `aliases` of at most 300 characters (`maxItems`, `maxLength`), counted as stored (without repeats); `PATCH` checks the lists it is given, except one equal to the stored list once both are cleaned (same entries in the same order), which it leaves as it is. A supplier stored before these caps (P1) therefore keeps its longer lists, or entries the format checks now refuse, through an edit that sends them back unchanged (the portal sends every list with every save); the first write that changes a list must bring that whole list within the caps. A `POST` or `PATCH` body is at most 64 KB (`413`). Delete is refused while documents or extractor profiles (of any tenant) reference the supplier (409 "Supplier in use"; the foreign keys are `NO ACTION`, docs/09 "Globalization"); the activity view is the twenty most recent documents. |
| `GET /settings`, `PUT /settings` | Read; TenantAdmin | Review policy (`autoApproveConfidenceThreshold` 0..1, `alwaysReviewSupplierIds` of this tenant, `retentionYears` 1..30, `shareExtractorProfiles`) plus the read-only tenant identity. |
| `GET /llm-policy`, `PUT /llm-policy` | TenantAdmin | Every configuration id must be one of the tenant's own; platform configurations are resolved by the gateway, not chosen here. |
| `GET /llm-providers`, `POST`, `POST /llm-providers/{id}:disable` / `:enable`, `DELETE` | TenantAdmin | `apiKey` is write-only (8 to 4096 characters, so `keyLast4` never shows most of it): it goes to `ISecretStore` under `tenant/{tenantId}/{configId}`; the row keeps the reference, `keyLast4` and a SHA-256 fingerprint. Responses carry `keyLast4` and `keyFingerprint` (the first 16 hex digits of the SHA-256 of the key's UTF-8 bytes), so a client that holds a key can tell whether it is the stored one (`docker/llm-setup.mjs` does) without the key ever being returned. `models` names each stage once (two spellings of one stage are refused rather than merged). `endpoint` must be `https` to a public host: loopback, private, link-local and other reserved addresses and local names are refused with 400, and the worker checks the resolved address again at connect time (docs/08 "Endpoint restrictions"). Delete is refused while the policy references the configuration (409); otherwise it removes the row and then the key from the secret store. |
| `GET /llm-usage?from=&to=&groupBy=day\|stage\|model` | Read | Calls, tokens, errors and estimated cost; inclusive UTC dates, last 30 days by default. |
| `GET /llm/providers`, `POST`, `POST /llm/providers/{id}:disable` / `:enable` | PlatformAdmin | Platform-scope configurations; the key goes to `platform/{configId}`. Their `endpoint` may be plain `http` or a private address (self-hosted models). `:disable` is the kill switch. |
| `GET /llm/usage?from=&to=&groupBy=tenant\|model\|day` | PlatformAdmin | Across tenants, for invoicing platform-key spend. |

Enum-valued fields (`status`, `type`, `role`, stage names) accept a member name in any case and
nothing else: a numeric string such as `"0"` is a validation error, never the first member.

**Suspension is enforced at the boundary, for people as well as machines.** `TenantResolutionMiddleware`
checks the tenant a bearer token's `tenant_id` claim resolves to against the tenant table through
`TenantStatusCache` (one platform-scoped read per tenant every 30 seconds): a `Suspended` or `Closed`
tenant answers 403 `Tenant suspended` on every endpoint, a tenant id that does not exist 404 `Unknown
tenant` (not cached, so a tenant created a moment ago is seen at once). API keys were already refused
at validation time (`ApiKeyValidator`) and are not checked twice; the middleware recognises them by the
authentication scheme that produced the principal (`ApiKey`), never by a claim, so a bearer token that
carries `auth_method=apikey` is still a person's token and is still checked. Platform scope is
unaffected, and a SuperAdmin acting through `X-Tenant-Id` may still look into a suspended tenant.
`:suspend`, `:activate` and a `PATCH` that changes `status` invalidate the cache entry after the write,
so on the node that took the change the tenant's users are locked out (or back in) with their very next
request; the 30-second TTL is the bound for every other node.

Still to come on these resources: `:test` and `:rotate` for providers (credential validation
records `validatedAt` / `lastError`), audit logging of who changed what, idempotency keys.

### Output endpoints as built (docs/11 §13, P3 part A)

Every route below needs the tenant's `outputs` switch (`403 Feature not enabled` naming `features.outputs`
otherwise; the lookup tables take `outputs` or `configurableTypes`). `IntegrationAdmin` is a policy that TenantAdmin
and SuperAdmin satisfy (docs/11 §16). Nothing here delivers yet: the fan-out, `ProcessDelivery`, the transports,
`:test`, the deliveries and feeds come with part B.

| Route | Policy | Notes |
|---|---|---|
| `GET /output-templates` | Read | The platform integration templates (`std.event`, `generic-ap-json`, docs/11 §13.9) with `copiedAs`, the tenant's mappings copied from each. |
| `GET /output-mappings`, `GET /output-mappings/{key}` | Read | The tenant's mappings and the platform templates (`origin` `Platform`, read-only), each with its versions (`state` Draft, Published, Deprecated; `revision`; `hasTargetSchema`). A key names the tenant's mapping before a template of the same key. |
| `POST /output-mappings` | IntegrationAdmin | `{from: {template}, key?, name?}` copies a template (origin `Copied`, `baseTemplate`), or `{key, name, description?, sourceTypeKey?, engine?, expression?, contentType?, targetSchema?}` creates the tenant's own (origin `Custom`); either way version 1 is a draft. Keys follow the type key syntax and may not start with `std.`; 409 for a key the tenant has. |
| `PATCH /output-mappings/{key}` | IntegrationAdmin | `name`, `description`, `sourceTypeKey` (empty clears it), `status` (Active, Archived). A platform template answers 404 (copy it). |
| `POST /output-mappings/{key}/versions` | IntegrationAdmin | The next version as a draft, from the published one; 409 when a draft exists. |
| `GET /output-mappings/{key}/versions/{v}` | Read | `expression`, `fileNameTemplate`, `targetSchema`, `referencedPaths` (published versions), `sampleDocumentIds`; the ETag is the revision. |
| `PUT /output-mappings/{key}/versions/{v}` | IntegrationAdmin | Replaces a draft: `{engine (jsonata, passthrough), expression?, contentType?, fileNameTemplate?, targetSchema?, sampleDocumentIds?}`, `If-Match` required (428 without, 412 on a stale revision, 409 on a published version). `fieldmap` and `liquid` are refused with the phase they come in. The expression is at most 256 K characters, the schema at most 256 K. |
| `POST /output-mappings/{key}/versions/{v}:publish` | IntegrationAdmin | Optional `{changeNote}`. The renderer parses the expression and builds the target schema; a problem answers 422 `{errors: [{kind, code, message, position}], referencedPaths, tables}`. The paths the expression reads are stored (`ReferencedPathsJson`); the published version before it is deprecated. |
| `POST /output-mappings:preview` | IntegrationAdmin | `{mappingVersionId}` or `{expression, engine?, targetSchema?}`, over `{documentId}` (the document's values view, `meta.event` from `event`, default `document.approved`) or `{sampleValues}`; `includeValues: true` returns the view too. Answers `{output, undefined, outputBytes, errors, schemaErrors: [{path, location, keyword, message}], referencedPaths, tables, elapsedMs, values}`; a render error is in `errors`, not an HTTP error. A document or version the tenant cannot see is 404. The request is at most 8 MB. |
| `GET /destinations`, `GET /destinations/{id}` | IntegrationAdmin | `{id, name, transport, config, hasSecret, secretSetAt, hasPreviousSecret, status, lastTestAt, lastTestResult}`; secrets never. |
| `POST /destinations` | IntegrationAdmin | `{name, transport (http, blob, pull), config, secret?}`; `sftp` and `email` are refused (P6). `config` is checked per transport: http `{method (POST, PUT, PATCH), urlTemplate (absolute http(s), no user info, `{{...}}` placeholders), headers (at most 20, no credential or transport headers such as Authorization, Cookie, Idempotency-Key), timeoutSeconds 1..120, successCodes 200..299, auth {type: none, apiKeyHeader (headerName), basic (username), bearer, oauth2ClientCredentials (tokenUrl, clientId, scope), hmacSha256}, responseMapping}`; blob `{path (relative, no `.`/`..`), fileNameTemplate}`; pull `{retentionDays 1..365}`; unknown settings are refused. The secret goes to `ISecretStore` under `tenant/{tenantId}/destination/{id}/{1|2}`. 409 for a name the tenant has. |
| `PATCH /destinations/{id}` | IntegrationAdmin | `name`, `status` (Active, Disabled), `config` (replaces it, checked as on create). |
| `PUT /destinations/{id}/secret` | IntegrationAdmin | `{secret}` (at most 8192 characters): the new secret goes to the other of the two slots, the replaced one stays as the previous secret until the next rotation (docs/11 §16). |
| `DELETE /destinations/{id}` | IntegrationAdmin | 409 while a binding, a delivery or an external reference refers to it (disable it instead); removes both secrets. |
| `GET /output-bindings`, `GET /output-bindings/{id}` | Read | `{id, name, events, documentTypeKey, supplierId, channelId, condition, mappingKey, pinnedMappingVersion, destinationId, mode, exclusiveGroup, priority, onReapproval, updateMappingKey, enabled}`. |
| `POST /output-bindings`, `PATCH /output-bindings/{id}` | IntegrationAdmin | Events of `document.approved`, `document.rejected`, `document.updated`, `document.needs_review` (default approved); the supplier, channel and destination must be the tenant's, the mapping the tenant's or a platform template, a pinned version published; `condition` is parsed by the renderer (at most 4000 characters); `onReapproval` Resend, Skip or Update (Update needs `updateMappingKey`); `mode` Batch is refused (P6). `PATCH` leaves absent fields; empty strings clear `documentTypeKey`, `condition`, `exclusiveGroup`, `updateMappingKey`, and `clearSupplier`, `clearChannel`, `clearPinnedVersion` the others. |
| `DELETE /output-bindings/{id}` | IntegrationAdmin | 409 while deliveries refer to it (disable it instead). |
| `GET /lookup-tables`, `GET /lookup-tables/{key}` | Read | The tenant's tables and the platform code lists (`platform: true`; `uom` is built in code); a table's entries `{from, to, validFrom, validTo, labels}`, ETag the revision. |
| `POST /lookup-tables`, `PATCH /lookup-tables/{key}`, `DELETE /lookup-tables/{key}` | ConfigAdmin | `{key, description?, caseSensitive?}`. |
| `PUT /lookup-tables/{key}/entries` | ConfigAdmin | `{entries: [...]}` replaces every entry (at most 50,000; `If-Match` the revision). CSV upload is not built yet. |

`POST /suppliers` and `PATCH /suppliers/{id}` take `attributes` (docs/11 §6.4): at most 50 keys (a letter, then
letters, digits, `_`, `-`, `.`, at most 64 characters), each a string of at most 400 characters, a number, a boolean or
null; `PATCH` replaces them (an empty object clears them) and leaves them when the field is absent. Responses carry
`attributes`. A reviewer's `:create-supplier` may not set them (403), as for e-mail domains and aliases.

## React applications

One monorepo, two apps sharing a component and API-client package:

### Admin console (SuperAdmin, Operator)

- Dashboard: documents per day by path (XML / profile / LLM), STP rate, review backlog,
  failed jobs, LLM spend by tenant and model, channel health (last poll, last error).
- Tenants and channels: create tenant, provision mailbox, test connection, see recent ingestions.
- Global supplier registry and extractor profiles: fingerprint, rules, success rate chart,
  sample documents, "synthesize profile" and "test profile against document" actions,
  activate / retire.
- Global review queue (cross-tenant) for operators, with a Tenant column (`ReviewTaskDto.tenantName`). Claim and
  Release need the Review policy; Release is offered on one's own task, and on someone else's only with the
  TenantAdmin policy (SuperAdmin) behind a confirm, as `ReviewTaskEndpoints` allows.
- Prompt and model management: prompt versions, which provider and model per stage, eval
  results against the gold set per provider/model.
- Platform AI providers: default configs per region, price tables, per-tenant BYOK overview,
  per-provider kill switch.
- Job inspector: pipeline timeline per document (the shell's `DataTable` with expandable rows), retry/re-run,
  dead-letter list. The re-run dialog defaults to the latest stage the document's timeline shows it reached and
  disables later ones (they would answer `409 Stage not reached`). Page thumbnails load the first 12 pages, the rest
  on "Show all", each one when it scrolls into view; a missing page (`404`) is not retried.
- Every role can sign in to the console (the shell does not gate the app); queries and actions follow the API's
  policies (`GET /tenants` only with Platform, the queue and Retry only with Review), and a user without the
  Platform policy sees a notice that the console is for platform operators. The cross-tenant documents list has a
  Tenant column (`DocumentSummary.tenantName`), hidden for tenant users.

### Customer portal (TenantAdmin, Reviewer, Viewer)

- Inbox: documents by status (Needs review, Approved, Rejected, Processing), filters, search.
- Review screen: page viewer on the left with field highlights; canonical form on the right;
  flags listed at the top with one-click jump to the field; line-item grid with arithmetic
  check indicators; approve / reject / reassign; keyboard-first.
  Unsaved corrections (a value that would be sent, not a retyped "58,80" for 58.8) block approve, reject and
  reprocess, and leaving the screen (Esc, the back arrow, the sidebar, browser Back, closing the tab) asks before
  discarding them. Esc that a dialog or popover handles stays there. The line check uses the validator's money
  tolerance (0.02); the line unit is a select of `UnitOfMeasure` names with the printed unit beside it; Reprocess
  offers only the stages the timeline shows the document reached, like the console's dialog. Upload, Suppliers and
  Settings are tenant-scoped and not offered in platform scope.
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
- Dates are written and read in the UI language, inputs included: date fields use `LocalizedDateInput`
  (app-shell, on `DateInput` from `@docproc/ui`: a text field with a calendar popover that shows
  `28.10.2026` in German and `10/28/2026` in English and hands the form an ISO date). A native
  `type="date"` input follows the browser's locale instead, which put US dates beside German ones.
- Status colours: red (`destructive`) is for failures and blocking flags only. A document that waits
  for a reviewer and an open review task are `warning` (amber), a passed check is `success` (green);
  both are Badge variants of `@docproc/ui`.

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
`200` and five `409 Task already claimed`. `Document`, `ReviewTask` and `Ingestion` carry an
application-maintained `Version` concurrency token (`IVersioned`): `DocProcDbContext.SaveChanges` bumps it
for every tracked modification, and the conditional updates bump it themselves (`SET Version = Version + 1`),
so the two kinds of write see each other. A tracked writer that loaded the row before a decision or a
claim (the pipeline, `PATCH /fields`) fails its own save with `DbUpdateConcurrencyException`, which is
mapped to `409` globally in `Program.cs`, so a lost optimistic update is never a `500` and never a silent
overwrite. `:approve`, `:reject` and `:reprocess` run the conditional update, the event and the task
closures in one transaction and enqueue the pipeline message only after the commit, so a failure in the
second step cannot leave an approved document with no event and open tasks, or a queued message for a
decision that was rolled back. `:claim` and `:release` are single statements and need no transaction.

| Endpoint | Policy | Notes |
|---|---|---|
| `POST /ingestions` | Ingest | `multipart/form-data` with one or more file parts (max 20 files, 50 MB each, whole body ≤ 1 GB streamed, optional `channelId` field; default: the tenant's ApiUpload channel, created as "API upload" on first use) or `application/json` `{fileName, contentType, base64, channelId?}` (one file, body capped at ~70 MB because it is buffered; non-file form values keep the framework's 4 MB default). Stores each file content-addressed in the raw container, creates the `Ingestion` (+ one `IngestionItem` per file, kind from the content type) and enqueues `ProcessIngestion`. `202` with `{id, status, items[{id, fileName, sizeBytes, sha256}]}` and `Location`. |
| `Idempotency-Key` on `POST /ingestions` | | Per tenant, 24 h window. The files are hashed first and the key looked up *before* any blob is written: inside the window the same request (channel + file names + hashes) replays the first response, a different one is `422`; a key older than 24 h is free again and its stale row is replaced in the same transaction as the new one, so a key reused daily keeps working. Two requests racing on one key (a unique-index violation on `(TenantId, Key)`, or, when both were replacing the same expired row, the loser's delete of that row affecting nothing, which EF reports as `DbUpdateConcurrencyException`) are the one save failure that is not an error: the loser's rows were rolled back, the winner's row is read back, and the loser answers what its retry would get, the winner's `202` for the same request or `422` for a different one. Only a winning row younger than 60 s counts as that race; any other save failure is the error it is. Expired keys are purged opportunistically (on upload, at most once an hour per tenant). Two simultaneous *first* uploads of a tenant may each create an "API upload" channel; this is tolerated on purpose (a tenant may run several API channels, so there is no unique index), later uploads use the oldest and the extra channel can be deleted through `/channels`. |
| `GET /ingestions`, `GET /ingestions/{id}` | Read | Newest first, cursor-paginated; the detail lists items with the documents split from them. |
| `GET /documents` | Read | Filters `status` (comma list), `type`, `supplierId`, `flag` (unresolved flag code), `from`/`to` (issue date), `q` (document number or supplier name contains); `cursor` + `limit` (default 50, max 200), newest first. `{items: DocumentSummary[], nextCursor}`. Every `DocumentSummary` carries `tenantId` and `tenantName` (a LEFT JOIN on `Tenants` in the list statement, no per-row lookup), so a platform-scope list can say whose row it is; a tenant-scoped caller sees its own tenant's name. |
| `GET /documents/{id}` | Read | `DocumentDetail`: the summary columns + `canonical` (the stored JSON verbatim, so every `FieldValue` keeps value, status, confidence, source and history) + `flags` + `costs` + `ingestion` + `pageImageUrls`. |
| `GET /documents/{id}/pages/{n}.png`, `/original`, `/text` | Read | Stream the derived PNG (ETag = content hash, `304` on `If-None-Match`), the ingested file and the text-layer JSON; `404` problem when missing. All three send `X-Content-Type-Options: nosniff`. The original is served `inline` only when its uploaded type (parameters stripped, lower-cased) is on a fixed allow-list of what the viewer renders (`application/pdf`, `image/png`, `image/jpeg`, `image/gif`, `image/webp`, `image/tiff`, `image/bmp`, plus the aliases `application/x-pdf`, `image/jpg`, `image/pjpeg`), and then under the list's constant, never the stored string; anything else, `image/svg+xml` and other `image/*` types included, is `Content-Disposition: attachment` as `application/octet-stream`, so an uploaded HTML or SVG file can never execute as a page of the API's origin. An upload whose content type is not a single `type/subtype` of RFC 9110 tokens (a list such as `image/png,text/html`, whitespace, quotes) is stored as `application/octet-stream`. |
| `GET /documents/{id}/events` | Read | Timeline ordered by time, then id. |
| `POST /documents/{id}:approve` | Review | From `NeedsReview` or `Validated`; sets `ApprovedAt/By`, closes open review tasks (`Done`), writes `document.approved`, enqueues `Publish`. Body `{comment?}` optional. `409 Invalid transition` on any other state, `409 Document changed` when another caller changed the document between this call's read and its conditional update. For a tenant with `configurableTypes` (docs/11 §11), `409 Cannot approve` with `code` `TYPE_REQUIRED` (the type is Unknown), `NO_DEFINITION` (no published definition the tenant can use) or `NO_DATA` (no canonical data, or a tree none of whose fields holds a value, as `:set-type` with prefill none leaves it); `DocumentDetail.approvalBlocked {code, message}` says the same before the click, and `DocumentDetail.configurableTypes` whether the review screen offers the P2 tools. |
| `POST /documents/{id}:reject` | Review | Body `{reason}` required; otherwise as approve, with `document.rejected`. |
| `POST /documents/{id}:reprocess` | Review | Body `{fromStage?}` ∈ Normalize, Classify, Extract (default), Validate. Allowed from Failed, NeedsReview, Rejected, Validated (`DocumentStateMachine.CanReprocess`); resets the status to the one before that stage (Extract → Matched), skips open review tasks, writes `document.reprocessed`, enqueues that stage. The stage must be one the document has reached: its timeline (`DocumentEvents`, the pipeline writes one transition per stage) must show the status the stage would reset to, otherwise `409 Stage not reached` names the latest stage that may rerun. A document that failed in Normalize can rerun Normalize, not Extract (which would run without a text layer). |
| `PATCH /documents/{id}/fields` | Review | Only while the document is `NeedsReview`, `Validated` or `Failed` (`409 Document not correctable` otherwise: settled documents must be reprocessed first, in-flight ones are still being written by the pipeline). `{corrections: [{path, value, reason?}]}` with canonical paths (`invoiceNumber`, `lines[2].quantity`, `supplier`, or a settable property inside a value such as `supplier.vatId`; computed properties such as `supplier.normalizedVatId` are not addressable). Each field goes through `FieldValue.Correct` (previous value and provenance kept in its history), a `Correction` row is written per change, the denormalised columns are refreshed and `document.corrected` is logged. Shorthands: a string for an identifier, a bare number for an amount (document currency). An identifier is always rebuilt from its verbatim text (`{verbatim, normalized}` objects are accepted, the normalized form is recomputed, blank is refused, `invoiceNumber.normalized` cannot be addressed). Currency codes (the `currency` field and `Money.currency`) are upper-cased and must be three letters (`Document.Currency` is `char(3)`), so `"EURO"` is a `400`, `"eur"` becomes `EUR`. Unknown path or wrong value → `400` validation problem naming the path. `op: "move"` with `from` (docs/11 §12.3 "match fields", P2): the value of `from` goes into `path`, converted to its kind (an identifier's text for a text field, an amount's number for a number) with the place it was read from, and `from` is left Missing; both keep their history; one Correction row with op `move` and `SourceJson.from`. A move is between single fields, list entries or fields of rows, not into a value's member or a bag key. At most 5,000 corrections and an 8 MB body per request (`400` / `413` beyond). A repeating group holds at most its `maxItems` rows (20,000 for a top-level group without one, 100 for a group inside a row such as a price list's `tiers`), and a document's tree at most 100,000 rows across all its groups; `set` on a group and `append` beyond that are a `400` naming the path, checked before any row is built. |
| `POST /documents/{id}:set-type` | Review | docs/11 §12.1 (P2). Body `{typeKey, version?, prefill?}`; the type must have a published definition the tenant can use (`400` naming `typeKey` or `version` otherwise). Sets the type, `TypeSource=Human` and pins the version (the tenant's published one, or `version`); writes a `setType` Correction and `document.type_set`. `prefill: "none"` (default): the document stays in review with the empty tree of the type version (every field Missing; an adopted invoice or credit note of a tenant without `configurableTypes` gets the empty coded record of its kind; a type without a data path gets none and `UNSUPPORTED_TYPE`), `TYPE_UNKNOWN` is resolved, and the checks of the type run at once (`200`; `409 Type unchanged` for the same type and version with data). `prefill: "auto"`: back to `Matched` and the Extract stage reads the document for the type (`202`; `409 Stage not reached` when it never got that far). Only from `NeedsReview`, `Validated` or `Failed` (`409 Document not in review`). |
| `POST /documents/{id}:assign-supplier` | Review | Body `{supplierId}`, a supplier of the document's tenant (`400` otherwise). Sets the supplier with `supplierMatchMethod: Human` (the Match stage keeps it on a reprocess), resolves `SUPPLIER_UNMATCHED`, writes `document.supplier_assigned` and runs the type's checks again (a duplicate the supplier reveals shows at once). `200`. Re-running identification for the supplier's layouts comes with P4. |
| `POST /documents/{id}:create-supplier` | Review | Body as `POST /suppliers`, with its checks (name, identifiers, currency, the caps and formats of the lists, a name that folds to an existing supplier is `409 Supplier exists`); creates the supplier in the document's tenant and assigns it as `:assign-supplier` does, in one save. Needs a tenant scope. `201` with `{supplier, document}`. Permission decision (docs/11 §14): a Reviewer gives the name and the identifiers printed on the document (`vatId`, `taxId`, `ibans`, `defaultCurrency`); `emailDomains`, `aliases` and `alwaysReview`, which steer the Match stage and the review of every later document of the tenant, need TenantAdmin (`403 Tenant admin only` otherwise), as on `POST /suppliers`. The body of every review action (`:set-type`, `:assign-supplier`, `:create-supplier`, `:split`, `:join`) is at most 64 KB (`413`). |
| `POST /documents/{id}:split` | Review | docs/11 §12.7. Body `{parts: [{pages: [1, 2], typeKey?}, …]}`: 2 to 100 parts that list the document's pages in order, each once, every part a run of consecutive pages (`400` otherwise; `409` for an e-invoice). The first part keeps the id; every other part becomes a new document of the same attachment with its page images and its own text layer (renumbered from 1). A part without a type goes to Classify, a typed one (`TypeSource=Human`, pinned, the document's supplier) to Extract. The document's data, list columns, field index and pipeline flags are cleared (they described all its pages); its corrections stay with it; open review tasks are `Skipped`; nothing is published. A `split` Correction and `document.split` (the new parts get `document.received` with `splitFrom`). `202` with `{parts: [{documentId, pages, typeKey, status}]}`. |
| `POST /documents/{id}:join` | Review | Body `{documentIds}`: other parts of the same attachment, all `NeedsReview`, `Validated` or `Failed`, which with this document make one unbroken run of the attachment's pages (`400`/`409` otherwise). This document takes every page in attachment order and goes back to Classify; the others become `Cancelled` with `joinedIntoDocumentId` (their list columns and field index cleared, so they are never duplicates; their data stays as the audit). Joined back into the whole attachment, the document is the whole file again and is read as before the split. A `join` Correction and `document.joined` on every part; nothing is published. `202` with the joined document. |
| `GET /documents/{id}/pages/{n}/words` | Read | docs/11 §12.3 (P2). `?bbox=x0,y0,x1,y1` (page units, 0 to 1000) and `?locale=` (`de-DE`, `en-US`, …; the tenant's country by default) optional. `{page, source, locale, words: [{index, text, bBox, confidence?}], text?, bBox?, candidates: [{kind, value}]}`: `source` is `pdfText`, `ocr` or `none` (no text layer: an image page); `index` is the word's index in the stored text layer. Without a box every word of the page; with one the words whose centre lies inside it in reading order, their joined `text`, their union and the readings of the text per kind (`date` as `yyyy-MM-dd`, `decimal`, `integer`, `money` when a currency is printed, `percent` with its sign, `currency`, `identifier`, `text`). `400` for a bad box or locale, `404` for a page that does not exist. |
| `POST /documents/{id}/regions:read` | Review | docs/11 §12.3. Body `{page, bBox, locale?}`: the page image cut to the box and read by the host's OCR provider; answers like `/words` with `source: ocr`, `provider` and words without `index`. Nothing is stored. `501` with `code: OCR_NOT_CONFIGURED` when the host has no OCR provider (none ships yet), `404` without the page image. A few run at a time per tenant (`TableTool:ConcurrentReadsPerTenant`, docs/09), `429` beyond. A body above 16 KB is a `413`. |
| `POST /documents/{id}/regions:table` | Review | docs/11 §12.4. Body `{page, bBox, columns?: [{x0, x1, field?, header?, tierMinQuantity?}], headerRow?: {y0, y1}, path?, rowAnchor?, pages?, repeatHeader?, minAnchors?, continuation?, groupHeader?: {target, when?}, tierColumns?: {headerPattern?, target?, quantityField?, priceField?}, skipRows?, stopAt?, pageMargins?: {top?, bottom?}, pageOverrides?: [{page, columns: [{x0, x1}]}], locale?, repeatedLines?, write?, reason?}`. The document needs a type with a definition (`409 No type` / `No definition`). Without `columns`: proposed dividers and column fields for the box (`proposed: true`) and the rows they read on `page`. With `columns`: the rows of the mapping over `pages` (`1-*`, `2-5,7`; just `page` by default) and the table report `{rows, rejectedRows, pages, pagesWithoutRows, stoppedOnPage?, perPage: [{page, headerFound, rows, rejected, rejectedLines, skippedLines, groupHeadings, continuationLines, columns, columnsFrom, firstRows, top, bottom, headings, repeatedLines, leftOutLines, repeatedTexts}]}` (`top` and `bottom` the band of the page read, `headings` the texts of the group headings read, `repeatedLines` and `leftOutLines` the lines left out as running lines and as text in left-out columns only, `repeatedTexts` the texts of the running lines left out, the first 20), plus the first 1,000 rows with every value's page, box and printed text, and the `locale` the values were read in. A column is a tier column only when it names `tierMinQuantity` (the proposal reads it from a header such as `ab 10` or `ab 1.000`); one without a field or a quantity is left out, and its words count for nothing. `repeatedLines` `skip` (default) leaves out lines printed at the same height with the same text on at least half of the document's pages above or below its rows (running titles, page numbers, footers; a number matches when it is the same on both pages or each page's own number) unless they read as a row, or as a group heading for another category above the page's first row (not when the line's copy on the mapping page lies above the band read there: that is a running title); `keep` reads them. A row with a value in the anchor column only is rejected (`anchorOnly`). The proposal reads its page with the request's `groupHeader`. A box with nothing printed below its `headerRow` (drawn around the header only) reads every page down to `pageMargins.bottom`, or the foot of the page. `write: true` (only `NeedsReview`, `Validated`, `Failed` and a definition-driven document, `409` otherwise) stores every row as one Correction (op `set` on `path`, `SourceJson.table` with the specification and the report) and the report as the group's `DocumentTableReport`, re-validates and answers with `document`; more rows than the group's `maxItems` is a `400`. Patterns run NonBacktracking with a time limit (at most 20 of 200 characters each, and at most 500 atoms once counted repetitions are unrolled, `400` otherwise); a pattern that runs out of time is a `400`. One read runs within `TableTool:ReadBudget` (10 s) and stops when the client goes away: `422` with `code: TABLE_READ_TOO_SLOW` past the budget. A few reads run at a time per tenant, `429` beyond. A body above 2 MB is a `413`, and `pages` is at most 200 characters (`400`). |
| `GET /documents/{id}/table-reports` | Read | The stored table reports: `[{path, origin, correctionId, producedAtVersion, createdAt, report}]`. A report goes when its group is written by another source (`set` of the whole group, the Extract stage, `:set-type`, `:split`, `:join`). |
| `GET /documents/{id}/parts` | Read | docs/11 §12.7 (P2). The documents of the same attachment as this one, in page order: `[{documentId, pageStart, pageEnd, pageCount, status, type, documentNumber, joinedIntoDocumentId}]`, pages within the attachment; a part a join cancelled names the document it was joined into. What the review screen's Join parts dialog offers. `404` for a document not visible in the scope. |
| `GET /review-tasks` | Review | Open and InProgress by default (`status=` to change), `assignedTo=me`, oldest first, each with its `DocumentSummary`. `ReviewTaskDto` carries `tenantId` and `tenantName` like the summary (same join), for the admin console's global queue. |
| `POST /review-tasks/{id}:claim` / `:release` | Review | Claim is a conditional update of an unassigned open task: exactly one of simultaneous claimants wins, the others get `409 Task already claimed` naming the holder; re-claiming one's own task is idempotent. Release by the assignee or a tenant admin undoes exactly the assignment the caller saw (`409 Task changed` when it was claimed, released or closed in the meantime). |

## Web implementation

`web/` is one pnpm workspace (Node 22, Vite 8, React 19, TypeScript strict): `packages/api-client` (generated
`openapi-typescript` schema + `openapi-fetch` client, the contract), `packages/ui` (shadcn/ui components, Tailwind v4
tokens, dark mode via `next-themes`), `packages/app-shell` (everything both apps share above the screen level),
`packages/review` (the review form generated from a document type's compiled form spec, `DefinitionForm` with
`RepeatingGroupGrid` and `FieldInput`, and the document type editor `DocumentTypesPanel`, docs/11 §12.2 and §19; its
strings are the `review` namespace, which both apps register, and its `styles.css`, which the apps import in place of the shell's, adds its sources to the Tailwind scan), and the two apps `apps/customer-portal` and
`apps/admin-console`, which only add screens. `web/e2e` holds the Playwright suite.

### `@docproc/app-shell`

| Area | What it gives a screen |
|---|---|
| Auth | `AppProviders` → `AuthProvider` with two modes from `VITE_AUTH_MODE`. **`dev`** (the apps' `.env.development`): `SignInPage` lists `GET /dev/users` as cards and `POST /dev/token` on click; the token lives in `sessionStorage` with its expiry (`createTokenStore`, 30 s safety skew) and sign-out clears it. **`oidc`** (`.env.production`): `react-oidc-context` + `oidc-client-ts` against `VITE_OIDC_AUTHORITY` / `VITE_OIDC_CLIENT_ID`, authorization code + PKCE redirect flow, silent renew, `code`/`state` stripped from the URL after the callback; sign-out redirects to the authority's end-session endpoint with the ID token as `id_token_hint` (`signOutOidc`), so Keycloak ends its session without a "Do you want to log out?" page and the next sign-in asks for the password again, and it only clears the local session when there is no end-session endpoint. `useAuth()` exposes `{ user: { name, displayName, email, roles, permissions, tenantId, scope }, accessToken, status, isAuthenticated, signIn, signOut, hasRole, hasPolicy }`; `user` is `GET /api/v1/me` after sign-in. `hasPolicy` prefers the API's `permissions` and otherwise mirrors `Policies.RolesByPolicy` (`rolesByPolicy` in `auth/policies.ts`). |
| Routing | TanStack Router, file-based: `src/routes/__root.tsx` renders `AppProviders` + `AppShell`, every other file under `src/routes` is a screen; `@tanstack/router-plugin` writes `src/routeTree.gen.ts` (committed, regenerated on `dev`/`build`; on a merge conflict delete it and run `pnpm build`). `AppShell` is the gate: anonymous → `/sign-in?redirect=…` (same-origin paths only), signed in on `/sign-in` → back, otherwise `AppLayout` around the `Outlet`. The apps register their router type (`declare module … Register`), so screens get typed `Link`/`navigate`; the shell navigates by `href`. `router.tsx` sets `LoadingScreen`, `NotFoundPage` and `RouteErrorPage` as the defaults. |
| API + server state | `ApiProvider` creates the one `@docproc/api-client` with `getAccessToken` from the session and a `QueryClientProvider`. `useApi().client.GET("/api/v1/documents", { params: { query } })` is fully typed from `schema.d.ts`; wrap every call in `unwrap(...)` so a problem-details error becomes a thrown `ApiError` (`status`, `title`, `fieldErrors`, the latter in the UI language wherever the API sent `errorCodes`). The global `onError` toasts (sonner): 409 shows the problem title and refetches active queries (concurrent edit, invalid transition, claimed task), 400 shows the first field error, 403 the title, 401 is handled by sign-out, 404 is left to the page; `meta: { silent: true }` on a query/mutation opts out. Defaults: `staleTime` 30 s, no refetch on focus, retries only for 5xx/network. `queryKeys.<resource>.list(query) / pages(filters) / detail(id) / all` is the key factory: `list` is one plain page (a lookup, a count) and keeps `limit` in the key, `pages` is the entry of a `useCursorList` infinite list and holds only the filters (`cursor`/`limit` are paging inside the entry), so the two never share a cache entry; both sit under `all`. Rendered page images are keyed `documentPages.page(id, n)`, outside `documents`, so invalidating documents after an action does not refetch them. `useCursorList({ queryKey, queryFn })` pages any `{ items, nextCursor }` list with `useInfiniteQuery`. `TenantSwitcherProvider`/`TenantSwitcher` (admin header, SuperAdmin only) set `X-Tenant-Id`; changing it resets the query cache (not while signed out: sign-out clears the override and drops the cache). |
| Layout | `AppLayout`: shadcn sidebar built from the app's `src/nav.ts` (`NavItem { to, labelKey, icon, policy?, roles?, exact? }` or sections; entries the user's policies do not satisfy are hidden), header with `role="banner"` holding the optional tenant switcher and `UserMenu` (avatar, name, e-mail, roles, scope, language en/de, light/dark/system, sign out), `#main-content` for the routed screen. `ErrorBoundary`/`ErrorPage`, `NotFoundPage`, `LoadingScreen`, `PageHeader`, `EmptyState`. |
| Components | `DataTable` (TanStack Table v9 `useTable` with `dataTableFeatures`: sorting + column visibility + row expanding; `createDataTableColumnHelper<T>()` for typed columns; skeleton rows, empty state, and with `query` (the list's query) an error state with Retry in place of the empty state when the request failed; sortable headers keep the column's text as their name, with the sort action as the description; optional toolbar/footer for cursor paging; `renderSubRow` + `getRowCanExpand` add a chevron column and a full-width detail panel under a row, kept open by row id across refetches and sorts; `rowAttributes` for `data-*` hooks), `ConfirmDialog` (shadcn alert dialog, awaits `onConfirm`, stays open on failure), `StatusBadge` / `ReviewTaskStatusBadge` / `TenantStatusBadge` (red `destructive` only for Failed; a state waiting for a person, Needs review or an open task, uses the amber `warning` variant of `@docproc/ui`), `FieldStatusBadge` / `ConfidenceBadge` / `SourceBadge`. |
| Utilities | `formatMoney(amount, currency, locale)`, `formatDate`, `formatDateTime`, `relativeTime`, `formatPercent`, `formatBytes`, `initials` (locale defaults to the i18n language; `number \| string` accepted because the generated schema types doubles that way); field helpers `parsePath` / `getByPath` / `setByPath` (immutable) / `rowOf` (the row of any repeating group; `lineItemOf` for `lines`) over canonical paths such as `lines[2].quantity`; `FieldValue` types with `isLowConfidence(field, threshold)`, `hasValue` and the badge-variant maps. |
| i18n | i18next + react-i18next + browser language detector (`localStorage` key `docproc.lang`, then `navigator`), en and de, namespace `common` from the shell; each app calls `createI18n({ resources: { en: { portal }, de: { portal } }, defaultNS: "portal" })` from `src/i18n/index.ts`. Every visible string goes through `t()`; shell components address `common:` explicitly, screens read their app namespace by default. `createI18n` also installs zod's global error map (`i18n/zodErrors.ts`): a schema check without a message of its own reads `common:validation.*` in the current language, so zod's English defaults never reach the screen. |

### Conventions for screens

- One file per route under `src/routes`; export `Route = createFileRoute("/path")({ component })`. Append the nav
  entry in `src/nav.ts` and the label under `nav` in `src/i18n/<lng>/<app>.json`.
- Server state only through TanStack Query with `queryKeys`; after a mutation invalidate the resource's `all` key
  (or the `detail(id)` key when the list cannot change). Never hand-write `fetch`: the typed client plus `unwrap`.
- Forms: react-hook-form + zod with the shadcn `Form` primitives; map an `ApiError.fieldErrors` entry back onto the
  form field whose name is the API's path. Validation messages are translated where the schema is built: a specific
  message is a lazy i18next lookup (`.min(1, { error: () => i18n.t("…") })`, the admin console's `translated(key)`),
  anything else falls to the shell's error map. `FormMessage` renders its children when given, else the field error's
  `message`, so `<FormMessage />` is the normal case.
- Components come from `@docproc/ui` (add missing shadcn ones with `pnpm dlx shadcn@latest add <name>` inside
  `packages/ui`, fix the generated `cn` import to `@/lib/utils`, export from `src/index.ts`). The `@/` alias inside that
  package is resolved for consumers by `docprocAlias()` (`packages/ui/vite-plugin.ts`, imported as `@docproc/ui/vite`)
  in each app's `vite.config.ts` and the second `paths` entry in its `tsconfig.json`.
- Tests: vitest (jsdom) next to the code for anything with logic; Playwright in `web/e2e` for flows against the
  dev-mode API (`E2E_API_URL`, `E2E_PORTAL_URL`, `E2E_ADMIN_URL`; `tests/sign-in.spec.ts` is the smoke test and skips
  with the reason when the servers are not there).
