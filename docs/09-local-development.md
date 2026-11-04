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
default network and five named volumes (`sql-data`, `azurite-data`, `keycloak-data`, `secrets`, and `outbound`, the
file drop of `blob` destinations that the API and the worker share at `/data/outbound`)
belong to the project, and `down -v` removes them.

**Mock ERP.** `mock-erp` is WireMock 3 with the stub mappings in `docker/mock-erp/mappings`, the
receiving system that the delivery demos of docs/11 §23 send to from P3 on. Both the AppHost and compose run it on host port 8086 with the folder mounted read-only, so a
change to a stub needs a restart of the container (`docker compose restart mock-erp`).

| Request | Answer |
|---|---|
| `POST /api/ap/invoices` (`erp-ap`) with a JSON body | 201 `{"erpId":"AP-<8 digits>","status":"accepted","idempotencyKey":<the Idempotency-Key header>,"receivedAt":...}` |
| `POST /api/ap/invoices` as `multipart/form-data` (a destination's `multipart` parts: the payload and the original file) | 201, the same body with `"withAttachment":true` |
| `POST /api/article-prices` with a JSON body | 202 `{"importId":"AP-IMP-<8 digits>","status":"queued",...}` |
| `PUT /api/article-prices/<priceListId>` with a JSON body (a re-approval under `OnReapproval: Update`) | 200 `{"importId":"AP-UPD-<8 digits>","status":"updated","priceListId":<from the path>,...}`; with `X-Mock-Erp-Fail: 503` a 503 |
| `POST /oauth/token` with `grant_type=client_credentials` | 200 `{"access_token":"mock-erp-token","token_type":"Bearer","expires_in":3600}` (any client id and secret), for `oauth2ClientCredentials` destinations |
| either endpoint with another content type | 415 |
| either endpoint with the header `X-Mock-Erp-Fail: 503` | 503 with `Retry-After: 1`, that request only |
| `POST /__control/<erp-ap\|article-prices>/fail-next` | the next call of that endpoint answers 503 (`Retry-After: 1`), the one after succeeds again: the "forced 503, retried, then success" of the P3 demo |
| `POST /__control/<erp-ap\|article-prices>/fail-always`, `.../reset` | every call answers 503 until the reset |
| `GET /__admin/requests` | WireMock's journal: every request it received, with headers and body (`DELETE` clears it) |

The failure modes are WireMock scenarios named after the endpoint, so `POST /__admin/scenarios/reset`
resets both. `ComposeParityTests` holds the compose service to the AppHost (image, arguments, mount,
port) and checks that every endpoint has its success and 503 stubs; `MockErpStubTests`
(`tests/DocProc.Integration.Tests`, Docker) runs the compose file's image over the folder and
exercises each answer. The http transport sends through `TenantEndpointGuard`, which accepts only
https to public hosts; `Delivery:DevelopmentHosts` (honoured in the Development environment only) lets a tenant
destination reach `http://mock-erp:8080` in compose and `http://localhost:8086` under the AppHost, which set it to
those (docs/11 §13.3).

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

**Map mode and the table tool (docs/11 §12.3, §12.4, P2).** The server side of capturing values by pointing at
the page; the review screen's map mode and table tool call them ("Demo: manual mapping" below). Boxes are in page
units (0 to 1000, top left origin):

```bash
# The words under a box, their text and its readings (a date, an amount, ...), in the tenant's locale.
curl -s "http://localhost:5100/api/v1/documents/$DOC/pages/1/words?bbox=600,120,800,140" -H "$H" | jq '{text, candidates}'
# Propose the columns of a table drawn around its header and first rows on page 1 (fields named from the headers).
COLUMNS=$(curl -s -X POST "http://localhost:5100/api/v1/documents/$DOC/regions:table" -H "$H" -H 'content-type: application/json' \
  -d '{"page":1,"bBox":{"x0":40,"y0":95,"x1":995,"y1":300}}' | jq -c .columns)
# Apply that mapping to every page and write the rows (without "write" it is a preview with the same report).
curl -s -X POST "http://localhost:5100/api/v1/documents/$DOC/regions:table" -H "$H" -H 'content-type: application/json' \
  -d "$(jq -n --argjson cols "$COLUMNS" '{page:1,bBox:{x0:40,y0:95,x1:995,y1:940},headerRow:{y0:99,y1:111},columns:$cols,pages:"1-*",groupHeader:{target:"category"},write:true}')" \
  | jq '.report | {rows, rejectedRows, pagesWithoutRows}'
curl -s "http://localhost:5100/api/v1/documents/$DOC/table-reports" -H "$H" | jq '.[0].report.perPage[] | {page, rows, rejected}'
```

`regions:read` (region OCR) answers `501 OCR_NOT_CONFIGURED`: no OCR provider is registered in either host yet, so
a scanned page without a text layer has no words (`source: none`).

With `configurableTypes` on, `:approve` refuses an Unknown document, one whose type has no published
definition, and one with no data: no tree, or a tree none of whose fields holds a value, as `:set-type` with prefill
none leaves it (`409 Cannot approve` with `code`), and the review screen shows the reason under a disabled Approve
button (`DocumentDetail.approvalBlocked`); a tenant without the switch approves as before. The review screen offers
the tools of P2 (Map this document, the More menu with set type, supplier, split and join, map mode and the table
tool) only with `configurableTypes` on (`DocumentDetail.configurableTypes`); a tenant without it reviews as before
P2, while the endpoints answer API clients of any tenant. A part joined into another document is `Cancelled` (status filter `Cancelled`, shown only under "All").

### Outputs

Output mappings, destinations, bindings and lookup tables (docs/11 §13, P3) need the tenant's `outputs` switch
(admin console: Tenants > tenant > Settings > Feature switches > Outputs) and IntegrationAdmin (a tenant admin;
lookup tables ConfigAdmin). In the portal they live under Settings > Integrations (destinations, output mappings with
the visual field map, code mode and the server-rendered preview, bindings, lookup tables, the delivery log), and a
document's deliveries under its Deliveries tab; "Demo: outputs and delivery" below walks through them. The preview
renders a mapping over a document's values view on the server. Renders run in renderer child processes
(`DocProc.Outputs.Renderer.dll`, built next to each host; settings under "Outputs" below); the first render of a host
starts one, which takes about half a second. The platform templates `std.event` and `generic-ap-json` are seeded on
every start (and by the `OutputsAndLookups` migration).

```bash
TOKEN=$(curl -s -X POST http://localhost:5100/dev/token -H 'content-type: application/json' -d '{"username":"tenant-a-admin"}' | jq -r .access_token)
H="authorization: Bearer $TOKEN"
curl -s http://localhost:5100/api/v1/settings -H "$H" | jq '.settings | .features.outputs = true' \
  | curl -s -X PUT http://localhost:5100/api/v1/settings -H "$H" -H 'content-type: application/json' -d @- | jq .settings.features
curl -s http://localhost:5100/api/v1/output-templates -H "$H" | jq '.[].key'
# Render generic-ap-json over an approved document (any document id of the tenant), without delivering.
VERSION=$(curl -s http://localhost:5100/api/v1/output-mappings/generic-ap-json -H "$H" | jq -r '.versions[0].id')
curl -s -X POST http://localhost:5100/api/v1/output-mappings:preview -H "$H" -H 'content-type: application/json' \
  -d "{\"mappingVersionId\":\"$VERSION\",\"documentId\":\"$DOC\"}" | jq '{output, errors, schemaErrors}'
# Or an expression of your own, with the values view back for the source tree.
curl -s -X POST http://localhost:5100/api/v1/output-mappings:preview -H "$H" -H 'content-type: application/json' \
  -d "{\"expression\":\"{ \\\"no\\\": roles.documentNumber, \\\"day\\\": \$fmtDate(roles.documentDate, \\\"dd.MM.yyyy\\\") }\",\"documentId\":\"$DOC\",\"includeValues\":true}" | jq '{output, values: .values.roles}'
# The ERP vendor number mappings read as supplier.attributes.erpVendorId.
curl -s -X PATCH "http://localhost:5100/api/v1/suppliers/$SUPPLIER" -H "$H" -H 'content-type: application/json' -d '{"attributes":{"erpVendorId":"V-10042"}}' | jq .attributes
```

A render that takes more than 2 s of processor time, allocates past its 1 GB heap or writes more than 5 MB fails with
`timeLimit`, `memoryLimit` or `sizeLimit` in `errors`, and the host starts a fresh renderer for the next one. An
output nesting objects and arrays more than 48 levels deep, and an expression nesting brackets or minus signs more than
1000 deep, fail with `depthLimit`; an error message (`$error` of a long text) is cut to 2000 characters. A renderer
that stops while it runs an expression is tried once more on a fresh process, then the render fails with `depthLimit`
(`CRASH`), and the host logs the first 20 lines of the renderer's standard error. Only a renderer that cannot be
started or cannot take the request is `unavailable`, the one error a delivery's publish stage retries.

**Delivery (P3 part B).** With the switch on, every approval, auto-approval and rejection the publish stage hands on
is fanned out to the tenant's bindings (docs/11 §13.5): each binding whose scope and condition hold renders its
mapping now, the snapshot goes to the `exports` container, and the delivery is sent by the runner of the host that
published (`ProcessDelivery`), retried with backoff on 408, 425, 429, 5xx and network errors (the receiver's
`Retry-After` replaces the wait), and dead-lettered with a `DELIVERY_FAILED` flag after 15 attempts, 72 h or any
other 4xx. The document's timeline shows `document.delivery_succeeded`, `document.delivery_failed` and
`document.delivery_skipped`; `GET /api/v1/deliveries?documentId=` lists them with their attempts. The delivery
sweeper sends what a crash left behind (`Delivery:SweepInterval`, `Delivery:SweepGrace`). Against the mock ERP of the
compose stack, with `$H` holding a tenant admin's Keycloak token as in "Demo: types as data" below (under the AppHost
the destination URL is `http://localhost:8086/...`; a plain `dotnet run` has no mock ERP) and `$DOC` an approved
invoice of the tenant (an e-invoice XML uploaded to `POST /api/v1/ingestions` is auto-approved):

