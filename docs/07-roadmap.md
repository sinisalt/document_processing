# Roadmap

Phases are ordered so that each one ships something a customer can use, and so that the
measurement needed for the next decision (which model, whether profiles pay off) exists
before the decision is made. Durations assume one to two backend developers and one
front-end developer; adjust to the team.

## Phase 0 – Foundations (1–2 weeks) – in progress

Done so far (see the README for how to build and test):

- Solution skeleton with central package management, analyzers as errors, CI workflow.
- `DocProc.Domain`: canonical model with field provenance, flag catalogue, document state machine,
  text-layer types and the stage-7 validation rules, all unit-tested.
- `DocProc.Llm`: provider interface, gateway with config resolution, spend cap, usage and cost
  recording, request hashing, replay provider, extraction contracts and the portable JSON schemas
  (generated, checked against both vendors' rules, snapshotted under `schemas/`).
- `DocProc.Llm.Anthropic`: Messages API adapter (native PDF, cached prefix, structured output,
  effort mapping, error normalisation); request building and error mapping are unit-tested, the
  live call is not yet exercised.
- `DocProc.Application`: the interfaces infrastructure implements and the pipeline messages.
- `DocProc.Infrastructure`: EF Core model for tenancy, channels, suppliers and the global
  registry, extractor profiles, ingestion, documents (canonical JSON plus denormalised columns,
  flags, events, review tasks, corrections) and LLM configuration/usage; the `InitialCreate`
  migration with temporal tables and the row-level security policy; tenant query filters and
  the SESSION_CONTEXT interceptor. Model checks run without a database; the migration, RLS and
  temporal behaviour are exercised against the SQL Server 2025 container in CI.
- `DocProc.Api` / `DocProc.Worker`: hosts with health, readiness, OpenAPI and a Scalar API
  reference; no business endpoints yet. `DocProc.ServiceDefaults` wires OpenTelemetry, health
  checks, service discovery and HTTP resilience into both.
- `DocProc.AppHost`: Aspire orchestration with SQL Server 2025, Azurite, the Service Bus emulator
  (queue per stage), Keycloak with a seeded realm (platform roles, two tenants), Lowkey Vault and
  GreenMail; model-level tests pin names, images, waits and the realm contents.

- Authentication: JWT bearer against the configured authority (Keycloak locally), the five roles
  as authorization policies, tenant resolution from the `tenant_id` claim with the SuperAdmin
  `X-Tenant-Id` override, secure-by-default fallback policy, `/api/v1/me`; in-process tests mint
  tokens with a test key.

Still open in Phase 0: API keys at the API boundary, the generated TypeScript client, Bicep, and
the three decisions below. Tenant status (suspended tenants) is not yet checked at the boundary;
that needs the tenant row and lands with the first tenant endpoints.


- Solution skeleton per `01-architecture-and-stack.md`; Aspire app host with the SQL Server
  2025 container, Service Bus emulator and Azurite; EF Core migrations; CI with build, tests
  against Testcontainers, container images; Bicep for the Azure dev environment; local
  substitutes per `09-local-development.md` (Keycloak, Lowkey Vault, GreenMail, replay LLM
  provider) wired into the AppHost from day one.
- Canonical domain model and JSON schema (versioned) from `02-data-model.md`; validation rules
  with unit tests.
- Tenancy, OIDC login, roles, API keys; OpenAPI published; generated TS client.
- LLM gateway and provider interface with the Anthropic adapter; schema-portability test.
- Decisions to close: Entra External ID vs another OIDC provider for customer users, Azure
  SQL tier (serverless for dev/test, provisioned for production), Container Apps vs AKS.

## Phase 1 – Ingest → LLM extract → review (3–5 weeks)

- Channels: API upload and watched folder first (simplest to test), then one M365 mailbox via
  Graph notifications; `.eml` retained; attachment unpacking; duplicate detection by hash.
- Normalisation: PDF text layer (PdfPig), page rasters (PDFtoImage), Tesseract OCR for scans.
- Classification (rules + Haiku), extraction with Opus 5.5 and structured outputs, validation
  and flags, review tasks; OpenAI-compatible adapter against Azure OpenAI with platform keys,
  so both provider families are exercised by the first pilot.
- Customer portal: inbox, review screen with highlights, approve/reject, corrections stored.
- Admin console: tenants, channels, document timeline, failed jobs, LLM usage.
- Exit criterion: a pilot restaurant's mailbox processed end-to-end; gold set started.

## Phase 2 – Structured e-invoices and splitting (2–3 weeks)

- Factur-X/ZUGFeRD/XRechnung (CII) and UBL parsers to the canonical model; detection in
  stage 2; these never touch OCR or the LLM.
- Multi-document splitting (heuristics + Haiku), `MULTI_DOCUMENT_SPLIT` flag, manual split
  tool in the portal.
- Purchase orders and delivery notes as document types; reference matching (invoice ↔ PO ↔
  delivery note) and the three-way match view.
- Webhooks and exports (JSON, CSV, UBL).
- **BYOK**: tenant provider configs, Key Vault flow, credential validation, per-stage model
  selection, portal settings screen; tenant policy for platform-key fallback.

## Phase 3 – Measurement and cost controls (2 weeks, overlaps Phase 2)

- Eval harness over the gold set: field accuracy, STP, cost per document for Opus/Sonnet/Haiku
  and for prompt versions; batch-mode runs.
- Prompt caching verified (cache-read tokens non-zero), supplier memory in the prompt,
  per-tenant auto-approval thresholds tuned from correction rates.
- Cost attribution by key owner, price tables, spend caps and `LLM_PROVIDER_UNAVAILABLE`
  handling; tenant usage dashboard.
- Decision point: which model per stage; whether the volume/supplier repetition justifies
  Phase 4 now or later.

## Phase 4 – Extractor profiles (4–6 weeks)

- Deterministic engine (`DocProc.Extraction.Profiles`) with the closed strategy set, snapshot
  tests on real supplier PDFs.
- Fingerprinting in stage 5; global supplier registry; cross-tenant sharing with opt-out.
- Profile synthesis job (Opus at high effort), replay verification, shadow mode, lifecycle
  (Candidate → Active → Degraded → Retired), drift alerts.
- Admin console: profile list, success-rate charts, test-against-document, activate/retire.
- Exit criterion: for the top suppliers of the pilot tenants, profile share > 60 % with
  precision not below the LLM path.

## Phase 5 – Hardening and scale (ongoing)

- Azure Document Intelligence Layout as the premium OCR provider for poor scans.
- Retention/purge jobs, legal hold, audit exports; per-tenant spend limits and alerts.
- Multi-region/self-hosted deployment option if a customer requires it.
- Additional channels (SFTP, Peppol access point inbound, portal drag-and-drop bulk upload).
- Accounting integrations (DATEV, Xero, QuickBooks, local ERPs) as webhook consumers or exporters.

## Risks and open questions

| Risk / question | Mitigation or decision needed |
|---|---|
| Scan quality from small suppliers (photos of paper delivery notes) | OCR provider interface; Azure Layout fallback; `UNREADABLE_PAGES` flag with a request-resend workflow in the portal. |
| Structured-output + citations cannot be combined | Provenance from our own text layer (see `06-llm-integration.md`). |
| LLM provider terms vs customer DPAs | Inline requests only, no Files API persistence, batch only where allowed; confirm retention tier with Anthropic for EU customers. |
| ZUGFeRD-csharp deprecation (Dec 2026) and its commercial successor | Pin last Apache release behind an interface; evaluate cost of the commercial package vs maintaining a fork when Phase 2 starts. |
| Retention periods differ by country (Germany 8 years since 2025, Croatia 11, France 10) | Retention is a tenant setting with per-country defaults; legal review before launch in each country. |
| Template drift when a supplier redesigns its invoice | Validation-gated fallback, drift metrics, automatic re-synthesis; never let a profile answer without arithmetic checks passing. |
| Provider feature drift (one vendor adds or removes a structured-output or PDF capability) | Capability descriptor per adapter, document input falls back to page images, CI schema test per provider, eval per provider before promotion. |
| A BYOK tenant's key leaks through logs or errors | Key Vault references only, redaction filter, per-job fetch with short TTL, audit log, secret scanning in CI. |
| Reviewer fatigue if thresholds are too strict | Per-field correction-rate reporting; raise auto-approval only where corrections are near zero. |
| Azure SQL cost at scale (JSON documents, page-text layers) | Keep page images and text layers in Blob Storage, not in the database; store only the canonical document, flags and indexes in SQL; use the serverless tier for dev/test. |
| SQL Server 2025 container vs Azure SQL feature drift (json, vector, RLS) | Pin the compatibility level; integration tests run against the container and a nightly run against an Azure SQL dev database. |
