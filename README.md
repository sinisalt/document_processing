# document_processing

Automated system to convert supplier documents (invoices, credit notes, purchase orders,
delivery notes) arriving as PDFs, images, text or e-invoice XML into a standardised dataset,
with human review where the data could not be read, for restaurant customers.

## Plan and design

- [`docs/00-plan.md`](docs/00-plan.md) – start here: problem, the answer to "should we automate
  known formats", research findings, next steps
- [`docs/01-architecture-and-stack.md`](docs/01-architecture-and-stack.md)
- [`docs/02-data-model.md`](docs/02-data-model.md)
- [`docs/03-pipeline.md`](docs/03-pipeline.md)
- [`docs/04-known-format-automation.md`](docs/04-known-format-automation.md)
- [`docs/05-api-and-ui.md`](docs/05-api-and-ui.md)
- [`docs/06-llm-integration.md`](docs/06-llm-integration.md)
- [`docs/07-roadmap.md`](docs/07-roadmap.md) – phases and what is done
- [`docs/08-llm-providers-and-byok.md`](docs/08-llm-providers-and-byok.md)
- [`docs/09-local-development.md`](docs/09-local-development.md)

## Building and testing

Requirements:

- the .NET 10 SDK (`global.json` pins the feature band);
- a TrueType font for the PDF fixture tests: macOS and Windows ship Arial, on Debian/Ubuntu run
  `sudo apt-get install -y fonts-dejavu-core` (without it every PDF fixture test fails, docs/09
  "PDF fixtures");
- Node 22 and pnpm 10 (`corepack enable` picks the version `web/package.json` pins) for the web
  workspace, see "Web apps and end-to-end tests" below;
- Docker only for the integration tests, which skip themselves when no Docker daemon answers.

```bash
dotnet build DocProc.slnx
```

```bash
dotnet test DocProc.slnx
```

Tests use xunit v3 on the Microsoft Testing Platform (opted in via `global.json`). The schemas
sent to LLM providers are snapshotted under `schemas/`; after an intentional contract change,
bump the version in `CanonicalSchemas` and rerun with `UPDATE_SCHEMAS=1`.

### Database

The schema lives in EF Core migrations under `src/DocProc.Infrastructure/Persistence/Migrations` and
targets SQL Server 2025 / Azure SQL (compatibility level 170, native `json` columns, temporal
tables on `Documents` and `ExtractorProfiles`, row-level security keyed on `SESSION_CONTEXT`).
`dotnet ef` is a local tool pinned in `.config/dotnet-tools.json`.

Add a migration after changing the entity model (a unit test fails until you do):

```bash
dotnet tool restore
```

```bash
dotnet ef migrations add <Name> --project src/DocProc.Infrastructure --startup-project src/DocProc.Infrastructure --output-dir Persistence/Migrations
```

Produce an idempotent SQL script for a DBA or a release pipeline:

```bash
dotnet ef migrations script --idempotent --project src/DocProc.Infrastructure --startup-project src/DocProc.Infrastructure --output artifacts/docproc.sql
```

The API applies pending migrations on start-up when `ConnectionStrings:DocProc` is configured
and `Persistence:MigrateOnStartup` is true (its default outside production). The worker only
connects: it migrates only when `Persistence:MigrateOnStartup=true` is set for it explicitly.

### Running everything locally

The Aspire app host starts every dependency as a container plus the API, the worker and the
Aspire dashboard (traces, logs, metrics). It needs Docker Desktop or OrbStack; on Apple Silicon
enable Rosetta, because SQL Server and the Service Bus emulator are amd64-only images.

```bash
dotnet run --project src/DocProc.AppHost
```

What comes up (resource name → what the hosts read it as):

| Resource | Local implementation | Notes |
|---|---|---|
| `sql` / `DocProc` | `mcr.microsoft.com/mssql/server:2025-latest` | `ConnectionStrings:DocProc`; the API applies migrations on start |
| `storage` / `blobs` | Azurite | containers `raw`, `derived`, `exports` |
| `messaging` | Azure Service Bus emulator | one queue per pipeline stage, topic `document-events` |
| `keycloak` | Keycloak with realm `docproc` imported from `src/DocProc.AppHost/realms` | users `superadmin`, `operator`, `tenant-a-admin`, `tenant-a-reviewer`, `tenant-b-admin`, `tenant-b-viewer`; password = username |
| `vault` | Lowkey Vault (Key Vault emulator) on https://localhost:8443 | vault name `docproc`, for the BYOK secret flow |
| `mail` | GreenMail SMTP 3025 / IMAP 3143 | mailboxes `invoices@tenant-a.local`, `invoices@tenant-b.local`, auth disabled |
| `api`, `worker` | the two hosts | the worker waits for the API so migrations run once |

