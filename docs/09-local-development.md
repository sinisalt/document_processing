# Local development and testability on a Mac with Docker

Goal: a developer runs the whole platform on a Mac (Apple Silicon or Intel) with Docker
Desktop or OrbStack, runs every automated test locally, and only touches real Azure for the
handful of managed services that have no emulator. Everything Azure-only sits behind an
interface with a local implementation, so "works on my Mac" and "works in Azure" differ only
by configuration.

## Component by component

| Component | Production | Local (Docker on Mac) | Fidelity | How the real thing is tested |
|---|---|---|---|---|
| Database | Azure SQL Database | `mcr.microsoft.com/mssql/server:2025-latest`. **amd64 only**: on Apple Silicon run it with `--platform linux/amd64` and Rosetta enabled in Docker Desktop / OrbStack (without Rosetta the container can hang at initialisation). | Same engine, same T-SQL, json and vector types; slower under emulation but fine for dev and tests. | Nightly integration run against an Azure SQL dev database (serverless tier, auto-pause) to catch Azure SQL-specific differences. |
| Queue | Azure Service Bus | Official **Service Bus emulator** container (needs a SQL Server container as its store; same Rosetta note applies). Supports queues, topics and subscriptions; no partitioned entities, no JMS, no AMQP over WebSockets. | Same SDK and wire protocol as production. | Wolverine's Service Bus transport is also run in the Azure dev environment by CI. If the emulator proves troublesome on a given machine, Wolverine's transport is a configuration switch, so RabbitMQ (native arm64 image) can stand in for local runs without changing code. |
| Blob storage | Azure Blob Storage | **Azurite** (native arm64). | Same SDK; immutability policies and lifecycle rules are not emulated. | Bicep deploys the real containers with WORM policy; an integration test in the Azure dev environment asserts the policy exists. |
| Secrets | Azure Key Vault | **Lowkey Vault** container (community emulator, same SDK, HTTPS on 8443) for the BYOK flow; `.NET user-secrets` for developer keys. | Same `SecretClient` calls; no RBAC, no soft-delete semantics. | Key Vault access through managed identity is exercised in the Azure dev environment. |
| OCR | Tesseract + Azure Document Intelligence Layout | **Tesseract** runs in-process (native arm64 and amd64 binaries). Document Intelligence has **no free local emulator**: Microsoft's containers must phone home to an Azure billing endpoint, and the offline variant needs a commitment plan. | Tesseract path fully local. | `IOcrProvider` has a recorded-fixture implementation (saved Layout JSON per sample PDF) for tests, and an opt-in live test against a Document Intelligence resource on the free F0 tier (500 pages/month). |
| PDF | PdfPig, PDFtoImage (PDFium) | Both run in-process; PDFium ships osx-arm64, osx-x64, linux-arm64 and linux-x64 natives. | Identical. | n/a |
| Email (M365) | Microsoft Graph change notifications + delta | No Graph emulator. `IMailSource` has three implementations: Graph (production), **IMAP via MailKit against a local GreenMail container** (SMTP+IMAP, arm64 image) for end-to-end runs where a test sends an email with attachments, and a **fixture source** that replays `.eml` files from `tests/fixtures/mail/`. | IMAP path is a real mail flow; Graph specifics (subscription renewal, delta tokens) are not local. | Graph integration tests run against a Microsoft 365 Developer Program tenant from the Mac; webhooks need a public URL, so use `devtunnel` (Microsoft Dev Tunnels) or rely on the delta-polling fallback, which is the default locally. |
| Watched folder | SMB/SFTP/blob container | A bind-mounted local folder or an Azurite container. | Identical code path. | n/a |
| LLM providers | Anthropic API, Claude on Microsoft Foundry, Azure OpenAI, OpenAI | Real APIs over the internet (no emulator). The gateway has a **replay provider** that serves recorded responses keyed by request hash, used by all CI tests. For offline smoke tests, the OpenAI-compatible adapter can point at **Ollama / LM Studio** on the Mac. | Replay is exact for regression; live calls are opt-in (`LLM_LIVE=1`) and cost cents. | The eval harness runs live in batch mode on demand, never in the default test run. |
| Identity | Entra External ID / Entra ID | **Keycloak** container (arm64) as a local OIDC provider with seeded realms for superadmin, operator and two tenants; the API only knows OIDC authority + audience. Alternatively the test auth handler for unit tests. | Same OIDC flows; Entra-specific claims mapping is configuration. | Entra External ID dev tenant (free up to 50 000 monthly active users) used by the Azure dev environment. |
| Compute | Azure Container Apps (KEDA scaling on queue length) | **Aspire AppHost** starts API, worker, both React apps and all containers above with one `dotnet run`; `docker compose` file generated from it for people who prefer that. | Scaling behaviour is not local. | Dev environment in Azure deployed by Bicep from CI on every main merge. |
| Observability | Azure Monitor / Application Insights | Aspire dashboard (traces, logs, metrics via OpenTelemetry). | Same instrumentation, different exporter. | n/a |

