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

Requirements: the .NET 10 SDK (`global.json` pins the feature band). Docker is only needed for
the integration tests, which skip themselves when it is absent.

```bash
dotnet build DocProc.slnx
```

```bash
dotnet test DocProc.slnx
```

Tests use xunit v3 on the Microsoft Testing Platform (opted in via `global.json`). The schemas
sent to LLM providers are snapshotted under `schemas/`; after an intentional contract change,
bump the version in `CanonicalSchemas` and rerun with `UPDATE_SCHEMAS=1`.

Run the API locally and check it answers:

```bash
dotnet run --project src/DocProc.Api
```

```bash
curl http://localhost:5000/health
```

## Repository layout

```
src/
  DocProc.Domain/             canonical model, flags, state machine, validation rules (no dependencies)
  DocProc.Application/        pipeline contracts and the interfaces infrastructure implements
  DocProc.Infrastructure/     EF Core / SQL Server, blob storage, mail, PDF, OCR (skeleton)
  DocProc.Llm/                provider-neutral LLM gateway, prompt bundles, portable JSON schemas, replay provider
  DocProc.Llm.Anthropic/      Anthropic API and Claude on Foundry adapter
  DocProc.Llm.OpenAi/         OpenAI / Azure OpenAI adapter (capabilities only until Phase 1)
  DocProc.Extraction.Profiles/ deterministic extractor-profile engine (Phase 4)
  DocProc.Extraction.EInvoice/ Factur-X / UBL parsers (Phase 2)
  DocProc.Api/                ASP.NET Core host
  DocProc.Worker/             pipeline worker host
tests/
  DocProc.Domain.Tests/       validation rules, state machine, value types
  DocProc.Llm.Tests/          schema portability and snapshots, config resolution, gateway, Anthropic adapter
  DocProc.Integration.Tests/  Testcontainers (SQL Server 2025); skipped without Docker
schemas/                      committed JSON schemas sent to providers
docs/                         plan and design
```
