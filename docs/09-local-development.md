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
| Compute | Azure Container Apps (KEDA scaling on queue length) | **Aspire AppHost** starts the API, the worker and all containers above with one `dotnet run`; the two React apps run as Vite dev servers next to it (README "Web apps and end-to-end tests"). **`docker-compose.yml`** starts the same set plus both apps as production builds behind nginx, without the .NET SDK ("With Docker Compose" below). | Scaling behaviour is not local. | Dev environment in Azure deployed by Bicep from CI on every main merge. |
| Observability | Azure Monitor / Application Insights | Aspire dashboard (traces, logs, metrics via OpenTelemetry). | Same instrumentation, different exporter. | n/a |

## What the AppHost actually starts

`src/DocProc.AppHost/AppHost.cs` is the executable version of the table above. Resource names
double as connection-string names: `DocProc` (database), `blobs`, `messaging`, `keycloak`.
The Keycloak realm is imported from `src/DocProc.AppHost/realms/docproc-realm.json`: realm
roles `SuperAdmin`, `Operator`, `TenantAdmin`, `Reviewer`, `Viewer`, clients `docproc-api`
(bearer-only audience), `docproc-portal` and `docproc-admin` (public, PKCE), a `tenant_id`
claim from a user attribute, and six users across the platform and two tenants (see README).
The realm declares the client scopes `basic` (`sub`), `profile` and `email` itself: an import that
lists `clientScopes` gets none of Keycloak's built-in ones, and without them the apps' sign-in
(`scope=openid profile email`) fails with `invalid_scope` and tokens carry no `sub`. Keycloak imports
the realm on its first start only, so a `docproc-keycloak-data` volume from before keeps the old
realm until it is deleted.
Lowkey Vault (`vault`, HTTPS 8443) runs and both hosts get `KeyVault__Endpoint`, but no
Key Vault `ISecretStore` reads it yet: BYOK keys go to the file-backed store, and the AppHost
gives the API and the worker the same absolute `Secrets__Path`
(`src/DocProc.AppHost/data/secrets`), so a key the API stores is the key the worker's LLM stages
read. Mail flows use GreenMail (`mail`). WireMock (`mock-erp`, http://localhost:8086) stands in for the
receiving ERP of the delivery demos ("Mock ERP" below).
Tests under `tests/DocProc.AppHost.Tests` build the application model without starting it and
assert the resource set, images, waits, the shared secret-store path and realm contents, so a
rename is caught before anyone runs the stack. Aspire's testing builder runs the AppHost's own
`Program` on a background thread, and once a test builds the model that `Program` goes on to
`.Run()`, which starts the orchestrator and every persistent container; only disposing the app
cancelled that start, and a test that did not dispose it left six containers (`storage-*`,
`messaging-*` and its `messaging-mssql-*`, `keycloak-*`, `vault-*`, `mail-*`) running after every
`dotnet test`. Every model check now builds through `AppHostModel`
(`tests/DocProc.AppHost.Tests/AppHostModel.cs`), which stops that run at `BeforeStartEvent`, before the
host and the orchestrator start, and always disposes the app (`Building_the_model_starts_nothing`
holds it); the AppHost itself is unchanged, so `dotnet run` behaves as before. Containers left by an
earlier run are removed with `docker rm -f` by name (the suffix is a hash of the AppHost path).

## With Docker Compose

`docker-compose.yml` at the repository root is the same stack for people without the .NET SDK, or
who want the two web apps as they ship: `docker compose up --build` (`-d` for the background) from a
clean clone; README "With Docker Compose" has the URLs, users, LLM keys and reset. The project name
is `docproc` (`name:` in the file), so volumes and the network are `docproc_*`; `-p <name>` runs a
second copy, though the fixed host ports below allow only one at a time. The AppHost listens on the
same ports while it runs, so stop one before starting the other. Its containers outlive it
(`ContainerLifetime.Persistent`) but do not hold those ports: Aspire proxies every endpoint, so each
container publishes a random host port, and the fixed one belongs to the AppHost's proxy, which
stops with `dotnet run`. Nothing is external: the
default network and four named volumes (`sql-data`, `azurite-data`, `keycloak-data`, `secrets`)
belong to the project, and `down -v` removes them.

**Mock ERP.** `mock-erp` is WireMock 3 with the stub mappings in `docker/mock-erp/mappings`, the
receiving system that the delivery demos of docs/11 §23 send to from P3 on (nothing sends to it in
P0). Both the AppHost and compose run it on host port 8086 with the folder mounted read-only, so a
change to a stub needs a restart of the container (`docker compose restart mock-erp`).

| Request | Answer |
|---|---|
| `POST /api/ap/invoices` (`erp-ap`) with a JSON body | 201 `{"erpId":"AP-<8 digits>","status":"accepted","idempotencyKey":<the Idempotency-Key header>,"receivedAt":...}` |
| `POST /api/article-prices` with a JSON body | 202 `{"importId":"AP-IMP-<8 digits>","status":"queued",...}` |
| either endpoint with another content type | 415 |
| either endpoint with the header `X-Mock-Erp-Fail: 503` | 503 with `Retry-After: 1`, that request only |
| `POST /__control/<erp-ap\|article-prices>/fail-next` | the next call of that endpoint answers 503 (`Retry-After: 1`), the one after succeeds again: the "forced 503, retried, then success" of the P3 demo |
| `POST /__control/<erp-ap\|article-prices>/fail-always`, `.../reset` | every call answers 503 until the reset |
| `GET /__admin/requests` | WireMock's journal: every request it received, with headers and body (`DELETE` clears it) |

The failure modes are WireMock scenarios named after the endpoint, so `POST /__admin/scenarios/reset`
resets both. `ComposeParityTests` holds the compose service to the AppHost (image, arguments, mount,
port) and checks that every endpoint has its success and 503 stubs; `MockErpStubTests`
(`tests/DocProc.Integration.Tests`, Docker) runs the compose file's image over the folder and
exercises each answer. In P3 the http transport sends through `TenantEndpointGuard`, which accepts
only https to public hosts; reaching `http://mock-erp:8080` from a tenant destination therefore needs
the development allowance that docs/11 §13.3 plans for it.

Every long-running service has `restart: unless-stopped`; only the one-shot helpers
(`servicebus-ready`, `llm-setup`) have `restart: "no"` (`ComposeParityTests` checks both). A
container that exits while others wait for it to be healthy otherwise fails the whole `up` with
"dependency failed to start": SQL Server 2025 does exit on a busy machine's first boot now and then
(`[AppLoader] Failed to load LSA: 0xc0070102`, a start-up timeout), and restarted it comes up
normally within the health check's 60 tries. Not always: after a first-boot crash
(`This program has encountered a fatal error ... Reason: 0x00000002`, with a core dump in the volume) the
restarted container can crash again on every restart (`Reason: 0x00000002, Last errno text: No such file or
directory`, `RestartCount` climbing in `docker inspect`), and `up --wait` fails with "container ...-sqlserver-1 is
unhealthy". Then start over with an empty volume and SQL Server on its own first:
`docker compose down -v`, `docker compose up -d --wait sqlserver`, then the usual `up` (with `--build` and the
same `-p` and `-f` options as before). `docker compose ps -a` shows `worker` and
`servicebus` as `Up` without "(healthy)": neither has a health check of its own (see the table).

| Service | Image | Mirrors | Notes |
|---|---|---|---|
| `sqlserver` | `mcr.microsoft.com/mssql/server:2025-latest`, `linux/amd64` | `sql` | `sa` password `DOCPROC_SQL_PASSWORD` (local default in the file); healthy once `sqlcmd` answers |
| `azurite` | `mcr.microsoft.com/azure-storage/azurite` (the AppHost's tag) | `storage` | `--skipApiVersionCheck`; the API creates `raw`, `derived`, `exports` (`Blobs:CreateContainersOnStartup`) |
| `servicebus` | `mcr.microsoft.com/azure-messaging/servicebus-emulator` (the AppHost's tag), `linux/amd64` | `messaging` | entities from `docker/servicebus/Config.json`; stores them in `sqlserver`. The image has no shell, so the one-shot `servicebus-ready` polls its `:5300/health` and the API waits for that |
| `keycloak` | `quay.io/keycloak/keycloak` (the AppHost's tag), `start-dev --import-realm` | `keycloak` | realm mounted from `src/DocProc.AppHost/realms`; `KC_HOSTNAME=http://localhost:${DOCPROC_KEYCLOAK_PORT:-8080}`, `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true` |
| `vault` | `nagyesta/lowkey-vault` | `vault` | in memory; nothing reads `KeyVault__Endpoint` yet |
| `mail` | `greenmail/standalone` | `mail` | same `GREENMAIL_OPTS`; nothing reads `Mail__ImapHost` yet. The health check reads the SMTP greeting and sends `QUIT` (the image has `bash`, no `wget`), since a dropped connection makes GreenMail log a stack trace each time |
| `mock-erp` | `wiremock/wiremock` (the AppHost's tag), `--disable-banner --verbose` | `mock-erp` | the receiving ERP of the delivery demos, `:8086`; stubs mounted read-only from `docker/mock-erp` ("Mock ERP" below); the image's `curl` checks `/__admin/health` |
| `api` | `src/Dockerfile`, target `api` | `api` | migrates, seeds, runs the pipeline in-process; `:5100` |
| `worker` | `src/Dockerfile`, target `worker` | `worker` | waits for the API's health check; no HTTP endpoint, so no health check of its own |
| `llm-setup` | `node:22-bookworm-slim` running `docker/llm-setup.mjs` | (none) | one-shot: LLM keys of `.env` to platform configurations, below |
| `portal`, `admin` | `web/Dockerfile` (`APP=customer-portal` / `admin-console`) | (Vite dev servers) | unprivileged nginx on container port 8080, published on `:5173` / `:5174`; `/api` proxied to `api:8080`, SPA fallback to `index.html`, security headers |

**Configuration.** The hosts get the keys the AppHost sets (`ConnectionStrings__DocProc`,
`ConnectionStrings__blobs`, `ConnectionStrings__messaging`, `Persistence__Provider=SqlServer`,
`Persistence__MigrateOnStartup` for the API, `Authentication__Dev__Enabled=false`,
`Authentication__Oidc__*`, `Secrets__Path`, `KeyVault__Endpoint`, `Mail__ImapHost`/`Port`;
`ComposeParityTests` in `tests/DocProc.AppHost.Tests` fails when the AppHost gives a host a key the
compose file does not) and run in the Development environment like under the AppHost, which loads
`appsettings.Development.json` and lets the development seed run. On top of that the API gets
`Persistence__SeedDevData=true` (the realm's tenants must exist, or every tenant user gets 404
"Unknown tenant"), `Persistence__SeedDemoDocuments` (`DOCPROC_DEMO_DOCUMENTS`, default true; the
image carries `fixtures/demo` and its replay fixtures), `Pipeline__RunInProcess=true` and
`Blobs__CreateContainersOnStartup=true`. Both hosts set
`Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning`, because EF Core logs
every statement at Information and the first start's migrations alone are thousands of lines of
SQL that bury the start-up messages (migrations applied, "Development seed applied", "Demo data
applied", "Now listening"); to see the SQL, set it to `Information` for the host in a
`docker-compose.override.yml`. Both hosts mount the `secrets` volume at `/data/secrets`
(created owner-only for the image's non-root user), so a BYOK key the API stores is one the worker
reads.

**Where documents are processed.** No host reads `ConnectionStrings:messaging` yet: the Service
Bus transport is a later block (docs/03 "Implementation"), and until then the queue is in memory.
The API therefore runs the pipeline itself, and the process that accepts an upload processes it;
the worker starts, connects and runs an idle in-process runner, exactly as under the AppHost. The
emulator still comes up with every entity the AppHost declares (`ComposeParityTests`
holds `docker/servicebus/Config.json` to the app model), so
the transport can be switched on without touching the compose file.

**OIDC issuer and back channel.** The browser signs in at `http://localhost:8080/realms/docproc`,
which is the `iss` of every token, but inside the API's container `localhost:8080` is the container
itself. Keycloak is told its public URL (`KC_HOSTNAME`), so the issuer never depends on who asks,
and answers other containers with back-channel URLs on the host name they used
(`KC_HOSTNAME_BACKCHANNEL_DYNAMIC`). The API keeps `Authentication:Oidc:Authority` at the public
URL and reads the discovery document from `Authentication:Oidc:MetadataAddress`
(`http://keycloak:8080/realms/docproc/.well-known/openid-configuration`), whose `jwks_uri` then
points at `http://keycloak:8080/...` too ("Settings reference"). The apps' host ports are fixed
for the same reason: the realm's redirect URIs name 5173 and 5174. Keycloak's host port is
`DOCPROC_KEYCLOAK_PORT` (default 8080), because 8080 is often taken by another local server; when
that server answers instead of Keycloak, sign-in fails with "Failed to fetch" and a CORS error on
`.../.well-known/openid-configuration`. The port goes into `KC_HOSTNAME`, the API's `Authority`, the
apps' `VITE_OIDC_AUTHORITY` build argument (which overrides `.env.production`, whose 8080 is the
default) and their `OIDC_ORIGIN`, so a change needs `docker compose up --build -d`
(`ComposeParityTests` checks all of them against the compose file). Inside the network Keycloak
stays `keycloak:8080`.

**LLM keys.** Outside the development mode the platform provider is a platform configuration in the
database, managed through `/api/v1/llm/providers` (admin console, "AI providers"); the newest active
one serves every tenant without its own, and its key is resolved from the secret store, then
`Llm:Secrets:<secretRef>`, then `LLM_SECRET_<SECRET_REF>` (docs/08). The seed adds the Anthropic
configuration `llm-platform-eu` disabled, and the demo data adds an active Replay configuration for
the demo PDFs. The compose file passes `.env`'s `ANTHROPIC_API_KEY` to both hosts as
`LLM_SECRET_LLM_PLATFORM_EU`. The one-shot `llm-setup` (`docker/llm-setup.mjs`, after the API is
healthy; the admin console waits for it) signs in as `superadmin` on every `up` and makes the
platform configurations follow `.env`:

- `ANTHROPIC_API_KEY` set: the seeded Anthropic configuration is enabled. Empty: an active seeded
  configuration is disabled again, since without the variable it has no key and every call would
  fail with "No key is stored".
- `OPENAI_API_KEY` with `OPENAI_MODEL`: the platform OpenAI configuration with this key and this
  model for every stage is the active one. The key is recognised by its fingerprint
  (`keyFingerprint` of `GET /llm/providers`, the first 16 hex digits of its SHA-256, which the API
  stores with every key), not by its last four characters, which two keys can share. It is created
  when none exists (the key goes to the shared secret store) and enabled again when it was disabled,
  and every other active platform OpenAI configuration is disabled, so a new key replaces the
  previous one and switching back to an earlier key re-enables that one. Empty: the OpenAI
  configurations stay as they are, since they keep the key stored for them; the log names the
  ones still active (disable them in the admin console).

With both keys, the one configured last is in use; switch in the admin console. A rerun with the same
`.env` changes nothing. It never fails the stack: `docker compose logs llm-setup` says what it did
and ends with the configuration in use. The API's start-up line "LLM platform configuration in
use" ("Demo data" below) is written before `llm-setup` runs, so after a key change it still names
the configuration from before; the admin console's AI providers page shows the current state.
Without any key, e-invoice XML needs no LLM, the demo PDFs replay, and other PDFs end `Failed`.
`node --test docker/llm-setup.test.mjs` runs the script's unit tests (CI's `compose` job does).

**Images.** `src/Dockerfile` restores once for the target architecture (cache mount) from the
project files alone (`COPY --parents src/*/*.csproj` with `Directory.*.props` and `global.json`), so
a source change does not restore again; then each host's stage copies `src/` without the other
host's folder and publishes it in Release, so a change under `src/DocProc.Api` does not publish the
worker again. They run on `mcr.microsoft.com/dotnet/aspnet` as the image's non-root `app` user (the
worker needs the ASP.NET runtime: `DocProc.ServiceDefaults` references it); `curl` is added for the
health check. The XML documentation files are left out of `/app` (the OpenAPI descriptions are
compiled into the API); the `.pdb` files stay, so exceptions in the logs carry file and line
numbers. `web/Dockerfile` copies the workspace's `package.json` files and the lockfile first and runs
`pnpm install --frozen-lockfile` for both apps and their packages (one layer both images share),
then copies the shared packages and the one app and runs its `pnpm build` (typecheck and `vite
build` with `.env.production`). It serves `dist` from `nginxinc/nginx-unprivileged` (nginx as uid
101, listening on 8080) with `web/nginx.conf.template`, which re-resolves `api` through Docker's DNS
so a recreated API container is found again and sends `Content-Security-Policy` (scripts from the
app only; the OIDC authority `OIDC_ORIGIN`, `http://localhost:${DOCPROC_KEYCLOAK_PORT}`, for
discovery, tokens and the silent-renew frame), `X-Content-Type-Options`, `X-Frame-Options:
SAMEORIGIN`, `Referrer-Policy` and `Permissions-Policy`. The base images are pinned to patch releases (SDK `10.0.403`, runtime `10.0.14`, Node
`22.23.4`, nginx `1.29.8`); bump them when a servicing release ships. The `# syntax=docker/dockerfile:1`
line pulls a frontend that knows `COPY --parents` and `--exclude`. `.dockerignore` keeps `bin`, `obj`,
`node_modules`, `dist`, `data`, tests, docs and `.git` out of the build context. CI's `compose` job
runs `docker compose config -q` and builds all four images.

**Building behind a TLS-inspecting proxy.** If restores or `pnpm install` fail with certificate
errors because a corporate proxy re-signs TLS, pass its CA bundle (PEM) as the build secret
`extra_ca_certs`; both Dockerfiles trust it for the build only and do nothing without it. For
example, in a `docker-compose.override.yml`, which compose merges automatically and Git ignores:

```yaml
services:
  api: { build: { secrets: [extra_ca_certs] } }
  worker: { build: { secrets: [extra_ca_certs] } }
  portal: { build: { secrets: [extra_ca_certs] } }
  admin: { build: { secrets: [extra_ca_certs] } }
secrets:
  extra_ca_certs:
    file: /path/to/corporate-ca.pem
```

Recent compose versions build through `buildx bake`, which asks for permission to read that file;
run `BUILDX_BAKE_ENTITLEMENTS_FS=0 docker compose up --build` to allow it.

**Globalization.** `Directory.Build.props` turns on invariant globalization, but
Microsoft.Data.SqlClient refuses to open a connection in that mode ("Globalization Invariant Mode is
not supported"), so `DocProc.Api`, `DocProc.Worker` and `DocProc.Integration.Tests` set
`InvariantGlobalization=false` and run with ICU (the `aspnet` image and macOS ship it). Before that,
no SQL Server connection could open, under the AppHost or the integration tests. The same run found
three SQL Server-only schema errors (error 1785, multiple cascade paths): the foreign keys
`Ingestions.ChannelId`, `Documents.SupplierId` and `ExtractorProfiles.SupplierId` are `NO ACTION`
now (SQLite accepted the cascades), and the API refuses to delete a channel, supplier or the
supplier of an extractor profile that something still references, with 409, before the database
would. That change edits `InitialCreate` (and the snapshot) in place instead of adding a migration,
because no database ever applied the old version: on SQL Server it stopped at error 1785 (and
before the globalization fix no connection opened at all), the SQLite development mode creates its
schema with `EnsureCreated` rather than migrations, and no workflow deploys or migrates a shared
database yet. A database that somehow did apply the old `InitialCreate` keeps the cascades while EF
Core considers it current; recreate it. Behaviour change: deleting a supplier that an extractor
profile references used to set the profile's `SupplierId` to null and is now refused with 409
(docs/05).

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
| `Persistence:SeedDevData` | implied by `Sqlite`; set it explicitly to seed a SQL Server dev database (refused in Production, like the two other switches) | Idempotent seed with fixed ids (`DevSeed`): tenants A and B of the Keycloak realm, an API-upload channel each, five suppliers, the platform Anthropic provider (status Disabled, secret ref `llm-platform-eu`) and tenant A's LLM policy. The type catalogue's own rows (the platform templates and every tenant's adopted legacy types, see "Document types" below) are added on every start whatever this switch says. |
| `Persistence:SeedDemoDocuments` | `true` (default `false`; refused in Production; only acts where the seed runs, so not under the AppHost) | After the seed, the API ingests the demo documents of `Persistence:DemoDocumentsPath` (`fixtures/demo`) and adds members and two weeks of LLM usage; see "Demo data" below. |
| `Llm:Replay:Directory` | `fixtures/demo/llm-replay` | Registers the file replay provider. A relative path is searched upwards from the content root until its first folder (`fixtures`) exists, so `src/DocProc.Api` finds the repository's folder; an absolute path is taken as it is. |
| `Authentication:Dev:Enabled` | `true` | The API signs its own tokens (`POST /dev/token`, `GET /dev/users`, tagged "Development") with an RSA key and the bearer handler trusts issuer `docproc-dev` instead of the OIDC authority. The endpoints are not mapped at all when disabled. |
| `Authentication:Dev:SigningKeyPath` | `./data/dev-signing-key.pem` (relative to the content root) | Unset: a key per process, tokens die on restart. Set: loaded, or generated and written on first start (owner-only permissions on Unix), so tokens survive restarts. The key is loaded at start-up, so a corrupt file stops the host rather than the first request. |
| `Blobs:Provider` | unset: inferred. `Azure` whenever `ConnectionStrings:blobs` (injected by the AppHost) or `Blobs:ServiceUri` is present, `FileSystem` otherwise; an explicit value wins. | `FileSystem` is a content-addressed store on disk (`<root>/<container>/<tenant>/<sha256>` plus a `.meta.json` sidecar). Both files appear by atomic rename, the sidecar before the blob, and readers key on the blob file: a blob that exists always has its metadata, a sidecar on its own (a put that crashed between the two steps) reads as not found and is completed by the next upload of the same bytes, and concurrent uploads of the same bytes all succeed with one stored copy. It cannot issue short-lived read URLs (`GetReadUrlAsync` throws `NotSupportedException`), so the API streams bytes itself in this mode. |
| `Blobs:RootPath` | `./data/blobs` (relative to the content root; the folder is created at start-up) | Both hosts must point at the same root, or the worker cannot read what the API ingested; under the AppHost both use Azurite instead. |
| `Pipeline:RunInProcess` | `true` (the default outside Production; the worker defaults to true everywhere) | The API process consumes its own in-process queue, so an upload is processed without a running worker. `Pipeline:MaxParallelism`, `RetryBaseDelay`, `MaxAttempts` and the `DocumentProcessing:*` tunables sit next to it in `appsettings.Development.json`; every one is listed in "Settings reference" below. |
| `Llm:Secrets:<secretRef>` | empty for `llm-platform-eu` | Provider keys by secret reference (user secrets or `Llm__Secrets__<secretRef>`); the environment variable `LLM_SECRET_<SECRET_REF>` works too. Unset, any LLM stage ends the document `Failed` with `LLM_PROVIDER_UNAVAILABLE`; the structured e-invoice path never needs a key. |
| `Secrets:Path` | `./data/secrets` (relative to the content root; created owner-only on first use) | Root of the file-backed `ISecretStore` that stands in for Key Vault: one owner-only file per secret reference (named by the reference's SHA-256, so `tenant/{id}/{configId}` never becomes a path). BYOK keys posted to `/llm-providers` and `/llm/providers` land here under `tenant/{tenantId}/{configId}` / `platform/{configId}`, and the gateway reads them back from here before it looks at `Llm:Secrets` (the worker composes the same store, so it must point at the same directory as the API), channel credentials posted as `secret` to `/channels` under `tenant/{tenantId}/channel/{channelId}`; SQL keeps only the reference (BYOK rows also the last four characters and a fingerprint). Deleting a provider or channel, or clearing a channel's secret, deletes the file. |

The dev users are the realm's users, with the same roles, `tenant_id` attributes, e-mails and
names, so a front end built against the dev issuer behaves identically against Keycloak:

```bash
curl -s -X POST http://localhost:5100/dev/token -H 'content-type: application/json' -d '{"username":"tenant-a-admin"}' | jq -r .access_token
```

Limits, by design: SQLite has no row-level security and no temporal tables (the EF query
filters are the only tenancy layer, which is why the mode is refused in Production: the host
throws at start-up for `Persistence:Provider=Sqlite`, `Persistence:SeedDevData=true`,
`Persistence:SeedDemoDocuments=true` or `Authentication:Dev:Enabled=true`),
and it cannot compare or `ORDER BY` a `DateTimeOffset` column stored as text. The context therefore
stores every timestamp as UTC ticks (an integer) when the provider is SQLite, so the cursor-paginated
lists, the idempotency window and the timeline order work unchanged; SQL Server keeps `datetimeoffset`.
A `docproc.db` created before that change must be deleted (as after any model change). Everything
SQL Server-specific is exercised by the integration tests against a container, not here.

### Document types

The type catalogue (docs/11 §5, §6.1) is seeded on every start: the SQLite schema host and the
migration host add the platform templates and, for every tenant, the standard types it adopts under
the legacy keys (`Invoice` … `Other`, version 1, published); the `TypeCatalogue` migration does the
same on SQL Server. Documents are processed with their tenant's published version of their type
(pinned in `Documents.DocumentTypeVersionId` by the Extract stage).

Changing the catalogue needs the `configurableTypes` switch of the tenant (all switches are off by
default, docs/11 §3) and ConfigAdmin (a tenant admin); a type of the tenant's own also needs
`customTypes`.

**In the browser.** As `superadmin` in the admin console, open the tenant, tab Settings: the card
"Feature switches" turns `configurableTypes` and `customTypes` on and off (custom types switch the
catalogue on with them). The same tenant page has a Document types tab. In the customer portal, a tenant
admin (`tenant-a-admin`) finds the catalogue under Settings > Document types (`/settings?tab=types`):
the templates with Adopt and Copy, the tenant's types with Fork (an adopted type, such as `CreditNote`,
becomes the tenant's copy under the same key) and the type editor (fields, labels in English and German,
the labels a value is printed with and a pattern as extraction hints, required, indexed, options, a
preview of the generated review form, validate and publish with the publish report, the versions, the
JSON). With the switch off the catalogue is read only. Once a type is definition-driven, its documents
open in review with the generated form (delivery notes, purchase orders, price lists, custom fields),
and the inbox filters by the type's indexed fields and shows them as columns
(`GET /documents?fields=<path>,…` returns the values in `DocumentSummary.fields`). A document keeps the
type version it was read with; the review screen's Reprocess dialog offers "Use the published version of
the type" (`configMode: current`), ticked when a newer version is published, and then starts from
Extract, where the added fields are read.

With the dev issuer:

```bash
TOKEN=$(curl -s -X POST http://localhost:5100/dev/token -H 'content-type: application/json' -d '{"username":"tenant-a-admin"}' | jq -r .access_token)
H="authorization: Bearer $TOKEN"
# Switch the feature on (features is optional on PUT; the other settings are sent as read).
curl -s http://localhost:5100/api/v1/settings -H "$H" | jq '.settings | .features.configurableTypes = true' \
  | curl -s -X PUT http://localhost:5100/api/v1/settings -H "$H" -H 'content-type: application/json' -d @- | jq .settings.features
curl -s http://localhost:5100/api/v1/document-templates -H "$H" | jq '.[].key'
# Adopt the price list (published at once), or fork the credit note in place as a draft to edit.
curl -s -X POST http://localhost:5100/api/v1/document-types -H "$H" -H 'content-type: application/json' -d '{"from":{"template":"std.price-list","mode":"adopt"}}' | jq '{key, origin, publishedVersion}'
curl -s -X POST http://localhost:5100/api/v1/document-types -H "$H" -H 'content-type: application/json' -d '{"from":{"template":"std.credit-note","mode":"copy"}}' | jq '{key, origin, draftVersion}'
```

A draft is edited with `PUT /api/v1/document-types/{key}/versions/{v}` (the body is the definition;
`If-Match` is the ETag of `GET …/versions/{v}`), checked with `POST …/versions/{v}:validate` and
published with `POST …/versions/{v}:publish` (422 with the report when a check fails). The
publish-time checks and their report are those of docs/11 §5.7. A fork of `Invoice` or `CreditNote`
keeps the fields of the invoice record as they are (relabel, require, deprecate and add fields; removing,
renaming or retyping one of them is refused, because the coded extraction and rules read them); an added
group or table of such a fork is filled in review, not asked of the LLM. Only an invoice or credit note
with its `invoice.totals` rule is approved without a reviewer in P1: on any other type
`processing.review.autoApprove` has no effect yet, and the report says so. A field renamed in a new version
keeps its value in documents moved onto that version (reprocessed from Extract or Validate with
`configMode: "current"`, or corrected after the move); a field moved into another group or table does
not, and the report says so. Publish refuses a field that takes a key another field of the type had in
an earlier version (old documents hold that field's value under it), sibling keys that differ only by
case (`refNo` and `refno`), and a `pattern` rule or field pattern the non-backtracking engine cannot run
(backreferences, lookarounds, atomic groups).

**A custom field, end to end (demo data).** The demo PDF invoice of Bio-Hof Müller prints
"Kundennummer 0815". Fork `Invoice` in place, add the field of
`fixtures/demo/types/invoice-customer-number.field.json`, publish, and reprocess the invoice with
`configMode: "current"` (a reprocess otherwise keeps the type version the document is pinned to). The
LLM is asked for the field in `x_custom` (schema `invoice_extraction@1+x.<hash8>`), answered from the
third replay fixture, and the document is stored as a definition-driven tree
(`canonicalSchemaVersion` 2) with the field indexed, so the inbox finds it:

```bash
curl -s -X POST http://localhost:5100/api/v1/document-types -H "$H" -H 'content-type: application/json' -d '{"from":{"template":"std.invoice","mode":"copy"}}' | jq '{key, draftVersion}'
ETAG=$(curl -s -D - -o /tmp/invoice-v2.json http://localhost:5100/api/v1/document-types/Invoice/versions/2 -H "$H" | awk 'tolower($1)=="etag:"{print $2}' | tr -d '\r')
jq --slurpfile f fixtures/demo/types/invoice-customer-number.field.json '.definition | .fields += $f' /tmp/invoice-v2.json \
  | curl -s -X PUT http://localhost:5100/api/v1/document-types/Invoice/versions/2 -H "$H" -H "if-match: $ETAG" -H 'content-type: application/json' -d @- | jq .state
curl -s -X POST http://localhost:5100/api/v1/document-types/Invoice/versions/2:publish -H "$H" -H 'content-type: application/json' -d '{}' | jq .publishedVersion
DOC=$(curl -s "http://localhost:5100/api/v1/documents?q=2026-131" -H "$H" | jq -r '.items[0].id')
curl -s -X POST "http://localhost:5100/api/v1/documents/$DOC:reprocess" -H "$H" -H 'content-type: application/json' -d '{"fromStage":"Extract","configMode":"current"}' | jq .status
sleep 5
curl -s "http://localhost:5100/api/v1/documents?field.supplierCustomerRef=0815" -H "$H" | jq '.items[] | {documentNumber, status}'
curl -s "http://localhost:5100/api/v1/documents/$DOC" -H "$H" | jq '{canonicalSchemaVersion, typeVersion, ref: .canonical.supplierCustomerRef.value}'
```

The fixture matches only this field: its key, kind and description (with `extraction.llmHint`, if
any) are part of the LLM schema and therefore of the request hash; its labels, printed labels, pattern,
section and `indexed` are not. It also needs a demo database where the PDF invoice is still as the
demo data left it; another field, or another PDF, gets a replay miss and ends `Failed` as described
under "Demo data".

Documents of a definition-driven type (docs/11 §5.6) are stored and corrected by their type
version: with `configurableTypes` on, a purchase order or delivery note gets the empty tree of its
type in Extract (`UNSUPPORTED_TYPE` with `reason: definition` says it is filled in review), and a
price list adopted from the catalogue is checked by `validate.schema` and its rules. `PATCH
/documents/{id}/fields` then takes `append` (a row, with an object of its values, or a list entry)
and `remove` (a row, a list entry or an `ext` key) besides `set`, re-validates the document at once
and answers with the new flags. A document of such a type read before the switch was turned on has no
tree; while it is correctable (`NeedsReview`, `Validated`, `Failed`) `GET /documents/{id}` answers the
empty tree of its type version (`canonicalSchemaVersion` 2) as `canonical`, which the first correction
stores, and a stored tree that lacks fields its version has is answered completed with them; nothing is
written by a read. Indexed fields are filtered with `?field.<path>=value` on `GET /documents` (at most
5 such filters, one value each, else a 400), compared
as the index stores them (identifiers trimmed, repeated spaces collapsed and upper-cased, so
`104 220815` is found as `104 220815` but not as `104220815`; a number of 10^17 or more is compared as
text only, the index holds no number that large); `validFrom`, `validUntil`, `lineCount`
and the open review task's `reviewPriority` are in every inbox row.

**Review actions (docs/11 §12, P2).** A document in review (`NeedsReview`, `Validated`, `Failed`) can be
given a type, a supplier, or other pages; docs/05 has the request and answer of each. With the dev issuer
(`$H` as above, a reviewer is enough) and an Unknown document `$DOC`:

```bash
# Type it as a price list (adopted above): the empty fields of the type at once.
curl -s -X POST "http://localhost:5100/api/v1/documents/$DOC:set-type" -H "$H" -H 'content-type: application/json' -d '{"typeKey":"price-list"}' | jq '{type, typeVersion, approvalBlocked}'
# A value captured into the wrong field moves to the right one (one correction, history kept).
curl -s -X PATCH "http://localhost:5100/api/v1/documents/$DOC/fields" -H "$H" -H 'content-type: application/json' -d '{"corrections":[{"path":"issueDate","value":"2026-11-01"}]}' > /dev/null
curl -s -X PATCH "http://localhost:5100/api/v1/documents/$DOC/fields" -H "$H" -H 'content-type: application/json' -d '{"corrections":[{"path":"validFrom","op":"move","from":"issueDate"}]}' | jq .validFrom
# Or have the Extract stage read it for a type (202; an invoice is read by the LLM).
curl -s -X POST "http://localhost:5100/api/v1/documents/$DOC:set-type" -H "$H" -H 'content-type: application/json' -d '{"typeKey":"Invoice","prefill":"auto"}' | jq .status
# Create the supplier from the document, or assign an existing one.
curl -s -X POST "http://localhost:5100/api/v1/documents/$DOC:create-supplier" -H "$H" -H 'content-type: application/json' -d '{"name":"Nordwind Großhandel GmbH","vatId":"DE811111111"}' | jq .supplier.id
# Pages 1-2 and 3 are two documents; page 3 is an invoice. Join them again with :join.
curl -s -X POST "http://localhost:5100/api/v1/documents/$DOC:split" -H "$H" -H 'content-type: application/json' -d '{"parts":[{"pages":[1,2]},{"pages":[3],"typeKey":"Invoice"}]}' | jq .parts
```

With `configurableTypes` on, `:approve` refuses an Unknown document, one whose type has no published
definition, and one with no data (`409 Cannot approve` with `code`), and the review screen shows the reason
under a disabled Approve button (`DocumentDetail.approvalBlocked`); a tenant without the switch approves as
before. A part joined into another document is `Cancelled` (status filter `Cancelled`, shown only under "All").

### Demo data

With `Persistence:SeedDemoDocuments=true` (the `appsettings.Development.json` default) the API's
`DemoDataHostedService` (`src/DocProc.Pipeline/Demo`) runs after the seed and fills the dev database with
something worth looking at, so the portal, the admin console and the screenshots show real documents on a
clean checkout without Docker or an LLM key:

| What | Rows |
|---|---|
| Documents | Every file under `fixtures/demo/<tenant slug>/` is ingested for that tenant through the same `UploadIntake` code path as `POST /ingestions` (raw blob, ingestion with one candidate item, `ProcessIngestion` on the in-process queue), from the tenant admin's address. The pipeline then processes them like any upload. |
| Replay provider | An active platform configuration of kind `Replay` (`DemoData.PlatformReplayConfig`) with the same models as the platform Anthropic one, so the gateway answers the demo PDFs' LLM calls from `fixtures/demo/llm-replay`. Added only when no other platform configuration is active: a dev database that already runs on a real key keeps it. A request with no fixture (any other PDF you upload) ends `Failed` at once (a miss is an `InvalidRequest`, which no retry fixes) with "No recorded response … This document has no demo fixture …" (the `LlmReplayMissHint` the demo data registers) instead of `LLM_PROVIDER_UNAVAILABLE`; to use a real key, store it and enable the Anthropic configuration in the admin console (the newest active platform configuration wins) or disable the Replay one. The start-up log names the platform configuration in use (under docker compose, before `llm-setup` has applied `.env`: its own log names the one in use afterwards), and warns instead when the active configuration is a real provider whose key the gateway cannot resolve (no `Llm:Secrets:<secretRef>` or `LLM_SECRET_<SECRET_REF>`), because every LLM call would then fail. |
| Members | `TenantMember` rows for `tenant-a-admin`, `tenant-a-reviewer`, `tenant-b-admin` and `tenant-b-viewer`, with the dev issuer's `sub` (`DevSeed.SubjectFor`), e-mail and name, so the members screens list the people you sign in as. |
| LLM usage | About 50 `LlmUsageEntry` rows over the 14 UTC days up to the first start (both tenants, platform Anthropic configuration, classify and extract, two failed calls), each at 08:00 UTC of its day plus a fixed offset (today's clamped to the start time), marked by a `ProviderRequestId` starting `demo-usage-`, so the usage charts have a history. The rows are written once: the history does not move forward with later starts; reset `data/` to refresh it. |

The demo documents and their designed outcome:

| File (`fixtures/demo/…`) | Format | Ends as |
|---|---|---|
| `tenant-a/METRO_Rechnung_RG-2026-882140.xml` | PEPPOL BIS 3 (UBL) invoice, VAT id of the METRO supplier row | `AutoApproved`, no flags |
| `tenant-a/METRO_Gutschrift_GS-2026-31207.xml` | PEPPOL BIS 3 credit note for that invoice | `AutoApproved`, type `CreditNote` |
| `tenant-a/Getraenke-Hoffmann_RE-2026-55318.xml` | ZUGFeRD / Factur-X EN 16931 (CII) with deposit lines; matched by name (the supplier row has no VAT id) | `AutoApproved` |
| `tenant-a/BioHof-Mueller_Rechnung_2026-118.xml` | UBL invoice whose lines sum to 230.00 while the printed subtotal is 240.00 | `NeedsReview`, Blocking `TOTALS_MISMATCH`, open review task |
| `tenant-a/BioHof-Mueller_Rechnung_2026-131.pdf` | two-page PDF invoice with a text layer | extracted by the LLM path from the replay fixture; `NeedsReview` with `LOW_CONFIDENCE` on `dueDate` (the model inferred it from "14 Tagen", it is not printed) |
| `tenant-a/BioHof-Mueller_Lieferung_L-2026-0457.pdf` | one-page delivery slip titled "Lieferung" that says "Die Rechnung folgt", so the keyword rules find no type | classified `DeliveryNote` by the (replayed) LLM classifier; `NeedsReview` with `UNSUPPORTED_TYPE` |
| `tenant-b/Konzum_Racun_04518-2026-P12.xml` | XRechnung 3.0 (UBL) from Konzum plus, 25 % and 5 % VAT | `AutoApproved` |

Everything is idempotent. The replay configuration, the members and the usage are added once, on the first
start-up of a database: the `demo-usage-` rows are the marker, so a member removed through the members screen or a
Replay configuration deleted in the admin console stays removed after a restart. A demo file counts as present when
its tenant has an ingestion with that source file name, so a restart ingests only files added to the folder since.
A missing demo folder is logged as a warning, and the documents go through the tenant's oldest active API-upload
channel (none active: the tenant's files are skipped with a warning).

The in-process queue is not durable, so a start-up that stops before it drains (Ctrl+C, a `dotnet watch` restart,
a crash) loses the queued work. Every start therefore resumes the demo work it finds unfinished, and logs each item
as "Resumed demo …": a demo ingestion still `Received` gets its `ProcessIngestion` again, and a demo document in a
pipeline state gets the `ProcessDocument` of the stage whose handler accepts that state (`Received` → Normalize,
`Normalized` → Split, … `Extracted` → Validate; `DemoData.ResumeStage`). A settled demo document (`AutoApproved`,
`Approved`, `Rejected`) whose settlement is not in the outbox lost its publish message and gets `ProcessDocument(Publish)`
again; "in the outbox" is the publish stage's own rule (a row of the settlement's event type written at or after the
settlement), so a row left by an earlier settlement, such as a rejection before a reprocess, does not count;
otherwise `Failed`, `NeedsReview` and the settled states are left alone, as is a document persisted `Validated` (which
the pipeline never does; it is logged). Every handler drops a message whose work is already done, so a twin is harmless.
For every tenant, not only the demo ones, the publish sweeper (`Pipeline:PublishSweep*` below) covers the rest: approve,
reject and auto-approval write the outbox row with the decision, so a publish message lost after that commit leaves a
row the publish stage never handed on, and the first sweep after the restart that finds the decision older than the grace
period (a few minutes with the defaults) queues the publish stage for it; its timeline entry then says `recoveredBy: "publish-sweeper"` (docs/11 §13.5).

**Demo: a decision whose publish message is lost** (the P0 demo of docs/11 §23). In the Development environment
`Pipeline:Faults:CrashBeforePublishEnqueueMarker` names a file; while that file exists, the next decision's publish
nudge (approve, reject or auto-approval) deletes it and ends the process at once (`Environment.FailFast`), after the
decision and its outbox row were committed and before the publish message is queued (`CrashBeforePublishEnqueueQueue`).
With compose, start the stack with the key set for the API (`Pipeline__Faults__CrashBeforePublishEnqueueMarker:
/tmp/crash-before-publish-enqueue` in a `docker-compose.override.yml`), arm it with
`docker compose exec api touch /tmp/crash-before-publish-enqueue`, and approve a document in review: the approve call
gets no answer, `restart: unless-stopped` starts the API again, the document is `Approved` without a publish entry,
and the first sweep after the grace period publishes it with `recoveredBy: "publish-sweeper"` in its timeline. The
marker is deleted before the process ends, so the restarted API does not crash again, and the next approval publishes
normally. Outside Development the key is ignored, and no shipped configuration sets it.
`DemoDataTests` (`tests/DocProc.Api.Tests`) boots the API in the Development environment and asserts the table
above and that a second start-up seeds nothing; `DemoDataHostTests` (`tests/DocProc.Pipeline.Tests/Demo`) interrupts
a start-up with the delivery note mid-pipeline and six ingestions queued, restarts, and asserts that all seven
documents reach the outcome above and that a further restart resumes nothing. The invoice dates are fixed (late October 2026); from a year
after that, stage 7 adds a `DATE_IMPLAUSIBLE` warning, which changes no outcome.

**Reset.** Stop the API and delete `src/DocProc.Api/data/` (database, blobs, secrets, signing key); the next
start seeds and ingests everything again, with a usage history that ends today. To keep the data but drop the demo documents, set
`Persistence:SeedDemoDocuments=false` before the first start.

**Replay fixtures.** `fixtures/demo/llm-replay/<request hash>.json` are `FileReplayStore` files: a readable
header, the usage and a hand-authored, schema-valid answer (`tests/DocProc.Pipeline.Tests/Demo/DemoAnswers.cs`).
The hash is `RequestHasher.Hash` over model, prompt, task text, schema, options and the document bytes the
stage sends (the PDF for extraction, the rendered first page PNG for classification).
`DemoFixtureTests.Llm_replay_fixtures_answer_exactly_the_requests_the_demo_pdfs_produce` boots the processing
stack with the API's `appsettings.json` and `appsettings.Development.json`, lets the demo data run with a capturing
provider in place of the replay store, hashes every request the real stages send and compares the resulting files
with the committed ones. It then forks tenant A's `Invoice` with the demo custom field (`fixtures/demo/types`, see
"Document types" above) and reprocesses the PDF invoice with `configMode: "current"`, which adds the third fixture:
the extraction with `x_custom`. Last it forks `CreditNote` with the field of docs/11 §5.3
(`fixtures/demo/types/credit-note-supplier-customer-ref.field.json`) and uploads the credit note of
`fixtures/demo/uploads/tenant-a`, which adds the fourth: that credit note's extraction with `x_custom` ("Demo: types
as data" below). After a change to a prompt, a schema, the LLM options, the models, the page rendering or a
demo PDF it fails and says so; regenerate and commit:

```bash
UPDATE_DEMO_FIXTURES=1 dotnet test --project tests/DocProc.Pipeline.Tests -c Release -- --filter-class DocProc.Pipeline.Tests.Demo.DemoFixtureTests
git add fixtures/demo/llm-replay
```

The three PDFs (the two of `tenant-a` and the credit note of `uploads/tenant-a`) are committed bytes, drawn once by
`tests/DocProc.Pipeline.Tests/Pdf/DemoPdfs.cs` with PDFsharp (test-only, see below). PDFsharp writes a new document
id on every save, so the normal run never rewrites them; after editing `DemoPdfs`, run once with
`REGENERATE_DEMO_PDFS=1` (every PDF) or `REGENERATE_DEMO_PDFS=<file name>` (that one only, so the others' bytes and
fixtures stay as they are) and then with `UPDATE_DEMO_FIXTURES=1`. To add a demo document, drop the file into the
tenant folder (or into `uploads/<tenant slug>` for one a demo uploads by hand: the demo data does not ingest it);
if it is a PDF that needs an LLM call, add its answer to `DemoAnswers` (and its expected request to the fixture
test) and regenerate. `DemoPdfClassificationTests` holds which demo PDFs the keyword rules classify.

The classification hash covers the PNG that PDFium renders. The committed fixtures are generated on linux-x64 (the
CI platform, `DemoData.FixtureCapturePlatform`); a machine whose PDFium build rasterises the page differently gets a
replay miss for the delivery note, which then ends `Failed`, and the API logs a warning saying so at start-up. On any
platform other than linux-x64 (`RuntimeInformation`: Linux and X64), `DemoFixtureTests` compares only the extraction
fixture, which hashes the PDF bytes and is platform-independent, `UPDATE_DEMO_FIXTURES=1` rewrites only that one, and
`DemoDataTests` skips the delivery-note assertions; both tests then report themselves skipped with this explanation.
Do not commit a classification fixture regenerated on such a machine. `fixtures/demo/.gitattributes` (`* -text`)
keeps every file's bytes exactly as committed on every checkout, whatever `core.autocrlf` says, since the bytes key
the request hashes.

### Demo: types as data

The P1 demo of docs/11 §23, run as the product owner needs it: a tenant gets a document type the system has no
fields for, or a supplier-specific identifier on a known one, and defines the fields itself, custom fields
included, without a code change. docs/11 tells it for the tenant "Acme"; in the demo data that is tenant A,
Gasthaus zur Post GmbH (`tenant-a`). Run it on a fresh stack (`docker compose up --build`, README "With Docker
Compose"; no LLM key needed) or in the dependency-free dev mode (the same screens with the dev sign-in, API on
port 5100). The screens are the portal on http://localhost:5173 and the admin console on http://localhost:5174.

1. **Before.** As `tenant-a-reviewer`, open the Bio-Hof Müller delivery note ("(no number)", Needs review) from the
   inbox: it says "This document type has no form to review yet", the gap the product owner hit. As
   `tenant-a-admin` (a reviewer has no Settings), Settings > Document types shows the tenant's catalogue read only
   under "Document types are switched off", because `configurableTypes` is off.
2. **Switch on.** As `superadmin` in the admin console: Tenants > Gasthaus zur Post GmbH > Settings > Feature
   switches: Configurable document types on (Types of the tenant's own as well, to offer New type for a type no
   template covers).
3. **Adopt and fork.** As `tenant-a-admin` in the portal: Settings > Document types. Price list > Adopt (`std.price-list`
   v1, published at once). Credit note > Fork to edit (a v2 draft under the key `CreditNote`). Add field, with the
   values of `fixtures/demo/types/credit-note-supplier-customer-ref.field.json` (the field of docs/11 §5.3):

   | Field | Value |
   |---|---|
   | Key / Kind | `supplierCustomerRef` / Number / identifier |
   | Label (EN) / (DE) | Our customer no. at supplier / Unsere Kundennummer beim Lieferanten |
   | Section | Document |
   | Description | `The account number the supplier assigned to us, printed as Kunden-Nr. or Kd.-Nr.` (exactly: with the key and kind it keys the replay fixture) |
   | Printed as (DE) / (EN) | Kunden-Nr., Kd.-Nr., Kundennummer / Customer No., Account No. |
   | Pattern | `^[0-9]{3}\s?[0-9]{6}$` |
   | Indexed | on |

   Save draft, then Validate: "The checks passed", extraction schema `invoice_extraction@1+x.58a31050`, the field
   listed as Added. Versions > Publish (v2; v1 becomes Deprecated).
4. **A credit note with that field arrives.** Inbox > Upload > `fixtures/demo/uploads/tenant-a/Getraenke-Hoffmann_Gutschrift_GS-2026-0918.pdf`
   (a Getränke Hoffmann credit note for empties, "Kd.-Nr. 104 220815"). The keyword rules classify it ("Gutschrift"),
   the Extract stage asks the LLM for the field in `x_custom` and is answered by the fourth replay fixture, and it is
   stored as a version 2 tree pinned to CreditNote v2, Needs review with one warning: `LOW_CONFIDENCE` on the due
   date, which the model worked out from "spätestens nach 30 Tagen" and which is not printed.
5. **Review.** As `tenant-a-reviewer`, open GS-2026-0918: the header says Credit note v2, and the generated form
   shows "Our customer no. at supplier" = `104 220815` with its AI provenance on page 1 (highlighted on the page).
   Change it (to `104 220816`, say) and Save corrections: the field is Corrected, re-validated at once. Then set it
   back to `104 220815` and save again: step 6 and the curl call below search for that value, and the index holds
   whatever was saved last.
6. **Inbox.** Tab All, Type Credit note, Field "Our customer no. at supplier", value `104 220815`, Apply: the credit
   note alone, with the field as a column (the value is compared as the index stores it, see "Document types").
   Through the API: `GET /api/v1/documents?type=CreditNote&field.supplierCustomerRef=104%20220815&fields=supplierCustomerRef`;
   tenant B finds nothing.
7. **The delivery note gets a defined type with fields.** Open the delivery note again: it now shows the generated
   form of Delivery note v1 with empty fields (it was read before the switch; the API answers the empty tree of its
   type) and can be filled, rows included. To add fields of the tenant's own: as `tenant-a-admin`, Delivery note >
   Fork to edit, add `tour` (Delivery round / Tour, indexed) and `supplierCustomerRef` (Number / identifier,
   labels Our customer no. at supplier / Unsere Kundennummer beim Lieferanten; a field without labels shows as
   "New field"), Save draft, Validate, Publish v2. As the reviewer, Reprocess: "Use the published version of the type" is ticked
   ("This document was read with Delivery note v1; v2 is published"), from Extract. The form now has the two
   fields; fill Delivery note no. `L-2026-0457`, Delivery round `Rosenheim Nord`, Our customer no. at supplier `0815` and a
   row, Save corrections, Approve. `GET /api/v1/documents?field.tour=Rosenheim%20Nord` finds it.

In the compose stack the API takes Keycloak tokens; for curl, the portal client allows the password grant:

```bash
TOKEN=$(curl -s http://localhost:8080/realms/docproc/protocol/openid-connect/token -d grant_type=password \
  -d client_id=docproc-portal -d username=tenant-a-reviewer -d password=tenant-a-reviewer | jq -r .access_token)
curl -s "http://localhost:5100/api/v1/documents?type=CreditNote&field.supplierCustomerRef=104%20220815&fields=supplierCustomerRef" \
  -H "authorization: Bearer $TOKEN" | jq '.items[] | {documentNumber, status, fields}'
```

The replay fixture answers only this field and this PDF (see "Replay fixtures" above); a field with another key,
kind or description sends another schema and gets a replay miss, and the credit note then ends `Failed`.
`docker compose down -v` resets the stack (in dev mode, delete `src/DocProc.Api/data/`). What the demo shows is
held by tests too: `DemoFixtureTests` (fork, upload, x_custom, the tree, the index row), `DemoPdfClassificationTests`,
`DefinitionDrivenDocumentEndpointsTests` (the empty tree, the completed tree, definitions on decisions), the portal's
`ReviewScreen.definition` tests (the reprocess option, the empty delivery note) and `DocumentTypesPanel` tests (the
extraction hints).

## Settings reference

Every setting the API and the worker read, by section. Environment variables use `__` for `:`
(`Pipeline__MaxParallelism=4`). Every key has a code default, so a host starts with none of them; the
two hosts' `appsettings.json` / `appsettings.Development.json` repeat the processing tunables with
their defaults so they can be found and changed in one place. `SettingsReferenceTests`
(`tests/DocProc.Api.Tests/Docs`) fails when an options class or a host settings file gains a key
this section does not name.

**Persistence** (`PersistenceSettings`, `PersistenceHostExtensions`; both hosts)

| Key | Default | What it does |
|---|---|---|
| `ConnectionStrings:DocProc` | unset | SQL Server connection string; the AppHost injects it. Unset and no `Persistence:Provider`: the host starts without a database. |
| `Persistence:Provider` | `SqlServer` when the connection string is set, else none | `SqlServer` or `Sqlite` ("Running without Docker"); `Sqlite` is refused in Production. |
| `Persistence:SqlitePath` | `./data/docproc.db` | SQLite file, relative to the content root. |
| `Persistence:MigrateOnStartup` | API: true outside Production; worker: false | Applies pending migrations at start-up (`MigrationHostedService`). The AppHost sets it for the API only. |
| `Persistence:SeedDevData` | false (implied by `Sqlite`) | Runs `DevSeed`; refused in Production. |
| `Persistence:SeedDemoDocuments` | false | Ingests the demo documents after the seed ("Demo data"); API only, refused in Production. |
| `Persistence:DemoDocumentsPath` | `fixtures/demo` | Folder with one sub-folder per tenant slug; a relative path is searched upwards from the content root. |

**Authentication** (`DevAuthOptions`, `OidcOptions`; API)

| Key | Default | What it does |
|---|---|---|
| `Authentication:Dev:Enabled` | false (true in `appsettings.Development.json`) | The API signs its own tokens (`/dev/token`, `/dev/users`); refused in Production. |
| `Authentication:Dev:SigningKeyPath` | unset (a key per process) | PEM file of the dev signing key, created on first start. |
| `Authentication:Dev:TokenLifetime` | `08:00:00` | Lifetime of a dev token (`DevTokenIssuer`). |
| `Authentication:Oidc:Authority` | unset | Issuer URL; empty disables metadata discovery (the tests supply keys). Ignored while dev auth is on. |
| `Authentication:Oidc:MetadataAddress` | unset (`{Authority}/.well-known/openid-configuration`) | Back-channel URL of the discovery document, for a host that cannot reach the issuer URL the browser uses: docker compose sets `http://keycloak:8080/realms/docproc/.well-known/openid-configuration` while `Authority` stays `http://localhost:8080/realms/docproc`, the `iss` of every token. Only where the metadata and keys are read changes, never which issuer is trusted; needs `Authority` and an absolute http(s) URL, or the host refuses to start. `RequireHttpsMetadata` applies to it. |
| `Authentication:Oidc:Audience` | `docproc-api` | Expected `aud`. |
| `Authentication:Oidc:RequireHttpsMetadata` | true | False only for the local Keycloak over plain HTTP. |
| `Authentication:Oidc:RoleClaimType` | `roles` | Claim the roles are read from (also for `/api/v1/me`). |
| `Authentication:Oidc:NameClaimType` | `preferred_username` | Claim used as the user name. |
| `Authentication:Oidc:TenantClaimType` | `tenant_id` | Claim that binds a customer user to a tenant (`TenantResolutionMiddleware`). |

**Blob storage and secrets** (`BlobStoreOptions`, `SecretStoreOptions`; both hosts)

| Key | Default | What it does |
|---|---|---|
| `Blobs:Provider` | inferred | `FileSystem` or `Azure`; see the "Running without Docker" table for the inference. |
| `Blobs:RootPath` | `./data/blobs` | Root of the file-system store. |
| `ConnectionStrings:blobs` | unset | Azurite or Azure connection string, or a bare service URI; the AppHost injects it. |
| `Blobs:ServiceUri` | unset | Blob endpoint used with `DefaultAzureCredential` when no connection string carries a key. |
| `Blobs:MaxReadUrlLifetime` | `00:15:00` | Upper bound of a read URL; longer requests are clamped. At most seven days (the validator refuses more). |
| `Blobs:CreateContainersOnStartup` | false | Creates `raw`, `derived` and `exports` at start-up (`BlobContainersHostedService`); off because the AppHost and Bicep own the containers. |
| `Secrets:Path` | `./data/secrets` | Root of the file-backed secret store that stands in for Key Vault. |

**Pipeline** (`PipelineOptions`; both hosts). The section is validated when a host that runs the pipeline starts
(`PipelineOptionsValidator`, `ValidateOnStart`): a value outside the bounds below stops the start-up with an
`OptionsValidationException` that names the key and the bound, instead of failing every document later.

| Key | Default | What it does |
|---|---|---|
| `Pipeline:RunInProcess` | true outside Production; the worker: true everywhere | Whether this process consumes the in-process queue (docs/03 "Implementation"). |
| `Pipeline:MaxParallelism` | 2 | Messages the runner processes at the same time. At least 1. |
| `Pipeline:RetryBaseDelay` | `00:00:05` | Delay before the second attempt; doubled per attempt. Not negative. |
| `Pipeline:MaxRetryDelay` | `00:10:00` | Cap of one backoff delay. Not negative. |
| `Pipeline:MaxAttempts` | 5 | Attempts per stage message, the first included; the last failure ends `Failed`. At least 1. |
| `Pipeline:ShutdownGracePeriod` | `00:00:30` | How long the runner waits for in-flight messages when the host stops. Not negative. |
| `Pipeline:PublishSweepInterval` | `00:01:00` | How often the publish sweeper (`PublishSweeperHostedService`, next to the runner) looks for settled documents whose publish message was lost (docs/11 §13.5); the first sweep runs one interval after start. `00:00:00` turns it off. Not negative. |
| `Pipeline:PublishSweepGrace` | `00:02:00` | How long a settlement may wait for its normal publish message before the sweeper queues one. Not negative. |
| `Pipeline:PublishSweepLookback` | `7.00:00:00` | How far back the sweeper looks; older settlements are left alone. Longer than the grace. |
| `Pipeline:PublishSweepBatchSize` | 200 | Most settlements one sweep queues. At least 1. |
| `Pipeline:Faults:CrashBeforePublishEnqueueMarker` | (unset) | Development only: a file whose presence makes the next decision's publish nudge end the process between the commit and the enqueue, for the publish sweeper demo ("Demo data" above). Ignored in every other environment. |
| `Pipeline:RenderDpi` | 200 | Page raster resolution of stage 2 (`NormalizeHandler`). At least 1 and at most `DocumentProcessing:Pdf:MaximumRenderDpi`: a higher value would fail every PDF render, so the host refuses to start with it. |
| `Pipeline:LlmClassificationThreshold` | 0.6 | Stage 4 (`ClassifyHandler`) asks the LLM classifier when the keyword answer is `Unknown` or below this confidence. Between 0 and 1. |
| `Pipeline:MaxArchiveDepth` | 2 | Archives nested deeper are not expanded (`IngestHandler`, docs/03 stage 2). Not negative. |
| `Pipeline:MaxArchiveEntries` | 200 | Files unpacked from one archive; the rest are not, and the archive's reason says so (`the rest was not: the archive's limit of N unpacked files is reached`). At least 1. |
| `Pipeline:MaxArchiveEntryBytes` | 67108864 (64 MiB) | Largest archive entry unpacked; the copy stops at the limit. At least 1. |
| `Pipeline:MaxUnpackedBytesPerIngestion` | 268435456 (256 MiB) | Inflated bytes one ingestion may unpack across all its archives, nested ones included; once reached, the current archive stops and waiting archives are ignored with a reason. At least 1. |
| `Pipeline:MaxUnpackedItemsPerIngestion` | 500 | Files one ingestion may unpack across all its archives (each can become a document). At least 1. |
| `Pipeline:MaxArchiveCompressionRatio` | 100 | Largest inflated-to-compressed ratio, never below a 1 MiB floor: per entry (a more compressed entry is skipped as a likely zip bomb), per archive (its entries together; the archive stops) and per ingestion (everything unpacked at every level against the archive bytes uploaded; the ingestion's unpack budget runs out). 0 turns all three off; not negative. |
| `Pipeline:MinimumImageBytes` | 20480 (20 KiB) | Smaller images are logos, signatures and tracking pixels and are ignored with a reason. Not negative. |

**Document processing** (`PdfProcessingOptions`, `KeywordClassifierOptions`, `HeuristicSplitterOptions`; both hosts)

| Key | Default | What it does |
|---|---|---|
| `DocumentProcessing:Pdf:TextLayerMinimumWordsPerPage` | 5 | A page has a text layer when it carries more words than this; otherwise it needs OCR. |
| `DocumentProcessing:Pdf:MaximumRenderDpi` | 600 | Upper bound of a page render. |
| `DocumentProcessing:Pdf:MaximumPdfBytes` | 104857600 (100 MiB) | Larger PDFs fail as too large instead of being read into memory. |
| `DocumentProcessing:Pdf:MaximumAttachmentBytes` | 33554432 (32 MiB) | Largest embedded file the e-invoice finder decodes, stored and decoded. |
| `DocumentProcessing:Classifier:ConfidenceThreshold` | 0.6 | Below this the keyword classifier answers `Unknown`. |
| `DocumentProcessing:Classifier:FirstPageHeaderFactor` | 2.0 | Weight of a hit in the top third of the first page. |
| `DocumentProcessing:Classifier:FirstPageFactor` | 1.0 | Weight of a hit elsewhere on the first page. |
| `DocumentProcessing:Classifier:LaterPageFactor` | 0.4 | Weight of a hit on a later page. |
| `DocumentProcessing:Classifier:BodyTitleFactorWhenHeaderTaken` | 0.5 | Extra factor for a body title when another type owns the header. |
| `DocumentProcessing:Classifier:StrongEvidenceScore` | 8.0 | Score that counts as full evidence; weaker winners are scaled down. |
| `DocumentProcessing:Splitter:BlankPageMaxWords` | 2 | A page with at most this many words is a blank separator. |
| `DocumentProcessing:Splitter:StartThreshold` | 2.0 | Net start score from which a page starts a new document. |
| `DocumentProcessing:Splitter:CertainScore` | 3.0 | Net score that counts as a certain decision either way. |
| `DocumentProcessing:Splitter:NoSignalCertainty` | 0.6 | Certainty of a page without any signal (a continuation page). |
| `DocumentProcessing:Splitter:ConfidenceThreshold` | 0.7 | Below this overall confidence the split counts as uncertain and the LLM splitter is asked. |
| `DocumentProcessing:Splitter:MaxConfidentPageCount` | 6 | Above this many pages the LLM splitter is asked regardless. |
| `DocumentProcessing:Splitter:HeaderBandFraction` | 0.12 | Height fraction of the header band compared for "supplier header repeated". |
| `DocumentProcessing:Splitter:HeaderSimilarityThreshold` | 0.5 | Jaccard similarity of header words above which the header counts as repeated. |

The two classification thresholds act one after the other. `DocumentProcessing:Classifier:ConfidenceThreshold`
turns a weak keyword answer into `Unknown`; `Pipeline:LlmClassificationThreshold` decides when stage 4 asks the
LLM. With both at 0.6 the LLM is asked exactly for `Unknown`. Raising the pipeline threshold above the classifier's
also sends weak but typed keyword answers to the LLM, and the keyword answer stands when the LLM returns `Unknown`.
A pipeline threshold below the classifier's changes nothing, because every answer under the classifier's threshold
is already `Unknown`.

**LLM providers** (`LlmReplayOptions`, `LlmExtractionOptions`, `ConfigurationLlmCredentialStore`; both hosts).
Models and providers are not settings: the gateway resolves them per tenant and stage from the database (docs/08).

| Key | Default | What it does |
|---|---|---|
| `Llm:Secrets:<secretRef>` | unset | Provider key for a secret reference; `LLM_SECRET_<SECRET_REF>` works too. |
| `Llm:Replay:Directory` | unset (`fixtures/demo/llm-replay` in Development) | Registers the file replay provider over this fixture folder. |
| `Llm:Replay:Record` | false | Wraps every real adapter so replay misses are recorded into the directory; needs `Llm:Replay:Directory`. |
| `Llm:Replay:Scripted` | false | Serves the Replay kind from the in-memory `ScriptedLlmProvider` (tests). |
| `Llm:Extraction:MaxOutputTokens` | 8192 (the `LlmOptions` default) | Output budget of the first extraction attempt; a truncated answer is retried once with twice the budget. |
| `Llm:Extraction:MaxAuxiliaryOutputTokens` | 1024 | Output budget of the classification and split calls (always at low effort). |
| `Llm:Extraction:Effort` | `Low` | Reasoning effort of the extraction call (docs/06 "Model choice"). |
| `Llm:Extraction:ClassificationTextChars` | 2000 | How much of the text layer the LLM classifier sees. |
| `Llm:Extraction:SplitTextCharsPerPage` | 300 | How much of every page's text the LLM splitter sees. |
| `Llm:Extraction:SplitThumbnailLongEdge` | 768 | Long edge in pixels of the page thumbnails the splitter attaches. |
| `Llm:Extraction:SplitMaxThumbnailPages` | 40 | Most thumbnails the splitter attaches (the first pages); zero attaches none. |

## Apple Silicon specifics

- Enable **"Use Rosetta for x86_64/amd64 emulation"** in Docker Desktop (or Rosetta in
  OrbStack). SQL Server and the Service Bus emulator are the only amd64-only images; everything
  else is multi-arch.
- Build the .NET images multi-arch (`linux/arm64` for local, `linux/amd64` for Azure). The
  native dependencies (PDFium, Tesseract, SkiaSharp) are present for both; CI builds and
  tests the amd64 image so a missing native never reaches Azure unnoticed.
- Allocate at least 8 GB to Docker; SQL Server under emulation is memory-hungry.
- `docker-compose.yml` pins `sqlserver` and `servicebus` to `platform: linux/amd64`, so they run under Rosetta;
  the images built from `src/Dockerfile` and `web/Dockerfile` are built for the host's own architecture.

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
present. Without Docker the container tests report themselves skipped and the run stays green:
`DockerFacts` (`tests/DocProc.Integration.Tests`) counts Docker as present only when a daemon
answers a ping at the endpoint Testcontainers discovers, so the `/var/run/docker.sock` symlink
that Docker Desktop or OrbStack leaves behind after quitting, or a `DOCKER_HOST` of a stopped
machine, does not turn the skips into failures. Finding the endpoint and pinging it are each bounded by five
seconds, so a `DOCKER_HOST` that accepts connections but never answers (a remote host behind a VPN that is off)
skips the tests in seconds rather than after Docker.DotNet's default timeout of minutes. The one ungated test that
builds a Testcontainers builder (`AzuriteSetupTests`, which checks Azurite's start-up arguments without Docker)
waits on the same discovery, because a builder's constructor runs it; it gets the same five seconds and then skips,
so a whole `dotnet test` run against such an endpoint also takes seconds, not minutes
(`AzuriteSetupTimingTests` runs that class in a child process against an endpoint that never answers and holds it
to that). CI's runner has Docker, so there they run.
Azurite is started with `--skipApiVersionCheck`, as the AppHost starts it, because every Azurite
release trails the service version the Blob SDK sends.

Two checks guard the hosts themselves rather than a layer inside them. `tests/DocProc.Worker.Tests`
boots the worker exactly as `src/DocProc.Worker/Program.cs` composes it (`WorkerHost.CreateBuilder`,
settings passed as command-line arguments) on a temporary SQLite file with the scripted LLM
provider, pushes a UBL invoice through the in-process queue until the document is auto-approved,
builds the production-like SQL Server configuration with `ValidateOnBuild` without ever connecting,
and asserts that `Persistence:Provider=Sqlite` and `Persistence:SeedDevData` are refused in
Production (the worker has no `Authentication:Dev`; that refusal is the API's). The CI `web` job
runs `web/scripts/export-openapi.sh` against the freshly built API and fails, printing the diff,
when the committed `web/packages/api-client/openapi/v1.json` or `src/schema.d.ts` differs from
what the API serves: after changing an endpoint, run the script and commit the result. The script
starts the API on port 5199 (`OPENAPI_EXPORT_PORT` moves it) and removes the document's `servers`
entry, which the API fills from that port, so the committed file is the same from any port
(`web/scripts/strip-openapi-servers.mjs`, tested in `packages/api-client/src/openapi.test.ts`). The same script lists
`components.schemas` in ordinal order: ASP.NET sorts them with the culture-sensitive comparer, and the API runs with
ICU ("With Docker Compose", "Globalization"), where the order differs from invariant mode's, so without it the export
would depend on the machine.

## PDF fixtures: fonts, PDFsharp and the artifacts switch

The stage 2–4 tests (`tests/DocProc.Integration.Tests/Pdf`, `.../Classification`) generate
their PDFs at test time with **PDFsharp**; the only committed PDFs are the two demo documents of
`fixtures/demo` ("Demo data" above), whose bytes key the replay fixtures. Three things to know:

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