## What the AppHost actually starts

`src/DocProc.AppHost/AppHost.cs` is the executable version of the table above. Resource names
double as connection-string names: `DocProc` (database), `blobs`, `messaging`, `keycloak`.
The Keycloak realm is imported from `src/DocProc.AppHost/realms/docproc-realm.json`: realm
roles `SuperAdmin`, `Operator`, `TenantAdmin`, `Reviewer`, `Viewer`, clients `docproc-api`
(bearer-only audience), `docproc-portal` and `docproc-admin` (public, PKCE), a `tenant_id`
claim from a user attribute, and six users across the platform and two tenants (see README).
Secrets for BYOK go to Lowkey Vault (`vault`, HTTPS 8443); mail flows use GreenMail (`mail`).
Tests under `tests/DocProc.AppHost.Tests` build the application model without starting it and
assert the resource set, images, waits and realm contents, so a rename is caught before anyone
runs the stack.

## Running without Docker

The API has a dependency-free development mode for machines without Docker or Keycloak
(a locked-down laptop, a CI agent that only takes screenshots, a quick demo). It is the default
of `appsettings.Development.json`, so `dotnet run --project src/DocProc.Api` works on a clean
checkout; the AppHost overrides it (`Persistence__Provider=SqlServer`,
`Authentication__Dev__Enabled=false`) so the full stack keeps using SQL Server and Keycloak.

| Setting | Development mode | What it does |
|---|---|---|
| `Persistence:Provider` | `Sqlite` (default is `SqlServer` whenever `ConnectionStrings:DocProc` is set) | `DocProcDbContext` on a SQLite file; the schema comes from the model with `EnsureCreated`, because the migrations are SQL Server-only. |
| `Persistence:SqlitePath` | `./data/docproc.db` (relative to the content root, i.e. the project folder under `dotnet run`, whatever the working directory; the folder is created) | Delete the file after a model change; there is no migration path for SQLite. |
| `Persistence:SeedDevData` | implied by `Sqlite`; set it explicitly to seed a SQL Server dev database (refused in Production, like the two other switches) | Idempotent seed with fixed ids (`DevSeed`): tenants A and B of the Keycloak realm, an API-upload channel each, five suppliers, the platform Anthropic provider (status Disabled, secret ref `llm-platform-eu`) and tenant A's LLM policy. |
| `Authentication:Dev:Enabled` | `true` | The API signs its own tokens (`POST /dev/token`, `GET /dev/users`, tagged "Development") with an RSA key and the bearer handler trusts issuer `docproc-dev` instead of the OIDC authority. The endpoints are not mapped at all when disabled. |
| `Authentication:Dev:SigningKeyPath` | `./data/dev-signing-key.pem` (relative to the content root) | Unset: a key per process, tokens die on restart. Set: loaded, or generated and written on first start (owner-only permissions on Unix), so tokens survive restarts. The key is loaded at start-up, so a corrupt file stops the host rather than the first request. |
| `Blobs:Provider` | unset: inferred. `Azure` whenever `ConnectionStrings:blobs` (injected by the AppHost) or `Blobs:ServiceUri` is present, `FileSystem` otherwise; an explicit value wins. | `FileSystem` is a content-addressed store on disk (`<root>/<container>/<tenant>/<sha256>` plus a `.meta.json` sidecar). It cannot issue short-lived read URLs (`GetReadUrlAsync` throws `NotSupportedException`), so the API streams bytes itself in this mode. |
| `Blobs:RootPath` | `./data/blobs` (relative to the content root; the folder is created at start-up) | Both hosts must point at the same root, or the worker cannot read what the API ingested; under the AppHost both use Azurite instead. |

The dev users are the realm's users, with the same roles, `tenant_id` attributes, e-mails and
names, so a front end built against the dev issuer behaves identically against Keycloak:

```bash
curl -s -X POST http://localhost:5100/dev/token -H 'content-type: application/json' -d '{"username":"tenant-a-admin"}' | jq -r .access_token
```

