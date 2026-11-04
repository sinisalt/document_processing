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
- [`docs/10-screenshots.md`](docs/10-screenshots.md) – every portal and admin screen, captured
  against the dev-mode API
- [`docs/11-document-types-and-mappings.md`](docs/11-document-types-and-mappings.md) – the accepted design for
  document types as tenant data, supplier layouts and identification rules, source routing, manual mapping in review
  and output mappings to existing systems, with the phased delivery plan

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
bump the version in `CanonicalSchemas` and rerun with `UPDATE_SCHEMAS=1`. The same switch rewrites
the meta-schema of document type definitions (`schemas/document-type-definition.v1.json`) and the
compiled schemas and form specs of the standard templates (`schemas/templates/`, which the web package
`packages/review` renders in its tests); a published template version is immutable, so a changed template
ships as a new version (docs/11 §5.7).

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
| `mail` | GreenMail SMTP 3025 / IMAP 3143 | mailboxes `invoices@tenant-a.local`, `invoices@tenant-b.local`, auth disabled; the worker polls the `Mailbox` channels of tenants with `sourceRouting` on (docs/09 "Source routing") |
| `mock-erp` | WireMock on http://localhost:8086 with the stubs in `docker/mock-erp` | the receiving ERP of the delivery demos (docs/11 §23): `POST /api/ap/invoices`, `POST /api/article-prices`, a 503 on demand; nothing sends to it before P3 (docs/09 "Mock ERP") |
| `api`, `worker` | the two hosts | the worker waits for the API so migrations run once |

Stateful containers keep named volumes (`docproc-*`) and survive restarts of the app host. The SQL
password is an Aspire parameter; set it once with `dotnet user-secrets set Parameters:sql-password
<value> --project src/DocProc.AppHost` or let Aspire generate it. For the same stack without the
.NET SDK, including both web apps, use Docker Compose (next section).

The Keycloak realm is imported on the first start only. If your `docproc-keycloak-data` volume is
older than the realm's `basic`, `profile` and `email` client scopes, the apps' sign-in fails with
`invalid_scope`: delete the volume (`docker volume rm docproc-keycloak-data`) and start again.

#### With Docker Compose

`docker-compose.yml` at the repository root starts the same services as the app host, plus the
customer portal and the admin console as production builds behind nginx. From a clean clone it
needs only Docker (Docker Desktop, OrbStack or Docker Engine with the compose plugin):

```bash
docker compose up --build        # or: docker compose up --build -d
```

The first build takes a few minutes (restore, publish, two `vite build`s); SQL Server and the
Service Bus emulator then need about half a minute. With `-d`, `docker compose ps -a` shows the
other services healthy; `worker` and `servicebus` are only `Up`, because they have no health check
of their own (the worker has no HTTP endpoint; the emulator's image has no shell, so the one-shot
`servicebus-ready` checks it from outside), and `servicebus-ready` and `llm-setup` are one-shot
helpers that exit 0.

SQL Server can exit on its first boot when the machine is busy (`docker compose logs sqlserver`
shows `Failed to load LSA: 0xc0070102`, a start-up timeout). Every long-running service has
`restart: unless-stopped`, so it is started again and `up` carries on once it is healthy. If `up`
still stops with `dependency sqlserver failed to start` (for example `is unhealthy`, after five
minutes of failed health checks on a very slow machine), read `docker compose logs sqlserver` and
run `docker compose up -d` again; the second start keeps what the first created. The hosts' logs
(`docker compose logs api`) leave out EF Core's SQL statements; docs/09 "With Docker Compose" says
how to turn them back on.

The compose stack and the app host use the same fixed host ports (1433, 8080, 8443, 3025, 3143,
8085, 8086), so run one at a time: stop `dotnet run` before `docker compose up`, and run
`docker compose stop` (or `down`) before going back to `dotnet run`. The app host's containers
(`sql-*`, `keycloak-*`, `vault-*`, `mail-*`, `storage-*`, `messaging-*`) have a persistent lifetime
and keep running after `dotnet run` stops, but they do not block compose: they publish random host
ports, and the fixed ports are served by the app host's proxy, which stops with it. If `up` still
fails with `Bind for 127.0.0.1:8080 failed: port is already allocated` (or another of those
ports), `docker ps --format '{{.Names}}\t{{.Ports}}'` shows which container holds it, for example a
second compose project (`-p`). `dotnet test` starts none of them: the AppHost checks build its
application model and stop the app host before it starts anything (`tests/DocProc.AppHost.Tests/AppHostModel.cs`).