```bash
# A destination at mock-erp (plain http, allowed by Delivery:DevelopmentHosts), the copy of generic-ap-json, a binding.
DEST=$(curl -s -X POST http://localhost:5100/api/v1/destinations -H "$H" -H 'content-type: application/json' \
  -d '{"name":"erp-ap","transport":"http","config":{"urlTemplate":"http://mock-erp:8080/api/ap/invoices","responseMapping":"erpId"}}' | jq -r .id)
curl -s -X POST http://localhost:5100/api/v1/output-mappings -H "$H" -H 'content-type: application/json' -d '{"from":{"template":"generic-ap-json"},"key":"erp-ap-import"}' > /dev/null
curl -s -X POST http://localhost:5100/api/v1/output-mappings/erp-ap-import/versions/1:publish -H "$H" -H 'content-type: application/json' -d '{}' > /dev/null
curl -s -X POST http://localhost:5100/api/v1/output-bindings -H "$H" -H 'content-type: application/json' \
  -d "{\"name\":\"ap-invoices\",\"documentTypeKey\":\"Invoice\",\"mappingKey\":\"erp-ap-import\",\"destinationId\":\"$DEST\"}" | jq .id
# Make the next call fail once, approve an invoice in review (or wait for an auto-approval), and watch it retry.
curl -s -X POST http://localhost:8086/__control/erp-ap/fail-next
curl -s "http://localhost:5100/api/v1/deliveries?documentId=$DOC" -H "$H" | jq '.items[] | {status, attempts, lastStatusCode, lastError}'
curl -s http://localhost:8086/__admin/requests | jq '.requests[] | {url: .request.url, key: .request.headers["Idempotency-Key"]}'
# Send a delivery again: from its snapshot, or rendered again (mode rerender, optionally mappingVersion).
curl -s -X POST "http://localhost:5100/api/v1/deliveries/$DELIVERY:redeliver" -H "$H" -H 'content-type: application/json' -d '{"mode":"snapshot"}' | jq .
```

