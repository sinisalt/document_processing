import { describe, expect, it } from "vitest";
import { dueState, sortByReviewDue } from "./priority";
import { insightsWindow, percent } from "./insights";
import { acknowledgementBody, acknowledgementDraft, callbackUrl } from "@/components/integrations/acknowledgements";
import { csvHeader, guessColumns, importBody, isImportTarget, reportOf } from "@/components/integrations/masterData";
import { budgetDrafts, budgetUse, budgetsBody } from "@/components/settings/LlmBudgetsCard";
import { settingsBody, settingsDraft } from "@/components/suppliers/SupplierSettingsTab";
import { revisionFacts } from "@/components/review/RevisionPanel";
import type { Supplier } from "./queries";

describe("review priority (docs/11 §11 dueBy)", () => {
  const now = new Date("2026-11-05T10:00:00Z");
  it("tells overdue, due soon and later apart", () => {
    expect(dueState("2026-11-05T09:59:00Z", now)).toBe("overdue");
    expect(dueState("2026-11-05T13:00:00Z", now)).toBe("soon");
    expect(dueState("2026-11-06T22:59:59Z", now)).toBe("later");
    expect(dueState(null, now)).toBeNull();
    expect(dueState("not a date", now)).toBeNull();
  });

  it("puts urgent reviews first, then by due time, keeping the list's order otherwise", () => {
    const rows = [
      { id: "a", priority: "Normal", due: null },
      { id: "b", priority: "Urgent", due: "2026-11-05T22:59:59Z" },
      { id: "c", priority: "High", due: null },
      { id: "d", priority: "Urgent", due: "2026-11-05T12:00:00Z" },
      { id: "e", priority: null, due: "2026-11-04T00:00:00Z" },
      { id: "f", priority: "Normal", due: null },
    ] as const;
    expect(sortByReviewDue(rows, (r) => r.priority, (r) => r.due).map((r) => r.id)).toEqual(["d", "b", "c", "e", "a", "f"]);
  });
});

describe("Insights window", () => {
  it("covers the last n days up to today in UTC and formats shares", () => {
    expect(insightsWindow(30, new Date("2026-11-05T23:30:00Z"))).toEqual({ from: "2026-10-07", to: "2026-11-05" });
    expect(insightsWindow(1, new Date("2026-11-05T00:10:00Z"))).toEqual({ from: "2026-11-05", to: "2026-11-05" });
    expect(percent(1, 3, "en")).toBe("33%");
    expect(percent(1, 0, "en")).toBe("—");
  });
});

describe("destination acknowledgements (docs/11 §13.8)", () => {
  it("reads the stored settings and writes them back, an empty object turning both off", () => {
    const draft = acknowledgementDraft({ callback: { auth: "bearer" }, statusPoll: { urlTemplate: "https://erp.example/status/{externalId}", intervalMinutes: 5, maxPolls: 12, mapping: "{}" } });
    expect(draft).toMatchObject({ callback: true, auth: "bearer", poll: true, intervalMinutes: "5", maxPolls: "12", pollMapping: "{}" });
    expect(acknowledgementBody(draft)).toEqual({ callback: { auth: "bearer" }, statusPoll: { urlTemplate: "https://erp.example/status/{externalId}", intervalMinutes: 5, maxPolls: 12, mapping: "{}" } });
    expect(acknowledgementBody({ ...draft, callback: false, poll: false })).toEqual({});
    expect(acknowledgementDraft(null)).toMatchObject({ callback: false, auth: "hmacSha256", poll: false, intervalMinutes: "15" });
    expect(callbackUrl("https://portal.example/", "d-1")).toBe("https://portal.example/api/v1/integrations/callbacks/d-1");
  });
});

