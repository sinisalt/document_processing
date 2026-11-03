# Demo data

Documents the API ingests in the dependency-free development mode (`Persistence:SeedDemoDocuments=true`, the
`appsettings.Development.json` default), one folder per tenant slug of the development seed:

- `tenant-a/`: Gasthaus zur Post GmbH (METRO, Getränke Hoffmann and Bio-Hof Müller invoices, a METRO credit note,
  a Bio-Hof Müller PDF invoice and delivery note)
- `tenant-b/`: Pizzeria Napoli d.o.o. (an XRechnung from Konzum plus)
- `uploads/tenant-a/`: a Getränke Hoffmann credit note PDF that the demo of docs/09 "Demo: types as data" uploads
  by hand after forking `CreditNote`; the demo data does not ingest it (`uploads` is no tenant slug)
- `llm-replay/`: `FileReplayStore` fixtures that answer the two PDFs' LLM calls without an API key, plus the
  invoice's extraction with the demo custom field and the credit note's extraction with the field of docs/11 §5.3
- `types/`: the demo custom fields: `invoice-customer-number.field.json`, which docs/09 "Document types" adds to a
  fork of `Invoice`, and `credit-note-supplier-customer-ref.field.json` (docs/11 §5.3), which "Demo: types as data"
  adds to a fork of `CreditNote`; their key, kind and description key the third and fourth replay fixtures

What each file is for, how the replay fixtures are regenerated (`UPDATE_DEMO_FIXTURES=1`) and how to reset the
database: docs/09-local-development.md, "Demo data". Do not edit the PDFs or the replay files by hand; their bytes
key the request hashes.
