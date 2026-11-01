# 10 – Screenshots

Every screen of the customer portal and the admin console, captured by Playwright against the
dev-mode API (README "Running without Docker") with the demo documents of `fixtures/demo`. The
images are written by `web/e2e/tests/screenshots-portal.spec.ts` and
`web/e2e/tests/screenshots-admin.spec.ts`; both tag their tests `@screenshot`.

To recapture, start the API on a fresh seed and both dev servers (README "Web apps and end-to-end
tests"), then from `web/e2e`:

```bash
E2E_API_URL=http://127.0.0.1:5100 E2E_PORTAL_URL=http://127.0.0.1:5173 E2E_ADMIN_URL=http://127.0.0.1:5174 pnpm e2e:screenshots
```

The specs set up what a screen needs beyond the seed through the API, and only when it is
missing: a claimed review task, an API key, and a tenant B invoice waiting for review. A second
run on the same database therefore writes the same files. Only relative times ("now",
"3 minutes ago") and rendering noise change between runs. Desktop shots are 1440 px wide at
device scale 1; most are 1440×900, and a few that need more room are taller or full page. Phone
shots are 390×844. Animations are off.

Known limits of the images:

- Native date inputs show the browser's UI-language pattern (mm/dd/yyyy). Playwright's locale
  does not change it.
- The due date of invoice 2026-131 has no box on the page, because the model computed it from
  the payment terms ("14 Tagen"). The flag says "Not on the page image".
- The line-item grid scrolls horizontally inside its panel at 1440 px.

## Customer portal (`web/e2e/screenshots/portal`)

| # | Screen |
|---|---|
| ![](../web/e2e/screenshots/portal/01-sign-in.png) | **01 Sign-in.** The dev sign-in lists the six realm users with their role and the tenant they belong to. |
| ![](../web/e2e/screenshots/portal/02-dashboard.png) | **02 Dashboard.** Tenant A admin: status counts, with "Nothing here" on the zero counts, and the quick links. |
| ![](../web/e2e/screenshots/portal/03-inbox.png) | **03 Inbox.** All six tenant A demo documents with type, issue date, total, status, flags and received time. |
| ![](../web/e2e/screenshots/portal/04-inbox-needs-review.png) | **04 Inbox, Needs review.** The three documents waiting for a person, with amber status badges. |
| ![](../web/e2e/screenshots/portal/05-review-pdf-flag-selected.png) | **05 Review, PDF.** Bio-Hof Müller invoice 2026-131 with its low-confidence due-date flag selected. Provenance boxes sit on the text each value came from. |
| ![](../web/e2e/screenshots/portal/06-review-pdf-line-items.png) | **06 Line items.** The two-row line grid with quantity, unit, prices, net and tax, and the "line arithmetic OK" check. |
| ![](../web/e2e/screenshots/portal/07-review-xml.png) | **07 Review, XRechnung.** E-invoice 2026-118 has no page images and a blocking "Totals do not add up" flag. Its values come from the XML at 100 %. |
| ![](../web/e2e/screenshots/portal/08-review-timeline.png) | **08 Timeline.** Received, each stage transition, Flagged and Review requested, with times to the second. |
| ![](../web/e2e/screenshots/portal/09-review-queue.png) | **09 Review queue.** As the tenant A reviewer: one task claimed by "You" with Release, the others open with Claim, and the reasons translated. |
| ![](../web/e2e/screenshots/portal/10-suppliers.png) | **10 Suppliers.** The three tenant A suppliers with VAT ID, currency, IBANs and e-mail domains. |
| ![](../web/e2e/screenshots/portal/11-supplier-dialog.png) | **11 Supplier sheet.** Editing METRO Deutschland GmbH: details, IBANs, domains and aliases. |
| ![](../web/e2e/screenshots/portal/12-settings-general.png) | **12 Settings, General.** Organisation identity and the review policy (threshold 80 %, retention, always-review suppliers). |
| ![](../web/e2e/screenshots/portal/13-settings-channels.png) | **13 Settings, Channels.** The API upload channel, Active, with Pause. |
| ![](../web/e2e/screenshots/portal/14-settings-members.png) | **14 Settings, Members.** Reviewer and admin with e-mail, role and member-since date. |
| ![](../web/e2e/screenshots/portal/15-settings-ai-providers.png) | **15 Settings, AI providers.** No BYOK providers yet, the routing policy, and the usage card with a daily chart. |
| ![](../web/e2e/screenshots/portal/16-upload-dialog.png) | **16 Upload.** Two files selected, a PDF and an XML, with the total size. |
| ![](../web/e2e/screenshots/portal/17-de-dashboard.png) | **17 German dashboard.** "Zu prüfen", "Nichts offen" and the German navigation. |
| ![](../web/e2e/screenshots/portal/18-de-review.png) | **18 German review.** Dates as 28.10.2026, amounts as 461,60 €, "KI · S. 1" source badges and the German flag text. |
| ![](../web/e2e/screenshots/portal/19-dark-inbox.png) | **19 Dark inbox.** The inbox in the dark theme. |
| ![](../web/e2e/screenshots/portal/20-dark-review.png) | **20 Dark review.** The focused field's box is highlighted in blue on the page image, and the Ctrl+Enter hint is readable. |
| ![](../web/e2e/screenshots/portal/21-phone-review.png) | **21 Phone review.** At 390 px: wrapped header actions, the flag, and the viewer toolbar on one line. |
| ![](../web/e2e/screenshots/portal/22-phone-inbox.png) | **22 Phone inbox.** Wrapped tabs, filters two per row, and a two-column document list. |
| ![](../web/e2e/screenshots/portal/23-viewer-review-read-only.png) | **23 Viewer, read only.** Tenant B viewer on a Konzum plus invoice: no edit or approve actions, no queue or settings in the navigation, and "Waiting for review: Supplier is always reviewed" where there are no flags to explain it. |
| ![](../web/e2e/screenshots/portal/24-empty-inbox-search.png) | **24 Empty search.** "No document matches these filters", with Clear filters. |
| ![](../web/e2e/screenshots/portal/25-settings-api-keys.png) | **25 Settings, API keys.** The "ERP export" key with its prefix, scopes, status and Revoke. |
| ![](../web/e2e/screenshots/portal/26-review-pdf-box-selects-field.png) | **26 Box selects field.** At 150 % zoom, clicking the Lieferdatum box highlights it and focuses the Delivery date field. |

## Admin console (`web/e2e/screenshots/admin`)

| # | Screen |
|---|---|
| ![](../web/e2e/screenshots/admin/01-sign-in.png) | **01 Sign-in.** The same dev users on cards of equal height, each naming its tenant. |
| ![](../web/e2e/screenshots/admin/02-dashboard-superadmin.png) | **02 Dashboard, super admin.** Tenants, the review backlog, failed documents, AI spend by tenant, and the failed-documents table with Retry. |
| ![](../web/e2e/screenshots/admin/03-tenants.png) | **03 Tenants.** Both tenants with slug, status, country, currency and created date. |
| ![](../web/e2e/screenshots/admin/04-tenant-create-validation.png) | **04 Create tenant.** An empty submit shows translated "Required." messages. |
| ![](../web/e2e/screenshots/admin/05-tenant-overview.png) | **05 Tenant overview.** Tenant A's identity, with Edit and Suspend. |
| ![](../web/e2e/screenshots/admin/06-tenant-channels.png) | **06 Tenant channels.** The API upload channel, with muted dashes where a value is empty. |
| ![](../web/e2e/screenshots/admin/07-tenant-members.png) | **07 Tenant members.** Reviewer and admin with role select and member-since date. |
| ![](../web/e2e/screenshots/admin/08-tenant-suppliers.png) | **08 Tenant suppliers.** Tenant A's suppliers with VAT ID, currency, domains and always-review. |
| ![](../web/e2e/screenshots/admin/09-tenant-settings.png) | **09 Tenant settings.** Threshold, retention, profile sharing and always-review suppliers. |
| ![](../web/e2e/screenshots/admin/10-tenant-api-keys.png) | **10 Tenant API keys.** The empty state, with New API key. |
| ![](../web/e2e/screenshots/admin/11-tenant-llm.png) | **11 Tenant AI.** Routing policy and the usage explorer for the current month. |
| ![](../web/e2e/screenshots/admin/12-documents.png) | **12 Documents.** Every tenant's documents, filterable by tenant, status, type, flag and date. |
| ![](../web/e2e/screenshots/admin/13-document-timeline.png) | **13 Failed document.** "Failure reason: Unreadable PDF", a placeholder for the page, and the timeline with the Stage failed row open. |
| ![](../web/e2e/screenshots/admin/14-document-reprocess.png) | **14 Reprocess.** The reprocess dialog with Normalize preselected. |
| ![](../web/e2e/screenshots/admin/15-review-queue.png) | **15 Global review queue.** Tasks across both tenants with translated reasons, the assignee under the task status, and Claim, Release and Open fully on screen. |
| ![](../web/e2e/screenshots/admin/16-llm-providers.png) | **16 AI providers.** The platform providers with their models per stage. |
| ![](../web/e2e/screenshots/admin/17-llm-add-provider.png) | **17 Add provider.** Provider, data residency, key, endpoint, monthly cap and a model per stage. |
| ![](../web/e2e/screenshots/admin/18-llm-usage.png) | **18 Usage by tenant.** Chart and table of calls, cost and share per tenant. |
| ![](../web/e2e/screenshots/admin/19-dashboard-operator.png) | **19 Dashboard, operator.** Counts across all tenants, with spend kept for super admins. |
| ![](../web/e2e/screenshots/admin/20-dashboard-tenant-viewer.png) | **20 Dashboard, tenant viewer.** A notice that the console is for platform operators, plus tenant B's own counts. |
| ![](../web/e2e/screenshots/admin/21-de-dashboard.png) | **21 German dashboard.** German labels, dates as 01.10.2026, and amounts as 2,05 $. |
| ![](../web/e2e/screenshots/admin/22-de-tenants.png) | **22 German tenants.** The "Mandanten" list with German dates. |
| ![](../web/e2e/screenshots/admin/23-dark-dashboard.png) | **23 Dark dashboard.** The super admin dashboard in the dark theme. |
| ![](../web/e2e/screenshots/admin/24-dark-tenant-members.png) | **24 Dark tenant tabs.** The active Members tab shows as a lighter pill. |
| ![](../web/e2e/screenshots/admin/25-phone-dashboard-sidebar.png) | **25 Phone sidebar.** At 390 px, the navigation sheet with its close button and the user menu. |
