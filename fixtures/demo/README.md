# Demo data

Documents the API ingests in the dependency-free development mode (`Persistence:SeedDemoDocuments=true`, the
`appsettings.Development.json` default), one folder per tenant slug of the development seed:

- `tenant-a/`: Gasthaus zur Post GmbH (METRO, Getränke Hoffmann and Bio-Hof Müller invoices, a METRO credit note,
  a Bio-Hof Müller PDF invoice and delivery note)
- `tenant-b/`: Pizzeria Napoli d.o.o. (an XRechnung from Konzum plus)
- `llm-replay/`: `FileReplayStore` fixtures that answer the two PDFs' LLM calls without an API key

What each file is for, how the replay fixtures are regenerated (`UPDATE_DEMO_FIXTURES=1`) and how to reset the
database: docs/09-local-development.md, "Demo data". Do not edit the PDFs or the replay files by hand; their bytes
key the request hashes.
