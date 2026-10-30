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
          │ PostgreSQL 17  │          │ Queue (RabbitMQ or Azure Service Bus)    │
          │ + pgvector     │          └───────┬─────────────────────────────────┘
          └───────▲────────┘                  │
                  │                   ┌───────▼─────────────────────────────────┐
                  │                   │ DocProc.Worker (one process, many        │
                  │                   │ handlers: ingest, normalize, split,      │
                  └───────────────────┤ classify, match, extract, validate,      │
                                      │ publish, profile-synthesis)              │
                                      └───┬─────────┬──────────┬────────────────┘
                                          │         │          │
                                 ┌────────▼──┐ ┌────▼─────┐ ┌──▼──────────────┐
                                 │ Blob store│ │ OCR      │ │ Claude API      │
                                 │ S3/Azure  │ │ Tesseract│ │ (Anthropic SDK) │
                                 │ Blob      │ │ / Azure  │ │                 │
                                 └───────────┘ │ Layout   │ └─────────────────┘
                                               └──────────┘
```

Two deployable processes (API and Worker) built from one solution, plus Postgres, a queue
and blob storage. The worker scales horizontally by stage via queue consumers; LLM and OCR
calls are bounded by per-provider concurrency limiters. Aspire is used for local
orchestration and the dashboard, not as a production dependency.

## Stack decisions

| Area | Choice | Why / alternatives considered |
|---|---|---|
| Runtime | **.NET 10 (LTS, supported to Nov 2028)**, C# 14 | EF Core 10 and Aspire 13 require it. .NET 11 is STS (GA Nov 2026); skip unless a feature is needed. |
| Web | ASP.NET Core minimal APIs + endpoint groups, `Microsoft.AspNetCore.OpenApi`, FluentValidation, ProblemDetails | Controllers are fine too; minimal APIs keep the vertical-slice layout lean. |
| Data | **PostgreSQL 17 + Npgsql EF Core 10**, `pgvector` extension via `Pgvector.EntityFrameworkCore` when fingerprint similarity is needed | JSONB for canonical documents and profiles (schema-versioned); relational for tenancy, flags, review, audit. |
| Blob | S3-compatible or Azure Blob via an `IBlobStore` abstraction; content-addressed keys | MinIO's community edition was archived in April 2026, so do not plan on it for self-hosting. Use the cloud provider's object store, or Garage/SeaweedFS if self-hosted. |
| Queue / jobs | **Wolverine** (MIT) with RabbitMQ or Azure Service Bus transport, Postgres-backed outbox/inbox and scheduled messages | MassTransit v9 became commercial (v8 still Apache-2.0 but maintenance-only). Hangfire (LGPL) is acceptable for pure cron jobs; Quartz.NET for schedules. Wolverine gives messaging, outbox and durable retries in one. |
| Email | Microsoft Graph change notifications + delta queries for M365 mailboxes (`Mail.Read` application permission, mailbox per customer, subscriptions renewed every ~7 days); **MailKit 5 (MIT)** IMAP IDLE for other providers | Plus-addressing (`invoices+acme@…`) is on by default in Exchange Online but some supplier systems strip the `+`; dedicated shared mailboxes per customer are licence-free in M365 and unambiguous. |
| PDF | **PdfPig** (Apache-2.0) for words with positions; **PDFtoImage** (MIT, PDFium + SkiaSharp) for rasterising pages | iText is AGPL (source-disclosure for SaaS or paid licence). Docnet is stale. |
| OCR | `IOcrProvider`: **TesseractOCR** NuGet (Apache-2.0, Tesseract 5.5) first; **Azure AI Document Intelligence** `prebuilt-layout` (~$10 / 1 000 pages, positioned words + tables) as the premium provider for poor scans | AWS Textract and Google Document AI are equivalent alternatives behind the same interface. |
| E-invoice XML | **ZUGFeRD-csharp** (Apache-2.0) for Factur-X/ZUGFeRD/XRechnung CII; UBL 2.1 via generated classes from the OASIS XSDs (no maintained OSS .NET UBL library found) | ZUGFeRD-csharp is deprecated from Dec 2026 with a commercial successor (FactoorSharp). Pin the last Apache version, wrap it behind an interface, decide later whether to pay or maintain a fork. |
| LLM | **Anthropic C# SDK** (`Anthropic` NuGet): native PDF input, structured outputs (JSON schema), citations, prompt caching, Message Batches | Details and costs in `06-llm-integration.md`. Provider abstraction kept thin: one `IDocumentExtractor` interface, Claude is the first implementation. |
| Identity | OIDC (Entra ID for M365-heavy customers, Keycloak if self-hosted); ASP.NET Core authorization policies; per-tenant API keys hashed at rest | Decide the IdP in phase 0. |
| Observability | OpenTelemetry (traces, metrics, logs) to the provider of choice; Aspire dashboard locally; per-document cost and token metrics as first-class data | |
| Front end | Vite + React 19 + TypeScript, TanStack Query/Table, shadcn/ui, PDF.js, generated OpenAPI client, i18next | Two apps, one shared package. |
| Testing | xUnit + Testcontainers (Postgres, RabbitMQ), snapshot tests for the extraction engine, a **gold set** of approved documents for LLM evals, Playwright for the portal | |
| Delivery | Docker images, GitHub Actions, environment per PR optional; Helm or Azure Container Apps | |

## Solution layout

```
document_processing/
├── docs/                          this plan, ADRs, API notes
├── src/
│   ├── DocProc.Domain/            canonical model, flags, state machine, validation rules (no deps)
│   ├── DocProc.Application/       use cases, pipeline stage handlers, interfaces (IBlobStore, IOcrProvider, IDocumentExtractor, IMailSource)
│   ├── DocProc.Infrastructure/    EF Core + Postgres, blob stores, Wolverine transports, Graph/MailKit, PdfPig/PDFtoImage, Tesseract, Azure DI, Anthropic
│   ├── DocProc.Extraction.Profiles/ the deterministic profile engine (pure, heavily unit-tested)
│   ├── DocProc.Extraction.EInvoice/ Factur-X / UBL parsers → canonical model
│   ├── DocProc.Api/               ASP.NET Core host
│   ├── DocProc.Worker/            Wolverine host
│   └── DocProc.AppHost/           Aspire local orchestration
├── tests/
│   ├── DocProc.Domain.Tests/
│   ├── DocProc.Extraction.Profiles.Tests/   sample PDFs + expected JSON (snapshot)
│   ├── DocProc.Integration.Tests/           Testcontainers
│   └── DocProc.Evals/                       gold-set runner: field accuracy, STP, cost per model/prompt
├── web/
│   ├── packages/ui/  packages/api-client/
│   ├── apps/admin-console/
│   └── apps/customer-portal/
└── deploy/                        docker-compose, Helm/ACA manifests, GitHub Actions
```

## Multi-tenancy and data protection

- Tenant id on every row; EF Core global query filters; integration tests that assert
  cross-tenant reads fail.
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