Stateful containers keep named volumes (`docproc-*`) and survive restarts of the app host. The SQL
password is an Aspire parameter; set it once with `dotnet user-secrets set Parameters:sql-password
<value> --project src/DocProc.AppHost` or let Aspire generate it. `aspire publish` can emit a
docker-compose file from the same definition for people who prefer compose.

Run the API on its own (the Development profile creates a SQLite database and signs its own
tokens, see "Running without Docker" below; with no `Persistence:Provider` and no connection string
the API starts without a database and readiness has nothing to check):

```bash
dotnet run --project src/DocProc.Api
```

The interactive API reference (Scalar) is at `/scalar`, the OpenAPI document at
`/openapi/v1.json`, liveness at `/alive`, health at `/health`, readiness at `/health/ready`.

### Running without Docker

For a machine without Docker or Keycloak the API has a self-contained development mode, which
is what `appsettings.Development.json` selects, so this works on a clean checkout:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/DocProc.Api
```

It stores data in a SQLite file (`Persistence:Provider=Sqlite`, `Persistence:SqlitePath`,
default `./data/docproc.db`, schema created from the model), seeds the two demo tenants, their
suppliers and the platform LLM provider, and signs its own bearer tokens for the users of the
Keycloak realm (`Authentication:Dev:Enabled=true`; the OIDC authority is ignored while on):

```bash
TOKEN=$(curl -s -X POST http://localhost:5100/dev/token -H 'content-type: application/json' -d '{"username":"tenant-a-admin"}' | jq -r .access_token)
curl -s http://localhost:5100/api/v1/me -H "Authorization: Bearer $TOKEN" | jq
```

`GET /dev/users` lists the six users. Both endpoints exist only while dev auth is enabled, and
the host refuses to start with either setting in Production. The AppHost switches both off
(SQL Server and Keycloak) for the full stack. Details and limits: docs/09 "Running without
Docker"; every setting the hosts read: docs/09 "Settings reference".

#### Web apps and end-to-end tests

The customer portal and the admin console (`web/apps`) are Vite dev servers in front of that API:
they proxy `/api` and `/dev` to `VITE_API_PROXY` (default `http://localhost:5100`, the API's launch
profile) and sign in through `/dev/token` on their `/sign-in` page. The AppHost does not start them.
From `web/`:

```bash
pnpm install --frozen-lockfile
```

```bash
pnpm dev:portal   # customer portal on http://localhost:5173
pnpm dev:admin    # admin console on http://localhost:5174
```

The ports are fixed (`--strictPort`) because the Keycloak realm's redirect URIs name them. To run
against an API on another port, or to run a second copy, start Vite yourself, for example
`cd apps/customer-portal && VITE_API_PROXY=http://127.0.0.1:5300 pnpm exec vite --port 5400 --strictPort`.
`pnpm typecheck`, `pnpm test` (vitest) and `pnpm build` are what CI runs.

The Playwright suite in `web/e2e` drives both apps against the dev-mode API. Every test skips with
the reason unless `E2E_API_URL` is set and the servers answer, so run the API and both dev servers
first, then:

```bash
cd web/e2e && pnpm exec playwright install chromium   # once
E2E_API_URL=http://127.0.0.1:5100 E2E_PORTAL_URL=http://127.0.0.1:5173 E2E_ADMIN_URL=http://127.0.0.1:5174 pnpm e2e
```

`PLAYWRIGHT_CHROMIUM_PATH` points Playwright at an installed Chromium instead of its download. The
tests create their own tenants and uploads, so they can run repeatedly against one database; delete
`src/DocProc.Api/data/` to start from the seed again.

### Authentication

Every endpoint except the probes, the OpenAPI document, Scalar and `/api/v1/version` needs an
OIDC bearer token (`Authentication:Oidc:Authority` and `Audience`, wired to the local Keycloak
realm by the app host). Roles come from the `roles` claim, the tenant from `tenant_id`:

