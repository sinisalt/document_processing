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
| Compute | Azure Container Apps (KEDA scaling on queue length) | **Aspire AppHost** starts the API, the worker and all containers above with one `dotnet run`; `docker compose` file generated from it for people who prefer that. The two React apps run as Vite dev servers next to it (README "Web apps and end-to-end tests"); the AppHost does not start them yet. | Scaling behaviour is not local. | Dev environment in Azure deployed by Bicep from CI on every main merge. |
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

### Demo data

With `Persistence:SeedDemoDocuments=true` (the `appsettings.Development.json` default) the API's
`DemoDataHostedService` (`src/DocProc.Pipeline/Demo`) runs after the seed and fills the dev database with
something worth looking at, so the portal, the admin console and the screenshots show real documents on a
clean checkout without Docker or an LLM key:

| What | Rows |
|---|---|
| Documents | Every file under `fixtures/demo/<tenant slug>/` is ingested for that tenant through the same `UploadIntake` code path as `POST /ingestions` (raw blob, ingestion with one candidate item, `ProcessIngestion` on the in-process queue), from the tenant admin's address. The pipeline then processes them like any upload. |
| Replay provider | An active platform configuration of kind `Replay` (`DemoData.PlatformReplayConfig`) with the same models as the platform Anthropic one, so the gateway answers the demo PDFs' LLM calls from `fixtures/demo/llm-replay`. Added only when no other platform configuration is active: a dev database that already runs on a real key keeps it. A request with no fixture (any other PDF you upload) ends `Failed` at once (a miss is an `InvalidRequest`, which no retry fixes) with "No recorded response … This document has no demo fixture …" (the `LlmReplayMissHint` the demo data registers) instead of `LLM_PROVIDER_UNAVAILABLE`; to use a real key, store it and enable the Anthropic configuration in the admin console (the newest active platform configuration wins) or disable the Replay one. The start-up log names the platform configuration in use, and warns instead when the active configuration is a real provider whose key the gateway cannot resolve (no `Llm:Secrets:<secretRef>` or `LLM_SECRET_<SECRET_REF>`), because every LLM call would then fail. |
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
`Approved`, `Rejected`) that has no outbox row lost its publish message and gets `ProcessDocument(Publish)` again;
otherwise `Failed`, `NeedsReview` and the settled states are left alone, as is a document persisted `Validated` (which
the pipeline never does; it is logged). Every handler drops a message whose work is already done, so a twin is harmless.
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
with the committed ones. After a change to a prompt, a schema, the LLM options, the models, the page rendering or a
demo PDF it fails and says so; regenerate and commit:

```bash
UPDATE_DEMO_FIXTURES=1 dotnet test --project tests/DocProc.Pipeline.Tests -c Release -- --filter-class DocProc.Pipeline.Tests.Demo.DemoFixtureTests
git add fixtures/demo/llm-replay
```

The two PDFs are committed bytes, drawn once by `tests/DocProc.Pipeline.Tests/Pdf/DemoPdfs.cs` with PDFsharp
(test-only, see below). PDFsharp writes a new document id on every save, so the normal run never rewrites them;
after editing `DemoPdfs`, run once with `REGENERATE_DEMO_PDFS=1` and then with `UPDATE_DEMO_FIXTURES=1`. To add a
demo document, drop the file into the tenant folder; if it is a PDF that needs an LLM call, add its answer to
`DemoAnswers` (and its expected request to the fixture test) and regenerate.

The classification hash covers the PNG that PDFium renders. The committed fixtures are generated on linux-x64 (the
CI platform, `DemoData.FixtureCapturePlatform`); a machine whose PDFium build rasterises the page differently gets a
replay miss for the delivery note, which then ends `Failed`, and the API logs a warning saying so at start-up. On any
platform other than linux-x64 (`RuntimeInformation`: Linux and X64), `DemoFixtureTests` compares only the extraction
fixture, which hashes the PDF bytes and is platform-independent, `UPDATE_DEMO_FIXTURES=1` rewrites only that one, and
`DemoDataTests` skips the delivery-note assertions; both tests then report themselves skipped with this explanation.
Do not commit a classification fixture regenerated on such a machine. `fixtures/demo/.gitattributes` (`* -text`)
keeps every file's bytes exactly as committed on every checkout, whatever `core.autocrlf` says, since the bytes key
the request hashes.

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

**Pipeline** (`PipelineOptions`; both hosts)

| Key | Default | What it does |
|---|---|---|
| `Pipeline:RunInProcess` | true outside Production; the worker: true everywhere | Whether this process consumes the in-process queue (docs/03 "Implementation"). |
| `Pipeline:MaxParallelism` | 2 | Messages the runner processes at the same time. |
| `Pipeline:RetryBaseDelay` | `00:00:05` | Delay before the second attempt; doubled per attempt. |
| `Pipeline:MaxRetryDelay` | `00:10:00` | Cap of one backoff delay. |
| `Pipeline:MaxAttempts` | 5 | Attempts per stage message, the first included; the last failure ends `Failed`. |
| `Pipeline:ShutdownGracePeriod` | `00:00:30` | How long the runner waits for in-flight messages when the host stops. |
| `Pipeline:RenderDpi` | 200 | Page raster resolution of stage 2 (`NormalizeHandler`); must not exceed `DocumentProcessing:Pdf:MaximumRenderDpi`, because a higher value fails every PDF render. |
| `Pipeline:LlmClassificationThreshold` | 0.6 | Stage 4 (`ClassifyHandler`) asks the LLM classifier when the keyword answer is `Unknown` or below this confidence. |
| `Pipeline:MaxArchiveDepth` | 2 | Archives nested deeper are not expanded (`IngestHandler`, docs/03 stage 2). |
| `Pipeline:MaxArchiveEntries` | 200 | Entries unpacked from one archive; the rest are skipped with a reason. |
| `Pipeline:MaxArchiveEntryBytes` | 67108864 (64 MiB) | Largest archive entry unpacked; the copy stops at the limit. |
| `Pipeline:MaxUnpackedBytesPerIngestion` | 268435456 (256 MiB) | Inflated bytes one ingestion may unpack across all its archives, nested ones included; once reached, the current archive stops and waiting archives are ignored with a reason. |
| `Pipeline:MaxUnpackedItemsPerIngestion` | 500 | Files one ingestion may unpack across all its archives (each can become a document). |
| `Pipeline:MaxArchiveCompressionRatio` | 100 | Largest inflated-to-compressed ratio, never below a 1 MiB floor: per entry (a more compressed entry is skipped as a likely zip bomb), per archive (its entries together; the archive stops) and per ingestion (everything unpacked at every level against the archive bytes uploaded; the ingestion's unpack budget runs out). 0 turns all three off. |
| `Pipeline:MinimumImageBytes` | 20480 (20 KiB) | Smaller images are logos, signatures and tracking pixels and are ignored with a reason. |

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
machine, does not turn the skips into failures. CI's runner has Docker, so there they run.
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
(`web/scripts/strip-openapi-servers.mjs`, tested in `packages/api-client/src/openapi.test.ts`).

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