Limits, by design: SQLite has no row-level security and no temporal tables (the EF query
filters are the only tenancy layer, which is why the mode is refused in Production: the host
throws at start-up for `Persistence:Provider=Sqlite`, `Persistence:SeedDevData=true` or
`Authentication:Dev:Enabled=true`),
and it cannot `ORDER BY` a `DateTimeOffset` column, so lists that sort by time order in memory
(the API-key list does). Everything SQL Server-specific is exercised by the integration tests
against a container, not here.

## Apple Silicon specifics

- Enable **"Use Rosetta for x86_64/amd64 emulation"** in Docker Desktop (or Rosetta in
  OrbStack). SQL Server and the Service Bus emulator are the only amd64-only images; everything
  else is multi-arch.
- Build the .NET images multi-arch (`linux/arm64` for local, `linux/amd64` for Azure). The
  native dependencies (PDFium, Tesseract, SkiaSharp) are present for both; CI builds and
  tests the amd64 image so a missing native never reaches Azure unnoticed.
- Allocate at least 8 GB to Docker; SQL Server under emulation is memory-hungry.

## Test pyramid

| Level | Runs where | Needs | Covers |
|---|---|---|---|
| Unit | `dotnet test`, no Docker | nothing | domain model, validation rules, profile engine (snapshot tests on sample PDFs), schema portability, prompt bundles, EF model vs. migration snapshot |
| Integration | `dotnet test`, Testcontainers | Docker | EF Core against SQL Server 2025 (RLS, json, vector), Wolverine outbox, Azurite, Lowkey Vault, GreenMail IMAP, full pipeline with the replay LLM provider and Tesseract |
| End-to-end | Aspire AppHost + Playwright | Docker | send an email with two PDFs to GreenMail → documents appear in the portal → review → approve → webhook received by a local sink |
| Contract (opt-in) | developer machine or CI job with secrets | Azure dev resources, API keys | Graph delta/webhooks, Document Intelligence F0, Key Vault via managed identity, live LLM calls, Azure SQL |
| Eval (on demand) | batch job | API keys | gold set accuracy and cost per provider/model/prompt |

The default `dotnet test` run needs only Docker and finishes without any Azure account or API
key. Contract and eval runs are tagged and skipped unless their environment variables are
present.

## PDF fixtures: fonts, PDFsharp and the artifacts switch

The stage 2–4 tests (`tests/DocProc.Integration.Tests/Pdf`, `.../Classification`) generate
their PDFs at test time with **PDFsharp**; no binary fixtures live in the repository. Three
things to know:

- **A TrueType font must be installed.** PDFsharp's Core build has no platform font lookup on
  Linux, so `TestFonts` embeds the first font it finds (DejaVu Sans, Liberation Sans or
  FreeSans on Linux; Arial on macOS and Windows). Without one, every PDF fixture test fails at
  type initialisation with `FileNotFoundException: No TrueType font found`. On Debian/Ubuntu
  run `sudo apt-get install -y fonts-dejavu-core`; the CI job installs it explicitly rather
  than relying on the runner image. macOS and Windows ship Arial.
- **PDFsharp is test-only.** Version 6.5 is dual-licensed (AGPL or commercial) and refuses to
  create a document until a licence is chosen; `TestPdfs.EnsureFonts` calls
  `PdfSharpLicense.SetAgplLicense()` once per process. The package is referenced only by
  `DocProc.Integration.Tests`, which is never shipped, so the AGPL terms are met. Do not add
  PDFsharp to a `src/` project: the production PDF stack is PdfPig (Apache-2.0) and PDFtoImage
  (MIT), see docs/01. If a product feature ever needs to write PDFs, pin PDFsharp 6.2.x (MIT) or
  decide on the commercial licence first.
- **`DOCPROC_TEST_ARTIFACTS`** (optional): set it to a directory before running the tests and
  the fixtures and rendered pages are written there (`invoice-with-delivery-note.pdf`,
  `*-page1-200dpi.png`, `image-only-scan.pdf`, ...) for eyeballing. Unset, nothing is written
  and the tests stay hermetic.

## What is not testable locally, and the mitigation

| Gap | Mitigation |
|---|---|
| Graph webhook delivery (needs a public HTTPS endpoint) | delta polling is the local default; `devtunnel` for manual webhook tests; recorded notification payloads in integration tests |
| Document Intelligence Layout output on new documents | recorded fixtures; F0 tier live test; Tesseract remains the local OCR |
| Container Apps scaling, managed identity, Azure networking | Bicep-deployed dev environment exercised by CI on every merge to main |
| Blob immutability and lifecycle policies | asserted in the Azure dev environment, not emulated |
| Entra External ID sign-up/sign-in branding and MFA policies | Entra dev tenant; local Keycloak covers the application's own authorisation logic |