describe("master-data import (docs/11 §14)", () => {
  it("reads a CSV header with either delimiter and guesses the column map in English and German", () => {
    const header = csvHeader('﻿"Kreditorennummer";Lieferant;USt-IdNr;IBAN;Währung\n70001;Nordwind;DE123;DE89;EUR');
    expect(header).toEqual(["Kreditorennummer", "Lieferant", "USt-IdNr", "IBAN", "Währung"]);
    expect(guessColumns(header)).toEqual({ name: "Lieferant", vatId: "USt-IdNr", ibans: "IBAN", currency: "Währung", "attributes.erpVendorId": "Kreditorennummer" });
    expect(csvHeader("")).toEqual([]);
    expect(isImportTarget("attributes.costCentre")).toBe(true);
    expect(isImportTarget("settings.review")).toBe(false);
  });

  it("builds the import body for CSV and JSON and reads a run's report", () => {
    expect(importBody({ format: "csv", content: "a,b", columns: { name: "a" }, matchKeys: ["vatId"], policy: {}, dryRun: true })).toEqual({ format: "csv", content: "a,b", records: null, columns: { name: "a" }, matchKeys: ["vatId"], conflictPolicy: null, dryRun: true });
    expect(importBody({ format: "json", content: '[{"name":"X"}]', columns: {}, matchKeys: [], policy: { name: "localWins" }, dryRun: false })).toMatchObject({ records: [{ name: "X" }], content: null, columns: null, conflictPolicy: { name: "localWins" } });
    const report = reportOf({ report: { created: 1, updated: 0, unchanged: 2, conflicts: 1, errors: 0, rows: [{ row: 1, action: "conflict", supplierId: null, name: "N", matchedBy: "vatId", conflicts: ["iban"] }] }, created: 9, updated: 9, unchanged: 9, conflicts: 9, errors: 9 });
    expect(report).toMatchObject({ created: 1, unchanged: 2, conflicts: 1, failure: null });
    expect(report.rows[0]!.changes).toEqual([]);
    expect(reportOf({ report: null as never, created: 3, updated: 0, unchanged: 0, conflicts: 0, errors: 0 }).created).toBe(3);
  });
});

describe("LLM budgets (docs/11 §16)", () => {
  it("turns the editor's rows into the PUT body and refuses bad limits and duplicates", () => {
    const drafts = budgetDrafts([{ period: "month", stage: null, limitUsd: 50, spentUsd: 10, periodStart: null }]);
    expect(drafts).toEqual([{ period: "month", stage: null, limit: "50" }]);
    expect(budgetsBody([...drafts, { period: "day", stage: "Extract", limit: "2,5" }])).toEqual({ budgets: [{ period: "month", stage: null, limitUsd: 50 }, { period: "day", stage: "Extract", limitUsd: 2.5 }] });
    expect(budgetsBody([{ period: "day", stage: null, limit: "-1" }])).toEqual({ invalid: 0, reason: "limit" });
    expect(budgetsBody([...drafts, ...drafts])).toEqual({ invalid: 1, reason: "duplicate" });
    expect(budgetUse({ spentUsd: 10, limitUsd: 50 })).toBe(20);
    expect(budgetUse({ spentUsd: 80, limitUsd: 50 })).toBe(100);
    expect(budgetUse({ spentUsd: null, limitUsd: 1 })).toBeNull();
  });
});

describe("supplier settings", () => {
  it("keeps the Processing tab's overrides when the Settings tab saves", () => {
    const supplier = { groupId: null, settings: { review: { mode: "always" }, pipelineOverrides: [{ op: "disable", step: "duplicate" }] } } as unknown as Supplier;
    const body = settingsBody(settingsDraft(supplier), supplier.settings as never);
    expect(body.pipelineOverrides).toEqual([{ op: "disable", step: "duplicate" }]);
    expect(body.review).toEqual({ mode: "always" });
  });
});

describe("revision comparison", () => {
  it("lists the facts side by side in a fixed order", () => {
    const facts = revisionFacts({ documentNumber: "TA-1", status: "NeedsReview", createdAt: "2026-11-05T06:00:00Z", approvedAt: null, validFrom: "2026-11-05", validUntil: "2026-11-05", lineCount: 24 } as never);
    expect(facts.map(([key]) => key)).toEqual(["number", "validFrom", "validUntil", "lineCount", "status", "received", "approved"]);
    expect(facts[3]![1]).toBe("24");
  });
});
