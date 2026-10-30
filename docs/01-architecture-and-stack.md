# Architecture and stack

## Shape of the system

```
          ┌───────────────────────── React (Vite/TS) ─────────────────────────┐
          │  apps/admin-console (SuperAdmin, Operator)   apps/customer-portal  │
          └──────────────────────────────┬────────────────────────────────────┘
                                         │ HTTPS, OIDC bearer tokens / API keys
          ┌──────────────────────────────▼────────────────────────────────────┐
          │  DocProc.Api  (ASP.NET Core, .NET 10, OpenAPI, SignalR for live    │
          │  review-queue updates)                                             │
          └───────┬───────────────────────────┬───────────────────────────────┘
                  │ EF Core                   │ enqueue (transactional outbox)
          ┌───────▼────────┐          ┌───────▼─────────────────────────────────┐
          │ Azure SQL /    │          │ Azure Service Bus (local: emulator)     │
          │ SQL Server 2025│          └───────┬─────────────────────────────────┘
          └───────▲────────┘                  │
                  │                   ┌───────▼─────────────────────────────────┐
                  │                   │ DocProc.Worker (one process, many        │
                  │                   │ handlers: ingest, normalize, split,      │
                  └───────────────────┤ classify, match, extract, validate,      │
                                      │ publish, profile-synthesis)              │
                                      └───┬─────────┬──────────┬────────────────┘
                                          │         │          │
                                 ┌────────▼──┐ ┌────▼─────┐ ┌──▼──────────────┐
                                 │ Azure Blob│ │ OCR      │ │ LLM gateway:    │
                                 │ (local:   │ │ Tesseract│ │ Anthropic /     │
                                 │ Azurite)  │ │ / Azure  │ │ OpenAI-compat.  │
                                 │           │ │ Layout   │ │ platform or BYOK│
                                 └───────────┘ │ Layout   │ └─────────────────┘
                                               └──────────┘
```

Two deployable processes (API and Worker) built from one solution, plus SQL Server, a queue
and blob storage. Production runs in Azure (Azure SQL Database, Azure Container Apps, Azure
Service Bus, Azure Blob Storage, Key Vault, Entra ID); local development and CI run the same
code against Docker containers (SQL Server 2025 image, Service Bus emulator, Azurite); see
`09-local-development.md` for the full local/Azure mapping and Apple Silicon notes. The worker scales horizontally by stage via queue consumers; LLM and OCR
calls are bounded by per-provider concurrency limiters. Aspire is used for local
orchestration and the dashboard, not as a production dependency.

## Stack decisions

| Area | Choice | Why / alternatives considered |
|---|---|---|
| Runtime | **.NET 10 (LTS, supported to Nov 2028)**, C# 14 | EF Core 10 and Aspire 13 require it. .NET 11 is STS (GA Nov 2026); skip unless a feature is needed. |
| Web | ASP.NET Core minimal APIs + endpoint groups, `Microsoft.AspNetCore.OpenApi`, FluentValidation, ProblemDetails | Controllers are fine too; minimal APIs keep the vertical-slice layout lean. |
| Data | **SQL Server, always**: Azure SQL Database in production, `mcr.microsoft.com/mssql/server:2025-latest` in Docker for local and CI; **EF Core 10 SQL Server provider** (`Microsoft.EntityFrameworkCore.SqlServer`) with migrations | Native `json` column type for the canonical document and the profile rules (schema-versioned, queryable with `JSON_VALUE`); relational tables for tenancy, flags, review, audit. EF Core 10 adds a built-in vector type for SQL Server, and Azure SQL / SQL Server 2025 have a native `VECTOR` type with distance functions, so layout-fingerprint similarity needs no extra service. Use the same compatibility level locally and in Azure. |
| Blob | **Azure Blob Storage** behind an `IBlobStore` abstraction; content-addressed keys; **Azurite** container locally | Immutable (WORM) policies on the raw-document container cover tax-record integrity requirements; lifecycle rules move old documents to cool/archive tiers. |
| Queue / jobs | **Wolverine** (MIT) with the **Azure Service Bus** transport and **SQL Server-backed** outbox/inbox and scheduled messages (`WolverineFx.SqlServer`, `WolverineFx.AzureServiceBus`); the Service Bus emulator container locally | MassTransit v9 became commercial (v8 still Apache-2.0 but maintenance-only). Hangfire (LGPL) is acceptable for pure cron jobs; Quartz.NET for schedules. Wolverine gives messaging, outbox and durable retries in one. |
| Email | Microsoft Graph change notifications + delta queries for M365 mailboxes (`Mail.Read` application permission, mailbox per customer, subscriptions renewed every ~7 days); **MailKit 5 (MIT)** IMAP IDLE for other providers | Plus-addressing (`invoices+acme@…`) is on by default in Exchange Online but some supplier systems strip the `+`; dedicated shared mailboxes per customer are licence-free in M365 and unambiguous. |
| PDF | **PdfPig** (Apache-2.0) for words with positions; **PDFtoImage** (MIT, PDFium + SkiaSharp) for rasterising pages | iText is AGPL (source-disclosure for SaaS or paid licence). Docnet is stale. |
| OCR | `IOcrProvider`: **TesseractOCR** NuGet (Apache-2.0, Tesseract 5.5) first; **Azure AI Document Intelligence** `prebuilt-layout` (~$10 / 1 000 pages, positioned words + tables) as the premium provider for poor scans | AWS Textract and Google Document AI are equivalent alternatives behind the same interface. |
| E-invoice XML | **Hand-written `System.Xml.Linq` mappers (UBL 2.1, CII D16B) in `DocProc.Extraction.EInvoice`**: local-name navigation over the EN 16931 core elements, no third-party e-invoice library | ZUGFeRD-csharp is deprecated from Dec 2026 with a commercial successor (FactoorSharp), and no maintained OSS .NET UBL library exists; the ~30 EN 16931 elements the canonical model needs are cheaper to map by hand than to wrap a library around. XML is read through one secured reader (DTDs prohibited, no resolver, size and nesting limits). |
| LLM | **Provider-neutral gateway** over `Microsoft.Extensions.AI`, with adapters for the **Anthropic C# SDK** (`Anthropic`, `Anthropic.Foundry` for Claude on Microsoft Foundry) and the **OpenAI / Azure OpenAI SDKs** (any OpenAI-compatible endpoint); **BYOK** per tenant with keys in Key Vault | Design in `08-llm-providers-and-byok.md`; Anthropic-specific defaults and costs in `06-llm-integration.md`. |
| Identity | **Microsoft Entra External ID** (customer portal users) and Entra ID (platform staff) via OIDC; ASP.NET Core authorization policies; per-tenant API keys hashed at rest | Managed identities for service-to-service access (SQL, Blob, Service Bus, Key Vault): no connection-string secrets in production. |
| Observability | OpenTelemetry (traces, metrics, logs) exported to Azure Monitor / Application Insights; Aspire dashboard locally; per-document cost and token metrics as first-class data | |
| Front end | Vite + React 19 + TypeScript, TanStack Query/Table, shadcn/ui, PDF.js, generated OpenAPI client, i18next | Two apps, one shared package. |
| Testing | xUnit + Testcontainers (`Testcontainers.MsSql`, Service Bus emulator, Azurite), snapshot tests for the extraction engine, a **gold set** of approved documents for LLM evals, Playwright for the portal | Integration tests run against the real SQL Server engine, never an in-memory provider, so T-SQL-specific features (json, RLS, vector) are exercised. |
| Delivery | Docker images, GitHub Actions, Bicep for Azure resources, **Azure Container Apps** for API and worker (KEDA scaling on Service Bus queue length), Azure SQL serverless tier for dev/test | |