A `blob` destination (`{"path":"ap","fileNameTemplate":"{{meta.documentId}}-{{meta.version}}.json"}`) writes below
`Delivery:DropRoot/<tenantId>/` (`/data/outbound` in compose: `docker compose exec worker ls -R /data/outbound`). A
`pull` destination keeps its deliveries in a feed that a receiving system polls with an API key of scope
`deliveries:read` (optionally restricted to the destination): `GET /api/v1/feeds/<id>?after=<sequence>` and
`POST /api/v1/feeds/<id>:ack {"upTo": <sequence>}` (docs/05).

### Source routing (mailboxes, watched folders, source rules)

docs/11 §7 (P4 part A). Everything here needs the tenant's `sourceRouting` switch (Settings → features, or
`PUT /api/v1/settings` with `features.sourceRouting: true`); a tenant without it is never polled and ingests as before.

- **Mailbox channels.** The worker reads each active `Mailbox` channel every `Intake:PollInterval` (30 s). In compose and
  under the AppHost the platform server is GreenMail (`Mail:ImapHost`), so a channel needs only its address:
  `POST /api/v1/channels {"name":"Rechnungen","type":"Mailbox","config":{"address":"invoices@tenant-a.local","trustedForwarderDomains":["bureau.example"]}}`
  (GreenMail accepts any password; a real server takes the channel's write-only `secret`). Send it a mail through
  GreenMail's SMTP port 3025, for example `curl smtp://localhost:3025 --mail-from rechnungsversand@nordwind.example
  --mail-rcpt invoices@tenant-a.local --upload-file rechnung.eml`. Each message is ingested once (the channel's cursor
  is `{UIDVALIDITY}:{UID}`, and the message id is unique per channel); the .eml is the raw artefact, its attachments
  the items. The ingestion records the sender, recipients, mailbox, subject and the `Authentication-Results` the
  receiving server wrote (GreenMail writes none, so put one in the .eml to try the DMARC rules: `Authentication-Results:
  mx.tenant-a.local; dmarc=pass header.from=nordwind.example`); a forward from a trusted forwarder domain is unwrapped
  to its original sender. A poll that fails names the reason in the channel's `lastError`.
- **Watched folders.** The worker scans `Intake:FolderRoot/<tenant slug>/<channel path>/` (in compose
  `./fixtures/dropzone/tenant-a/...`, mounted at `/data/dropzone`): `POST /api/v1/channels
  {"name":"Scanner","type":"WatchedFolder","config":{"path":"scanner","stableSeconds":5}}`, then copy a PDF to
  `fixtures/dropzone/tenant-a/scanner/lieferanten/nordwind/`. A file is taken once its size and time stayed the same
  for `stableSeconds` (two scans at least), recorded with its folder path relative to the channel root
  (`lieferanten/nordwind/`, the values view's `source.folderPath`) and moved to `processed/` (or `failed/`) below the
  channel root. Hidden files, `.part`/`.tmp` files and links are skipped. The container's user must be able to write
  the folder (`chmod -R a+rwX fixtures/dropzone` on Linux).
- **Source rules.** `POST /api/v1/suppliers/{id}/source-rules` with a sender, sender domain, recipient, channel and
  folder prefix, subject or file-name expression; `POST /api/v1/routing:test` shows what a source would get and why.
  Documents are created with the supplier (and a pinned type) at ingest; the Match stage corroborates it with the VAT id
  and IBAN (`SUPPLIER_CONFLICT` for an `Exclusive` rule, the content's supplier for a default). Sender rules need DMARC
  (or SPF with an aligned DKIM signature) unless the rule says `requireSenderAuth: false`.

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
stage sends (the PDF for extraction, the rendered first page PNG for classification, page thumbnails for the LLM
splitter).
`DemoFixtureTests.Llm_replay_fixtures_answer_exactly_the_requests_the_demo_pdfs_produce` boots the processing
stack with the API's `appsettings.json` and `appsettings.Development.json`, lets the demo data run with a capturing
provider in place of the replay store, hashes every request the real stages send and compares the resulting files
with the committed ones. It then forks tenant A's `Invoice` with the demo custom field (`fixtures/demo/types`, see
"Document types" above) and reprocesses the PDF invoice with `configMode: "current"`, which adds the third fixture:
the extraction with `x_custom`. Last it forks `CreditNote` with the field of docs/11 §5.3
(`fixtures/demo/types/credit-note-supplier-customer-ref.field.json`) and uploads the credit note of
`fixtures/demo/uploads/tenant-a`, which adds the fourth: that credit note's extraction with `x_custom` ("Demo: types
as data" below). Then it adopts `std.price-list` and uploads the Nordwind price list of the same folder, which adds
the fifth and sixth: its split (14 pages are too many for the heuristic splitter) and its classification, both
answered so that it arrives as one document of type `Other` ("Demo: manual mapping" below). After a change to a prompt, a schema, the LLM options, the models, the page rendering or a
demo PDF it fails and says so; regenerate and commit:

```bash
UPDATE_DEMO_FIXTURES=1 dotnet test --project tests/DocProc.Pipeline.Tests -c Release -- --filter-class DocProc.Pipeline.Tests.Demo.DemoFixtureTests
git add fixtures/demo/llm-replay
```

The four PDFs (the two of `tenant-a`, the credit note and the price list of `uploads/tenant-a`) are committed bytes,
drawn once by `tests/DocProc.Pipeline.Tests/Pdf/DemoPdfs.cs` and `DemoPriceList.cs` with PDFsharp (test-only, see
below). PDFsharp writes a new document
id on every save, so the normal run never rewrites them; after editing `DemoPdfs`, run once with
`REGENERATE_DEMO_PDFS=1` (every PDF) or `REGENERATE_DEMO_PDFS=<file name>` (that one only, so the others' bytes and
fixtures stay as they are) and then with `UPDATE_DEMO_FIXTURES=1`. To add a demo document, drop the file into the
tenant folder (or into `uploads/<tenant slug>` for one a demo uploads by hand: the demo data does not ingest it);
if it is a PDF that needs an LLM call, add its answer to `DemoAnswers` (and its expected request to the fixture
test) and regenerate. `DemoPdfClassificationTests` holds which demo PDFs the keyword rules classify.

The classification and split hashes cover the PNGs that PDFium renders. The committed fixtures are generated on linux-x64 (the
CI platform, `DemoData.FixtureCapturePlatform`); a machine whose PDFium build rasterises the page differently gets a
replay miss for the delivery note and the price list, which then end `Failed`, and the API logs a warning saying so at start-up. On any
platform other than linux-x64 (`RuntimeInformation`: Linux and X64), `DemoFixtureTests` compares only the extraction
fixtures, which hash the PDF bytes and are platform-independent, `UPDATE_DEMO_FIXTURES=1` rewrites only those, and
`DemoDataTests` skips the delivery-note assertions; both tests then report themselves skipped with this explanation.
Do not commit a classification or split fixture regenerated on such a machine. `fixtures/demo/.gitattributes` (`* -text`)
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

### Demo: manual mapping

The P2 demo of docs/11 §23, in the browser: a 14-page Nordwind price list the system does not recognise is given its
type in review, its header values are captured by pointing at the page, and its 412 rows by one table mapping drawn
on page 1 and applied to all pages. Run it on a fresh compose stack (`docker compose up --build`; no LLM key needed),
portal on http://localhost:5173, admin console on http://localhost:5174.

The file is `fixtures/demo/uploads/tenant-a/Nordwind_Preisliste_2027.pdf` (drawn by
`tests/DocProc.Pipeline.Tests/Pdf/DemoPriceList.cs`, see "Demo data"): 14 A4 pages, 412 articles in nine groups
("Weißweine" … "Feinkost"), the column header Art.-Nr. / Bezeichnung / Gebinde / Preis / ab 10 / ab 50 repeated under a
running header on every page, eleven continuation lines under a row, two rows continued at the top of the next page,
tier prices in some rows only, and "gültig ab 01.01.2027", "Listen-Nr. PL-2027-01" and "Alle Preise in EUR" on page 1.
Nordwind is not in the demo master data; the demo creates it from the document (step 5).

1. **Switch on and adopt** (as in "Demo: types as data", steps 2 and 3). As `superadmin` in the admin console: Tenants >
   Gasthaus zur Post GmbH > Settings > Feature switches: Configurable document types on. As `tenant-a-admin` in the
   portal: Settings > Document types > Templates > Price list > Adopt ("price-list adopted.").
2. **Upload.** As `tenant-a-reviewer`: Inbox > Upload > the price list > Upload 1 file. It costs two LLM calls, both
   answered by replay fixtures: 14 pages are more than the heuristic splitter decides alone (`MaxConfidentPageCount`,
   6), so the LLM splitter is asked and keeps the pages together; no keyword rule knows a price list, so the LLM
   classifier is asked, whose types have none either. The document arrives as `Other`, not `Unknown` (the correction
   in docs/11 §23), in Needs review with `UNSUPPORTED_TYPE` and `SUPPLIER_UNMATCHED`; Approve is disabled with
   "Nothing was captured yet: none of the fields holds a value. Fill the fields before approving."
3. **Map this document.** Open it from the inbox ("(no number)", type Other). The Fields tab shows the Map this
   document panel: Type = Price list, Start with = Empty, Set type. The form of Price list v1 appears with every field
   empty, the blocking `FIELD_REQUIRED` flags of Valid from, Supplier and Currency, and map mode on (Map in the page
   viewer's toolbar; M switches it on and off). Approve stays disabled with the same note: a form whose fields are all
   empty is no data (docs/11 §11), until the first value is saved in step 4.
4. **Header values from the page.** Click Valid from in the form, then click "01.01.2027" on the page: the preview above
   the page reads `“01.01.2027” → 2027-01-01` (editable); Enter assigns it and moves to the next empty required field,
   Tab skips, Esc drops the selection. Likewise Price list no. ← "PL-2027-01" (click it first: it is not required) and
   Currency ← "EUR"; Enter after Currency moves on to Supplier > Name (the supplier party is required as a whole): drag
   a box around "Nordwind Großhandel GmbH" in the letterhead (a box reads every word in it, in reading order;
   shift-click adds words one by one). Save corrections: each value is Corrected by the reviewer with its page and box,
   drawn on the page, and the three required flags resolve.
5. **Supplier.** More > Create supplier from this document: the dialog is filled from the supplier party
   (Nordwind Großhandel GmbH); Create and assign. `SUPPLIER_UNMATCHED` resolves and the header names the supplier.
   More > Assign supplier picks an existing one instead. A reviewer gives the name and the identifiers printed on the
   document (VAT id, IBAN); the dialog says that e-mail domains and aliases are added by a tenant admin under
   Suppliers, and offers the e-mail domain only to `tenant-a-admin` (the API answers 403 to a reviewer who sends one).
6. **The table, once.** Click Table in the toolbar (the Table tab opens) and drag a box on page 1 from just above and
   left of "Art.-Nr." down to just above the footer rule and across to the right of "ab 50". The proposal names the
   columns Article no., Description, Pack as printed, Price, Tier price from 10 and Tier price from 50 and draws the
   dividers on the page (drag one to move it, or move it with the arrow keys; double-click inside the box to add one;
   the merge button beside a column removes the divider before it). "Apply to all pages" and "Group headings fill the
   category" are ticked, and the proposal reads page 1 with them: `Proposal (page 1 only): 22 rows`, the heading
   "Weißweine" listed under "Group headings on page 1". Read all pages: the page strip shows `412 rows, 0 rejected, 14
   pages read`, p. 1: 22 rows and p. 2 to p. 14: 30 rows each; a click on a page shows it with the dividers measured
   again at its header. Write 412 rows: one correction ("412 rows written as one correction."), the rows in the grid
   "Articles · 412 rows" (100 a page) with their category and tier prices, and the report kept as the Table tab's page
   strip (also after a reload).
   A box drawn down to the foot of the page holds the footer of every page. It is printed at the same place on all 14
   pages, so it is left out by itself: still `412 rows, 0 rejected`, and each page says "2 lines printed at the same
   place on most pages (running titles, page numbers, footers) were left out." and lists them under "Left out on page
   n". A group heading that opens most pages for another category than the rows above it is read as a heading, not
   left out. Lines inside the box that are not rows and do not repeat go under "Lines that are not rows" with their reason; "Leave out lines like …" beside a rejected
   line (or beside a heading under "Group headings on page n", where a title printed on one page only shows up) adds a
   skip line for that kind of line only, so a footer of two different lines needs two clicks; typing the start of a
   line under "Leave out lines that start with" (a number stands for any number) does the same, and "On the other
   pages, read the part of the page from … to … %" cuts off running titles and footers by position instead. A box
   drawn only around the header row reads every page down to its foot (or the bottom margin). Choosing "(leave out)"
   for a tier column leaves its prices out of the rows, and its words out of every decision.
   A reprocess from Classify (Reprocess, From stage = Classify) keeps the type set in review, and with it the rows,
   and raises no `UNSUPPORTED_TYPE` again (the reviewer set the type and fills it).
7. **Approve by hand.** Approve: no open flag is left, and the document is Approved. Price lists are always reviewed in
   this phase (`review.autoApprove: false`, docs/11 §5.8).

The same through the API (`$H` as under "Document types", `$DOC` the price list): `POST $DOC:set-type
{"typeKey":"price-list"}`, `PATCH $DOC/fields` with a `source` on each correction, `POST $DOC/regions:table` with the box
`{"x0":45,"y0":280,"x1":945,"y1":935}` on page 1 (the proposal), then again with its `columns`, `headerRow` and
`rowAnchor`, `pages: "1-*"`, `groupHeader: {"target":"category"}` and `write: true`. `DemoPriceListTableTests`
(`tests/DocProc.Api.Tests`) runs this on the committed PDF and checks the 412 rows; `DemoFixtureTests` holds the upload
(the split and classification fixtures, one document of type Other in review); the portal's `ReviewScreen.mapping`,
`ReviewTools` and `FieldHighlightOverlay.selection` tests and the review package's `MappingTools`, `mapMode` and
`tableTool` tests hold the screens.

The other review actions of P2 work on the demo data as well. More > Split into documents on the two-page invoice
2026-131, with page 2 starting a document: the parts are read again and end `Failed` on a replay miss (their pages have
no fixture); then More > Join parts on page 1 joins page 2 back, and the document is the whole file again, read from its
fixture as before, as long as no other Bio-Hof Müller document is approved. Once one is (the e-invoice 2026-118, or
the delivery note of "Demo: types as data", step 7), the extraction carries that supplier's last approved document as
hints, its request no longer matches the fixture and the joined invoice ends `Failed` on a replay miss too; do the
split and join first, or on a fresh stack (`DemoFixtureTests` holds both). A value typed into the wrong field of the delivery note moves to the right one when the handle beside
its label is dragged onto that field (or with the arrows button beside it, "Move value to"): one `move` correction.
Region OCR (a box without words, on a scan) says "No OCR is configured on this server": no OCR provider ships yet
(docs/07).

### Demo: outputs and delivery

The P3 demo of docs/11 §23, in the browser: the Nordwind price list of "Demo: manual mapping" is rendered by an
`article-price-import` mapping built in the visual field map of Settings > Integrations (the mapping of docs/11
§13.10), previewed on the server (412 rows, tier prices as scales), bound to price lists with a destination at the
mock ERP's `POST /api/article-prices`, and delivered on approval; a forced 503 is retried and then succeeds, while
invoices keep going to `erp-ap`. Run it on a fresh compose stack (`docker compose up --build`), portal on
http://localhost:5173, admin console on http://localhost:5174, the mock ERP's journal on
http://localhost:8086/__admin/requests.

1. **Switch on.** As `superadmin` in the admin console: Tenants > Gasthaus zur Post GmbH > Settings > Feature
   switches: Configurable document types on, and Outputs on ("Feature switches saved."). Without Outputs the portal's
   Settings > Integrations says "Outputs are off for this tenant", the review screen has no Deliveries tab, and
   approving publishes exactly as before (nothing is rendered or sent).
2. **The price list, mapped but not yet approved.** Steps 1 to 6 of "Demo: manual mapping" (adopt Price list, upload
   `Nordwind_Preisliste_2027.pdf`, set the type, map Valid from, Price list no., Currency and the supplier's name,
   create the supplier from the document, draw the table once: `412 rows, 0 rejected, 14 pages read`, write them).
   Leave it in Needs review: an approval is delivered to the bindings that exist when it is published.
3. **The supplier's ERP vendor number.** As `tenant-a-admin`: Suppliers > Nordwind Großhandel GmbH > Attributes:
   `erpVendorId = V-10042` (one `key = value` per line; mappings read them as `supplier.attributes`) > Save. The
   mapping below refuses to render without it ("Supplier has no ERP vendor id").
4. **A lookup table.** Settings > Integrations > Lookup tables > New table `erp-uom` > entries `KAR → KAR`,
   `ST → ST`, `PCK → PCK`, `Karton → KAR` > Save ("4 entries saved."). `$table("erp-uom", unit, "ST")` reads it; a
   table that does not exist is a render error, not the default.
5. **Destinations.** Integrations > Destinations > New destination: `erp-articles`, HTTP (JSON), POST,
   URL `http://mock-erp:8080/api/article-prices`, response mapping `importId`, update URL
   `http://mock-erp:8080/api/article-prices/{{output.header.priceListId}}` (method default PUT) > New destination; and
   `erp-ap`, POST `http://mock-erp:8080/api/ap/invoices`, response mapping `erpId`. The response mapping is compiled
   when the destination is saved: one that is not JSONata is refused under the field. Plain http to `mock-erp` is allowed only by
   the Development setting `Delivery:DevelopmentHosts` (compose sets it); anywhere else the URL must be https and public.
6. **The invoice mapping.** Output mappings > Integration templates > Generic AP JSON > Copy, key `erp-ap-import` >
   Copy; the editor opens on draft v1 in code mode (the template is written in code) > Publish ("Version 1
   published.").
7. **The article-price mapping, in the visual map.** Output mappings > New mapping: key `article-price-import`, name
   "Article price import", document type `price-list` > New mapping. The editor has three panes: the values view of the
   preview document (the Nordwind list is chosen; `doc`, `roles`, `supplier`, `entity`, `source`, `meta`, `links`, with
   `doc.lines · 412 rows`), the visual map, and the server-rendered preview. Add field rows top to bottom as the table
   lists them, in the fixture's order, so the compiled code is the fixture property for property (Target field ←
   Source; the gear opens the options: when empty use another field, lookup table and its default, format, constant
   when empty, required with a message; a click on a field in the source tree fills the row being edited, relative to
   the list it is in):

   | Target | Kind | Source | Options |
   |---|---|---|---|
   | `header.vendorNo` | Field | `supplier.attributes.erpVendorId` | Required: `Supplier has no ERP vendor id` |
   | `header.priceListId` | Expression | `roles.documentNumber ? roles.documentNumber : supplier.attributes.erpVendorId & "-" & roles.validFrom` | |
   | `header.validFrom` | Field | `roles.validFrom` | Format Date `yyyyMMdd` |
   | `header.validTo` | Field | `roles.validUntil` | Format Date `yyyyMMdd`, when empty a constant `99991231` |
   | `header.currency` | Field | `roles.currency` | |
   | `header.priceType` | Expression | `doc.priceBasis = "Gross" ? "B" : "N"` | |
   | `header.mode` | Expression | `doc.listScope = "Changes" ? "UPSERT" : "REPLACE"` | |
   | `header.dmsId`, `header.revision` | Field | `meta.documentId`, `meta.version` | |
   | `prices` | List | `doc.lines`, only rows where `$not(status = "Discontinued")` | its rows below |
   | `vendorArticleNo`, `ean` | Field | `supplierSku`, `gtin` | |
   | `text` | Field | `description` | Format Shorten text, 40 |
   | `group` | Field | `category` | |
   | `orderUnit` | Field | `unit` | Lookup `erp-uom`, else `ST` |
   | `contentQty` | Field | `packSize` | |
   | `contentUnit` | Field | `packUnit` | Lookup `erp-uom`, else `ST` |
   | `priceUnit` | Field | `priceQuantity` | When empty a constant, Number `1` |
   | `price` | Field | `price.amount` | |
   | `validFrom` | Field | `lineValidFrom` | When empty use `$root.roles.validFrom`, Format Date `yyyyMMdd` |
   | `scales` (in `prices`) | List | `tiers` | rows `fromQty ← minQuantity`, `price ← price.amount` |
   | `delist` | List | `doc.lines`, only rows where `status = "Discontinued"` | one row with an empty target ← `supplierSku` (a list of plain values) |

   The preview renders as you type (and at once with Render again): `Renders`, about 96 KB, `prices: 412 items`,
   `prices[].scales: 378 items in 309 rows` (the "ab 10" and "ab 50" columns), `delist: 0 items`, and the JSON:
   `"vendorNo": "V-10042", "priceListId": "PL-2027-01", "validFrom": "20270101", "validTo": "99991231", …` and per
   article `"vendorArticleNo": "104007", "text": "Riesling trocken 2023", "group": "Weißweine", "orderUnit": "ST",
   "priceUnit": 1, "price": 9.38, "scales": [{"fromQty": 10, "price": 8.91}, {"fromQty": 50, "price": 8.44}]`. The
   table tool did not read GTINs, units or pack sizes from this list, so `ean` and `contentQty` are `null` and the
   units take the lookup's default. Code (JSONata) shows what the map compiles to: the expression of docs/11 §13.10
   (`fixtures/outputs/article-price-import.fieldmap.jsonata`), headed by the map as a comment; editing the code
   detaches it from the map (the Visual map tab then offers to replace the code with the map). A target schema (Target
   schema tab) is checked on every preview and delivery; its errors name the row (`prices[379].price`). Save draft
   ("Draft v1 saved."), then Publish ("Version 1 published."); a published version is read-only, New version starts v2.
8. **Bindings.** Integrations > Bindings > New binding: `article-prices`, when Approved, document type `price-list`,
   output mapping Article price import, version Published, destination `erp-articles`, on re-approval Send as update
   with the same mapping > New binding; and `ap-invoices`, type `Invoice`, mapping `erp-ap-import`, destination `erp-ap`.
9. **A 503, then approve.** Make the article-price stub fail once:
   `curl -X POST http://localhost:8086/__control/article-prices/fail-next`. As `tenant-a-reviewer`, open the price list
   and Approve. The Deliveries tab lists `erp-articles · article-prices · v1`, Delivered after 2 attempts. The retry
   waits only about a second (the stub's `Retry-After: 1`), and the tab reads every 1.5 s while its list is empty and
   every 3 s while a delivery is in flight, so it usually goes straight from an empty list (or Pending) to Delivered;
   Retry pending (last answer 503) shows only when a read lands in that second (for a screenshot of it, use
   `__control/article-prices/fail-always`, and `__control/article-prices/reset` afterwards: the next retry delivers).
   This holds with the tab open before the approval too: the decision makes the tab read again, and it keeps reading
   an empty list for a minute after the decision, until the worker's publish stage has fanned out. Open the row: the
   idempotency key (the same for both calls), the external id `AP-IMP-…` (Accepted, from the response mapping), the attempts
   `503 Will be retried · status` and `202 Delivered` with the first 4 KB of each answer, Show payload, and for an
   integration admin Redeliver (from the snapshot) and Render again and redeliver. The Timeline shows
   `document.delivery_failed` then `document.delivery_succeeded`.
10. **What the mock ERP received.** Two `POST /api/article-prices` with one idempotency key and
    `X-DocProc-Revision`, a 503 and a 202, each body with 412 prices:

    ```bash
    curl -s http://localhost:8086/__admin/requests | jq '[.requests[] | select(.request.url == "/api/article-prices")
      | {status: .responseDefinition.status, key: .request.headers["Idempotency-Key"],
         prices: (.request.body | fromjson | .prices | length),
         scales: (.request.body | fromjson | [.prices[].scales[]] | length)}]'
    ```
11. **Invoices still go to erp-ap.** Upload an e-invoice (a copy of `fixtures/demo/tenant-a/Getraenke-Hoffmann_RE-2026-55318.xml`
    with another invoice number, so it is no duplicate) through Inbox > Upload or `POST /api/v1/ingestions`: it is
    auto-approved and delivered by `ap-invoices` to `erp-ap` (`POST /api/ap/invoices`, 201); the price-list binding does
    not match it, the invoice binding did not match the price list. Settings > Integrations > Delivery log lists both,
    with filters by status and destination.

The same through the API, with `$H` a tenant admin's token and `$DOC` the price list after steps 1 and 2 (the UI
calls exactly these; steps 3, 4, 5, 7, 8 and 9 in that order, without the invoice side of steps 5, 6 and 11):

```bash
API=http://localhost:5100/api/v1; J='content-type: application/json'
# Step 3: the supplier's ERP vendor number (the mapping refuses to render without it).
SUPPLIER=$(curl -s $API/documents/$DOC -H "$H" | jq -r .supplierId)
curl -s -X PATCH $API/suppliers/$SUPPLIER -H "$H" -H "$J" -d '{"attributes":{"erpVendorId":"V-10042"}}' | jq .attributes
# Step 4: the lookup table with its four entries.
curl -s -X POST $API/lookup-tables -H "$H" -H "$J" -d '{"key":"erp-uom"}' > /dev/null
REV=$(curl -s -D - -o /dev/null $API/lookup-tables/erp-uom -H "$H" | tr -d '\r' | awk -F': ' 'tolower($1)=="etag"{print $2}')
curl -s -X PUT $API/lookup-tables/erp-uom/entries -H "$H" -H "$J" -H "If-Match: $REV" \
  -d '{"entries":[{"from":"KAR","to":"KAR"},{"from":"ST","to":"ST"},{"from":"PCK","to":"PCK"},{"from":"Karton","to":"KAR"}]}' | jq .entryCount
# Step 5: the destination, with the update URL a re-approval is sent to.
DEST=$(curl -s -X POST $API/destinations -H "$H" -H "$J" -d '{"name":"erp-articles","transport":"http","config":{"urlTemplate":"http://mock-erp:8080/api/article-prices","responseMapping":"importId","updateUrlTemplate":"http://mock-erp:8080/api/article-prices/{{output.header.priceListId}}"}}' | jq -r .id)
# Step 7: the compiled visual map (or any JSONata of your own) as draft v1, previewed (no errors, 412 prices), published.
jq -n --rawfile e fixtures/outputs/article-price-import.fieldmap.jsonata '{key:"article-price-import",name:"Article price import",sourceTypeKey:"price-list",engine:"jsonata",expression:$e}' \
  | curl -s -X POST $API/output-mappings -H "$H" -H "$J" -d @- | jq .key
jq -n --rawfile e fixtures/outputs/article-price-import.fieldmap.jsonata --arg d "$DOC" '{expression:$e,documentId:$d}' \
  | curl -s -X POST $API/output-mappings:preview -H "$H" -H "$J" -d @- | jq '{errors, n: (.output.prices | length), outputBytes}'
curl -s -X POST $API/output-mappings/article-price-import/versions/1:publish -H "$H" -H "$J" -d '{}' | jq .publishedVersion
# Step 8: the binding; a re-approval is sent as an update, rendered with the same mapping.
curl -s -X POST $API/output-bindings -H "$H" -H "$J" -d "{\"name\":\"article-prices\",\"documentTypeKey\":\"price-list\",\"mappingKey\":\"article-price-import\",\"destinationId\":\"$DEST\",\"onReapproval\":\"Update\",\"updateMappingKey\":\"article-price-import\"}" | jq .id
# Step 9: one 503, then approve.
curl -s -X POST http://localhost:8086/__control/article-prices/fail-next
curl -s -X POST "$API/documents/$DOC:approve" -H "$H" -H "$J" -d '{}' | jq .status
curl -s "$API/deliveries?documentId=$DOC" -H "$H" | jq '.items[] | {destinationName, status, attempts, lastStatusCode}'
```

The run behind this section (compose project `p3c`, 2026-11-03) used a Playwright script that clicks through steps 1
to 11; its screenshots show the switch, the supplier attributes, the lookup table, the destination form, the editor
with the source tree, the visual map with the preview (`prices: 412 items`, `prices[].scales: 378 items in 309
rows`), the field options, the compiled code, the bindings, the Deliveries tab with the 503 waiting for its retry and
then Delivered with both attempts, the payload, and the delivery log with the price list and the invoice. The mock
ERP journal had the two article-price calls (503, 202; same `Idempotency-Key`, revision 11, 412 prices, 378 scales in
309 rows each, header `vendorNo` V-10042, `priceListId` PL-2027-01) and one `POST /api/ap/invoices` (201).

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
| `Pipeline:MaxParallelism` | 2 | Ingestion and document messages the runner processes at the same time (deliveries have their own `Delivery:MaxParallelism`). At least 1. |
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

**Table tool** (`TableToolOptions`; API only). The table tool and region OCR (docs/11 §12.3, §12.4) run in the API
every tenant shares.

| Key | Default | What it does |
|---|---|---|
| `TableTool:ReadBudget` | `00:00:10` | How long one `regions:table` read (proposal, preview or write) may take over all its pages and patterns; past it the request answers 422 `TABLE_READ_TOO_SLOW`. A read also stops when the client goes away. `00:00:00` refuses every read (tests). |
| `TableTool:ConcurrentReadsPerTenant` | 2 | `regions:table` and `regions:read` requests running at once per tenant (per user in platform scope). At least 1. |
| `TableTool:QueuedReadsPerTenant` | 4 | Further requests that wait for a free slot; more answer 429. Not negative. |

**Outputs** (`OutputOptions`, `JsonataRendererOptions`; both hosts). Output mappings render in renderer child
processes (`DocProc.Outputs.Renderer.dll`, next to the host's assembly; docs/11 §13.1), started on the first render.

| Key | Default | What it does |
|---|---|---|
| `Outputs:PublicBaseUrl` | unset | The API's public base URL the values view's `links` start with (`https://dms.example`); relative links without it. |
| `Outputs:Renderer:PoolSize` | processor count, at most 4 | Renderer processes kept, and renders that run at once. |
| `Outputs:Renderer:TimeLimit` | `00:00:02` | Processor time one render may use from reading its request to its answer; the process is killed past it (`timeLimit`). |
| `Outputs:Renderer:WallClockLimit` | `00:00:10` | Wall-clock ceiling of one render however little processor time it got. |
| `Outputs:Renderer:TransferTimeout` | `00:00:30` | Sending a request and the renderer reading it (a 10,000-row price list is about 20 MB of values view). |
| `Outputs:Renderer:StartTimeout` | `00:00:30` | Starting a new renderer process. |
| `Outputs:Renderer:QueueTimeout` | `00:01:00` | How long a render waits for a free process of the pool before it answers `unavailable` (the publish stage retries, a preview answers 503). |
| `Outputs:Preview:ConcurrentPreviewsPerTenant` | 2 | API only: `POST /output-mappings:preview` requests rendering at once per tenant (per user in platform scope). At least 1. |
| `Outputs:Preview:QueuedPreviewsPerTenant` | 2 | API only: further previews that wait for a free slot; more answer 429. Not negative. |
| `Outputs:Renderer:HeapHardLimitBytes` | 1073741824 (1 GiB) | The renderer's GC heap (`DOTNET_GCHeapHardLimit`); a render past it is `memoryLimit` and the process is replaced. |
| `Outputs:Renderer:RendererPath` | `DocProc.Outputs.Renderer.dll` next to the host | The renderer assembly. |
| `Outputs:Renderer:DotnetHostPath` | the running runtime's `dotnet` | The `dotnet` host that runs it. |
| `Delivery:RetrySchedule` | empty: 1 min, 5 min, 15 min, 1 h, 3 h, 6 h, then 12 h | The waits between attempts of a delivery (docs/11 §13.6), each with up to 10 % jitter; the last entry repeats. |
| `Delivery:MaxParallelism` | 4 | Delivery attempts the runner sends at once, in a lane of their own beside `Pipeline:MaxParallelism` (an attempt waits on a receiver for up to its `timeoutSeconds`, and must not hold up the document stages). Below 1 counts as 1. |
| `Delivery:MaxParallelismPerTenant` | 2 | Of those, the most one tenant's attempts take. A tenant that holds a slot never takes the last free one, which is kept for a tenant with nothing in flight, so one or two tenants whose receivers never answer leave a slot for the others; it takes three such tenants with the defaults (ceil((MaxParallelism - 1) / MaxParallelismPerTenant) + 1) to fill the lane. Below 1 counts as 1. |
| `Delivery:MaxAttempts` | 15 | Attempts before a retryable failure is dead-lettered (`DELIVERY_FAILED`). |
| `Delivery:MaxAge` | `3.00:00:00` | How long after its creation a delivery is still retried. |
| `Delivery:MaxRetryAfter` | `12:00:00` | The longest `Retry-After` of a receiver that is honoured (the shortest is 1 s). |
| `Delivery:SweepInterval` | `00:00:30` | How often the delivery sweeper looks for deliveries whose message was lost (a crash after the fan-out's commit, a retry waiting in a process that ended). Zero turns it off. |
| `Delivery:SweepGrace` | `00:01:00` | How long past its due time a delivery is left to its own message before the sweeper queues it. |
| `Delivery:SweepBatchSize` | 100 | The most deliveries one sweep queues. |
| `Delivery:SendLease` | `00:02:00` | How long an attempt holds its delivery on top of the transport's timeout; a process that died mid-send leaves it to the sweeper after that. |
| `Delivery:DropRoot` | `data/outbound` (content root); `/data/outbound` in compose | The root folder of `blob` destinations' file drop; each tenant writes below `<root>/<tenantId>`. |
| `Delivery:DevelopmentHosts` | empty; `mock-erp:8080` in compose, `localhost:8086` under the AppHost | `host` or `host:port` entries http destinations may reach over plain http at any address, past `TenantEndpointGuard`. Honoured in the Development environment only. |

**Mailboxes** (`MailOptions`; both hosts: the worker's pollers read mail, the API checks a mailbox channel's host)

| Key | Default | What it does |
|---|---|---|
| `Mail:ImapHost` | unset; `mail` in compose, the GreenMail container under the AppHost | The platform's IMAP server, which a mailbox channel without a `host` of its own reads (docs/11 §7.1). Unset: such a channel's last error says the platform runs none. |
| `Mail:ImapPort` | 143; `3143` in compose | Its port. |
| `Mail:ImapSecurity` | `Auto`; `None` in compose and under the AppHost | `None`, `StartTls`, `SslOnConnect` or `Auto` for the platform server. A tenant's own server is always reached over TLS (`ssl` or `starttls` in its config). |
| `Mail:AllowedHosts` | empty | IMAP hosts a tenant's mailbox may name past the public-host check (loopback, private and reserved addresses are refused otherwise, as for tenant endpoints). |
| `Mail:TrustedAuthServIds` | empty | `authserv-id`s of the receiving servers whose `Authentication-Results` header is read; empty reads the topmost one (the receiving server's). A channel's `authServId` adds one. |
| `Mail:MaxMessageBytes` | 52428800 (50 MB) | The largest message ingested; a larger one is skipped (the cursor moves past it) and named in the channel's last error. |
| `Mail:Timeout` | `00:00:30` | Connect, login and read timeout of one IMAP operation. |

**Channel pollers** (`IntakeOptions`; the host that runs the pipeline)

| Key | Default | What it does |
|---|---|---|
| `Intake:PollersEnabled` | true; false for the API in compose and under the AppHost | Whether this host reads the `Mailbox` and `WatchedFolder` channels of tenants with `sourceRouting` on (docs/11 §7.1). Two hosts polling one channel ingest each message and file once (unique per channel), so one host is enough. |
| `Intake:PollInterval` | `00:00:30` | How often every polling channel is read; zero turns the pollers off. The first poll runs one interval after start. |
| `Intake:MaxMessagesPerPoll` | 25 | The most messages one mailbox poll ingests; the rest wait for the next poll. |
| `Intake:FolderRoot` | `data/dropzone` (content root); `/data/dropzone` in compose (`./fixtures/dropzone` mounted), `fixtures/dropzone` under the AppHost | The drop zone. A tenant's folders are below `<root>/<tenant slug>/`; a channel's `path` is relative to that. |
| `Intake:FolderStableFor` | `00:00:05` | How long a file's size and time must stay unchanged before it is taken (a channel's `stableSeconds` overrides it). |
| `Intake:MaxFilesPerScan` | 50 | The most files one folder scan takes. |
| `Intake:MaxFileBytes` | 52428800 (50 MB) | The largest file taken; a larger one is moved to `failed/`. |

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