| Role | Scope | Policies satisfied |
|---|---|---|
| SuperAdmin | platform, or one tenant via `X-Tenant-Id` | PlatformAdmin, Platform, TenantAdmin, Review, Ingest, Read |
| Operator | platform | Platform, Review, Read |
| TenantAdmin | own tenant | TenantAdmin, Review, Ingest, Read |
| Reviewer | own tenant | Review, Ingest, Read |
| Viewer | own tenant | Read |
| API key (`ingest`, `read`) | own tenant | Ingest and/or Read |

Machine clients use tenant **API keys** instead of OIDC: `X-Api-Key: dpk_…` (or
`Authorization: ApiKey dpk_…`). A key has the scopes `ingest` and/or `read`, authenticates as its
tenant, satisfies only the Ingest and Read policies, and cannot use `X-Tenant-Id`. Tenant admins
manage keys at `/api/v1/api-keys` (create returns the secret once; the database keeps a SHA-256
hash and the display prefix; revoke is idempotent). Keys of suspended tenants stop working.

`GET /api/v1/me` returns the caller's identity, roles or scopes, resolved tenant scope and the
policies they satisfy, which the front ends use to show or hide actions. Get a local token from
Keycloak:

```bash
curl -s -X POST http://localhost:8080/realms/docproc/protocol/openid-connect/token -d client_id=docproc-portal -d grant_type=password -d username=tenant-a-reviewer -d password=tenant-a-reviewer -d scope=openid | jq -r .access_token
```

## Repository layout

```
src/
  DocProc.Domain/             canonical model, flags, state machine, validation rules (no dependencies)
  DocProc.Application/        pipeline contracts and the interfaces infrastructure implements
  DocProc.Infrastructure/     EF Core model, migrations, tenancy (query filters + SQL Server RLS), SQLite dev mode,
                              blob stores, PDF processing, keyword classifier, heuristic splitter, secret store
  DocProc.Llm/                provider-neutral LLM gateway, prompt bundles, portable JSON schemas, replay provider,
                              the LLM classifier, splitter and invoice extractor
  DocProc.Llm.Anthropic/      Anthropic API and Claude on Foundry adapter
  DocProc.Llm.OpenAi/         OpenAI, Azure OpenAI and OpenAI-compatible adapter
  DocProc.Extraction.Profiles/ deterministic extractor-profile engine (Phase 4, empty so far)
  DocProc.Extraction.EInvoice/ UBL 2.1 and CII (Factur-X / ZUGFeRD / XRechnung) parsers to the canonical model
  DocProc.Pipeline/           pipeline runner, in-process queue, the handlers of stages 1-7 and 9, demo data
  DocProc.Api/                ASP.NET Core host: /api/v1 endpoints, auth, dev mode, OpenAPI + Scalar
  DocProc.Worker/             pipeline worker host
  DocProc.ServiceDefaults/    OpenTelemetry, health checks, service discovery, HTTP resilience
  DocProc.AppHost/            Aspire local orchestration and the seeded Keycloak realm
tests/
  DocProc.Domain.Tests/       validation rules, state machine, value types
  DocProc.Llm.Tests/          schema portability and snapshots, config resolution, gateway, both adapters, LLM stages
  DocProc.EInvoice.Tests/     UBL / CII / XRechnung mapping, detection, resource limits
  DocProc.Pipeline.Tests/     stage handlers, runner, retries, duplicate handling, demo data and its replay fixtures
  DocProc.Api.Tests/          in-process API tests (minted tokens, in-memory SQLite): endpoints, auth, roles, tenant
                              scope, API keys, dev mode; the documentation checks under Docs/
  DocProc.Worker.Tests/       the worker host as Program.cs composes it
  DocProc.AppHost.Tests/      app-model checks (resources, waits, images, realm) without starting containers
  DocProc.Integration.Tests/  PDF, classifier and splitter fixtures; Testcontainers (SQL Server 2025, Azurite) for
                              migrations, RLS, temporal tables and the Azure blob store, skipped without Docker
web/                          pnpm workspace: apps/customer-portal, apps/admin-console, packages/ui, app-shell,
                              api-client (generated from the OpenAPI document), e2e (Playwright)
fixtures/                     demo documents and their LLM replay fixtures (docs/09 "Demo data")
schemas/                      committed JSON schemas sent to providers
docs/                         plan and design
```