## Solution layout

```
document_processing/
├── docs/                          this plan, ADRs, API notes
├── src/
│   ├── DocProc.Domain/            canonical model, flags, state machine, validation rules (no deps)
│   ├── DocProc.Application/       use cases, pipeline stage handlers, interfaces (IBlobStore, IOcrProvider, IDocumentExtractor, IMailSource)
│   ├── DocProc.Infrastructure/    EF Core + SQL Server, Azure Blob, Key Vault, Wolverine (Service Bus, SQL Server), Graph/MailKit, PdfPig/PDFtoImage, Tesseract, Azure DI
│   ├── DocProc.Llm/               ILlmGateway, ILlmProvider, prompt bundles, schema portability tests
│   ├── DocProc.Llm.Anthropic/     Anthropic API + Claude on Foundry adapter
│   ├── DocProc.Llm.OpenAi/        OpenAI, Azure OpenAI and OpenAI-compatible adapter
│   ├── DocProc.Extraction.Profiles/ the deterministic profile engine (pure, heavily unit-tested)
│   ├── DocProc.Extraction.EInvoice/ Factur-X / UBL parsers → canonical model
│   ├── DocProc.Api/               ASP.NET Core host
│   ├── DocProc.Worker/            Wolverine host
│   └── DocProc.AppHost/           Aspire local orchestration
├── tests/
│   ├── DocProc.Domain.Tests/
│   ├── DocProc.Extraction.Profiles.Tests/   sample PDFs + expected JSON (snapshot)
│   ├── DocProc.Integration.Tests/           Testcontainers (SQL Server 2025, Azurite, Service Bus emulator)
│   └── DocProc.Evals/                       gold-set runner: field accuracy, STP, cost per model/prompt
├── web/
│   ├── packages/ui/  packages/api-client/
│   ├── apps/admin-console/
│   └── apps/customer-portal/
└── deploy/                        docker-compose, Helm/ACA manifests, GitHub Actions
```

## Multi-tenancy and data protection

- Tenant id on every row; EF Core global query filters as the first layer; **SQL Server
  row-level security** (a security policy keyed on `SESSION_CONTEXT('TenantId')`, set by the
  data layer per request) as the second layer so a missed filter cannot leak data; integration
  tests that assert cross-tenant reads fail.
- Azure SQL: Transparent Data Encryption on by default, Always Encrypted or customer-managed
  keys only if a customer contract requires it; temporal tables on `Document` and
  `ExtractorProfile` give the audit history for free.
- Blobs under a per-tenant prefix; signed URLs with short expiry for the viewer.
- Invoices are tax records: retention is a tenant setting with per-country defaults (Germany 8 years
  for accounting documents since 2025, Croatia 11, France 10), with legal hold and a purge job for expired documents.
  GDPR applies to the personal data inside (names, emails of contact persons): document the
  purpose, keep an access log, and offer export/erasure for non-tax-relevant data.
- LLM provider: use an API tier with a data-retention agreement appropriate for the
  customers' jurisdictions; never send tenant data to a provider that trains on it. Log the
  request hash and token counts, not the document content, in telemetry.
- Secrets in the platform's secret store (Key Vault / AWS Secrets Manager); mailbox credentials
  and API keys never in the database in plain text.