If sign-in fails with "Failed to fetch", and the browser console shows a CORS error on
`http://localhost:8080/realms/docproc/.well-known/openid-configuration`, another server (a local
Apache, another project's container) answers on 8080 instead of Keycloak; opening that URL shows
whose page it is. Stop that server, or move Keycloak: put `DOCPROC_KEYCLOAK_PORT=8180` in `.env`
and run `docker compose up --build -d` (the apps are built with Keycloak's URL).

| URL | What |
|---|---|
| http://localhost:5173 | customer portal; sign in as `tenant-a-admin`, `tenant-a-reviewer`, `tenant-b-admin` or `tenant-b-viewer` |
| http://localhost:5174 | admin console; sign in as `superadmin` or `operator` |
| http://localhost:5100/scalar | API reference (Scalar); `/health/ready`, `/openapi/v1.json` |
| http://localhost:8080 | Keycloak (realm `docproc`; admin console `admin` / `admin`); `DOCPROC_KEYCLOAK_PORT` in `.env` moves it |
| http://localhost:8085 | GreenMail API; SMTP `localhost:3025`, IMAP `localhost:3143` |
| http://localhost:8086 | mock ERP (WireMock): `/__admin/requests` lists what it received; `POST /__control/erp-ap/fail-next` makes the next call answer 503 (docs/09 "Mock ERP") |

Every password is the user name. The API applies the migrations, seeds the realm's two tenants
(suppliers, upload channels, members) and ingests the demo documents of `fixtures/demo`, so the
portal has something to show; `DOCPROC_DEMO_DOCUMENTS=false` skips the documents. Published ports
are bound to `127.0.0.1`. The passwords in the file (SQL `sa`, Keycloak admin) are local defaults
for a developer machine, not secrets; override them in `.env` (see `.env.example`) before the first
start, since SQL Server keeps the password it was created with.

**LLM keys.** Without a key the stack still processes e-invoice XML (UBL, CII, XRechnung) end to
end, and the demo PDFs are answered from their replay fixtures (so are the demo PDF invoice after the
custom-field steps of docs/09 "Document types", the credit note of `fixtures/demo/uploads` in the P1 demo,
docs/09 "Demo: types as data", and the split and classification of the Nordwind price list in the P2 demo, docs/09
"Demo: manual mapping"); any other PDF ends `Failed` until a
provider is configured. To use a real provider, copy `.env.example` to `.env` (git-ignored) and set
`ANTHROPIC_API_KEY`, or `OPENAI_API_KEY` together with `OPENAI_MODEL`, then `docker compose up -d`.
The one-shot `llm-setup` service makes the platform configurations follow `.env` through the admin
API on every `up`: a key that is set is enabled (or created), a changed OpenAI key replaces the
previous one, and removing `ANTHROPIC_API_KEY` disables the Anthropic configuration again.
`docker compose logs llm-setup` says what it did and ends with the configuration in use (the API's
own start-up log is written before `llm-setup` runs, so it still names the one from before). Or add
a provider in the admin console under AI providers. Details: docs/09 "With Docker Compose".

**Demos.** The phase demos of docs/11 §23 run on this stack: the P0 demo (a decision whose publish message is
lost) and the P1 demo, types as data (switch the catalogue on, adopt the price list, fork the credit note with a
custom field, upload a credit note that carries it, review it in the generated form, find it in the inbox, and give
the demo delivery note fields of its own) and the P2 demo, manual mapping (upload a 14-page price list the system
does not recognise, give it its type in review, take its header values from the page in map mode, draw its table once
on page 1 and apply it to all pages: 412 rows, none rejected, then approve) and the P3 demo, outputs and delivery
(switch outputs on, build the article-price-import mapping of that price list in Settings > Integrations with the
visual field map, preview it on the server, bind it to price lists with a destination at the mock ERP, approve, watch a
forced 503 retried and delivered in the document's Deliveries tab, while invoices keep going to erp-ap), step by step
in docs/09 "Demo data", "Demo: types as data", "Demo: manual mapping" and "Demo: outputs and delivery".

**Reset.** `docker compose down` keeps the data (SQL, blobs, Keycloak, secrets live in named
volumes); `docker compose down -v` deletes the volumes, and the next `up` starts from the seed.

**Apple Silicon.** SQL Server and the Service Bus emulator are amd64-only images, pinned to
`linux/amd64` in the compose file: enable "Use Rosetta for x86_64/amd64 emulation" in Docker
Desktop (or Rosetta in OrbStack) and give Docker at least 8 GB. Everything else, including the
images built from this repository, runs natively.

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

`pnpm e2e:screenshots` runs only the `@screenshot` tests. They write every portal and admin screen
to `web/e2e/screenshots/`, and [`docs/10-screenshots.md`](docs/10-screenshots.md) shows them with a
caption each. Start the API on a fresh seed first, so the images show only the demo data.

### Authentication

Every endpoint except the probes, the OpenAPI document, Scalar and `/api/v1/version` needs an
OIDC bearer token (`Authentication:Oidc:Authority` and `Audience`, wired to the local Keycloak
realm by the app host). Roles come from the `roles` claim, the tenant from `tenant_id`:

| Role | Scope | Policies satisfied |
|---|---|---|
| SuperAdmin | platform, or one tenant via `X-Tenant-Id` | PlatformAdmin, Platform, TenantAdmin, ConfigAdmin, IntegrationAdmin, DeliveriesRead, Review, Ingest, Read |
| Operator | platform | Platform, Review, Read |
| TenantAdmin | own tenant | TenantAdmin, ConfigAdmin, IntegrationAdmin, DeliveriesRead, Review, Ingest, Read |
| Reviewer | own tenant | Review, Ingest, Read |
| Viewer | own tenant | Read |
| API key (`ingest`, `read`, `deliveries:read`) | own tenant | Ingest, Read and/or DeliveriesRead |

Machine clients use tenant **API keys** instead of OIDC: `X-Api-Key: dpk_…` (or
`Authorization: ApiKey dpk_…`). A key has the scopes `ingest`, `read` and/or `deliveries:read` (a pull
destination's feed, docs/11 §13.3; a key created with `destinationId` reads that destination's feed only),
authenticates as its tenant, satisfies only the Ingest, Read and DeliveriesRead policies, and cannot use
`X-Tenant-Id`. Tenant admins
manage keys at `/api/v1/api-keys` (create returns the secret once; the database keeps a SHA-256
hash and the display prefix; revoke is idempotent). Keys of suspended tenants stop working.

`GET /api/v1/me` returns the caller's identity, roles or scopes, resolved tenant scope and the
policies they satisfy, which the front ends use to show or hide actions. ConfigAdmin (document
types and lookup tables, docs/11 §16), IntegrationAdmin (destinations, output mappings, bindings and redelivery)
and DeliveriesRead (the pull feeds) are policies, not roles: TenantAdmin and SuperAdmin satisfy them. Get a local token from
Keycloak:

```bash
curl -s -X POST http://localhost:8080/realms/docproc/protocol/openid-connect/token -d client_id=docproc-portal -d grant_type=password -d username=tenant-a-reviewer -d password=tenant-a-reviewer -d scope=openid | jq -r .access_token
```

## Repository layout

```
src/
  DocProc.Domain/             canonical model, flags, state machine, validation rules (no dependencies)
  DocProc.Definitions/        document type definitions as data (docs/11 §5): the format, its meta-schema and checks,
                              the compiler and publish checks, and the embedded standard templates (std.invoice,
                              std.credit-note, ..., std.price-list, std.daily-specials)
  DocProc.Application/        pipeline contracts and the interfaces infrastructure implements
  DocProc.Infrastructure/     EF Core model, migrations, tenancy (query filters + SQL Server RLS), SQLite dev mode,
                              blob stores, PDF processing, keyword classifier, heuristic splitter, secret store
  DocProc.Llm/                provider-neutral LLM gateway, prompt bundles, portable JSON schemas, replay provider,
                              the LLM classifier, splitter and invoice extractor
  DocProc.Llm.Anthropic/      Anthropic API and Claude on Foundry adapter
  DocProc.Llm.OpenAi/         OpenAI, Azure OpenAI and OpenAI-compatible adapter
  DocProc.Mapping/            mapping engine over the positioned text layer (docs/11 §8, §12.4): the table tool's
                              column-table, value readers by kind and locale; identification rules, the layout
                              resolver and suggester, and the layouts' field mapping engine (P4)
  DocProc.Outputs.Renderer/   the sandboxed JSONata renderer of output mappings (docs/11 §13.1): a child process the
                              API and the worker start and kill (time, memory, output and depth limits, no I/O)
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
  DocProc.Mapping.Tests/      the table engine, value readers, identification rules, resolver and layout engine on synthetic word layouts
  DocProc.Api.Tests/          in-process API tests (minted tokens, in-memory SQLite): endpoints, auth, roles, tenant
                              scope, API keys, dev mode; the documentation checks under Docs/
  DocProc.Worker.Tests/       the worker host as Program.cs composes it
  DocProc.AppHost.Tests/      app-model checks (resources, waits, images, realm) without starting containers, and
                              docker-compose.yml and the Service Bus emulator config held to the app model
  DocProc.Integration.Tests/  PDF, classifier and splitter fixtures; Testcontainers (SQL Server 2025, Azurite) for
                              migrations, RLS, temporal tables and the Azure blob store, skipped without Docker
web/                          pnpm workspace: apps/customer-portal (with Settings > Integrations and the Deliveries tab, docs/11 §19), apps/admin-console, packages/ui, app-shell,
                              review (the generated review form and the document type editor, docs/11 §12.2, §19),
                              api-client (generated from the OpenAPI document), e2e (Playwright)
fixtures/                     demo documents, the demo custom fields and uploads, and their LLM replay fixtures (docs/09 "Demo data", "Demo: types as data", "Demo: manual mapping"), the Nordwind invoices in two designs (docs/09 "Supplier layouts"); outputs/ the compiled visual map of the article-price-import mapping ("Demo: outputs and delivery"); dropzone/ the watched-folder channels' drop zone, one folder per tenant slug ("Source routing")
schemas/                      committed JSON schemas sent to providers, the definition meta-schema and the compiled
                              template schemas (templates/)
docs/                         plan and design
docker/                       what docker-compose.yml mounts: the Service Bus emulator's entities, the llm-setup script (and its node tests)
docker-compose.yml            the whole stack with Docker Compose (images: src/Dockerfile, web/Dockerfile; .env.example)
```
