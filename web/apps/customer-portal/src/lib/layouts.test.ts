import { describe, expect, it } from "vitest";
import {
  EMPTY_DRAFT,
  appliedEvidence,
  appliedLayout,
  evidenceBoxes,
  flagSuggestions,
  isLayoutKey,
  learnLayoutRequest,
  leadVersion,
  locationOf,
  newChip,
  openLayoutFlag,
  prefillOf,
  replayRows,
  ruleOf,
  suggestLayoutKey,
  toggleCaseSensitive,
  warmupLeft,
  widen,
  type LayoutSaveChoice,
  type SupplierLayout,
} from "./layouts";
import type { DocumentFlag } from "./queries";

const flag = (code: string, patch: Partial<DocumentFlag> = {}): DocumentFlag =>
  ({ id: code, code, severity: "Blocking", field: null, message: code, params: {}, createdBy: "System", createdAt: "2026-11-03T10:00:00Z", resolvedAt: null, resolvedBy: null, ...patch }) as DocumentFlag;

const choice = (patch: Partial<LayoutSaveChoice> = {}): LayoutSaveChoice => ({ saveAsLayout: true, saveAs: "newLayout", targetLayoutId: null, layoutKey: "nordwind-invoice-webshop", name: "", pendingLayoutVersionId: null, confirmNotRecognised: false, ...patch });

describe("layout flags", () => {
  it("takes the open NEW_LAYOUT first, then LAYOUT_AMBIGUOUS, and ignores resolved ones", () => {
    expect(openLayoutFlag({ flags: [flag("LAYOUT_AMBIGUOUS"), flag("NEW_LAYOUT")] })?.code).toBe("NEW_LAYOUT");
    expect(openLayoutFlag({ flags: [flag("NEW_LAYOUT", { resolvedAt: "2026-11-03T11:00:00Z" }), flag("LAYOUT_AMBIGUOUS")] })?.code).toBe("LAYOUT_AMBIGUOUS");
    expect(openLayoutFlag({ flags: [flag("TOTALS_MISMATCH")] })).toBeNull();
  });

  it("reads the suggestions a NEW_LAYOUT flag carries, and survives a malformed list", () => {
    const suggestions = [{ layoutId: "l1", layoutKey: "nordwind-invoice-erp", documentTypeKey: "Invoice", similarity: 0.78 }];
    expect(flagSuggestions(flag("NEW_LAYOUT", { params: { suggestions: JSON.stringify(suggestions) } }))).toEqual(suggestions);
    expect(flagSuggestions(flag("NEW_LAYOUT", { params: { suggestions: "{oops" } }))).toEqual([]);
    expect(flagSuggestions(null)).toEqual([]);
  });
});

describe("identification draft (docs/11 §12.9)", () => {
  it("makes no rule without a positive chip, and puts negative chips under none", () => {
    expect(ruleOf(EMPTY_DRAFT)).toBeNull();
    const title = newChip({ text: "RECHNUNG / INVOICE", page: 1, region: "header" }, "'RECHNUNG / INVOICE' in the page-1 header");
    const vat = newChip({ supplierValue: "vatId", page: "last" }, "the supplier's VAT id on the last page");
    const credit = { ...newChip({ text: "Gutschrift", page: 1 }, "'Gutschrift' on page 1"), negative: true };
    expect(ruleOf({ connective: "all", chips: [{ ...title, negative: true }] })).toBeNull();
    expect(ruleOf({ connective: "all", chips: [title, vat] })).toEqual({ all: [title.identifier, vat.identifier] });
    expect(ruleOf({ connective: "any", chips: [title, credit] })).toEqual({ any: [title.identifier], none: [credit.identifier] });
    expect(title.id).not.toBe(vat.id);
  });

  it("widens a box or a region to its page, then to any page", () => {
    const boxed = { text: "Rechnung", page: 1, bBox: [10, 20, 30, 40], tolerance: 5 };
    expect(locationOf(boxed)).toBe("area");
    expect(widen(boxed)).toEqual({ text: "Rechnung", page: 1 });
    expect(locationOf({ text: "Rechnung", page: 1, region: "title" })).toBe("region");
    expect(widen({ text: "Rechnung", page: 1, region: "title" })).toEqual({ text: "Rechnung", page: 1 });
    expect(locationOf(widen(boxed))).toBe("page");
    expect(widen({ text: "Rechnung", page: 1 })).toEqual({ text: "Rechnung" });
    expect(locationOf({ text: "Rechnung" })).toBe("anyPage");
  });

  it("toggles case sensitivity", () => {
    expect(toggleCaseSensitive({ text: "ERP" })).toEqual({ text: "ERP", caseSensitive: true });
    expect(toggleCaseSensitive({ text: "ERP", caseSensitive: true })).toEqual({ text: "ERP" });
  });

  it("draws evidence of one page from bBox arrays and the applied layout's evidence", () => {
    const evidence = [
      { page: 1, bBox: [100, 50, 300, 70], description: "'Rechnung' in the title", positive: true },
      { page: 2, bBox: [10, 900, 200, 910], description: "the VAT id" },
      { page: 1, bBox: null, description: "no place" },
    ];
    expect(evidenceBoxes(evidence, 1)).toEqual([{ box: { x0: 100, y0: 50, x1: 300, y1: 70 }, label: "'Rechnung' in the title", positive: true }]);
    expect(evidenceBoxes(evidence, 2)).toHaveLength(1);
    expect(appliedEvidence({ layout: { identification: evidence } })).toHaveLength(3);
    expect(appliedEvidence(null)).toEqual([]);
  });
});

describe("applied config", () => {
  it("names the layout that filled a document", () => {
    expect(appliedLayout({ layout: { layoutId: "l1", layoutKey: "nordwind-invoice-erp", version: 2 } })).toEqual({ layoutId: "l1", layoutKey: "nordwind-invoice-erp", version: 2 });
    expect(appliedLayout({ prefill: {} })).toBeNull();
  });

  it("reads where each pre-filled value came from", () => {
    expect(prefillOf({ prefill: { sources: { invoiceNumber: "definitionLabel" }, filledByLlm: false, basisVersionId: null } })).toEqual({ sources: { invoiceNumber: "definitionLabel" }, filledByLlm: false, basisVersionId: null });
    expect(prefillOf({ layout: {} })).toBeNull();
  });
});

describe("layout keys", () => {
  it("suggests the supplier's first word and the type, numbered when taken", () => {
    expect(suggestLayoutKey("Nordwind Großhandel GmbH", "Invoice", [])).toBe("nordwind-invoice");
    expect(suggestLayoutKey("Nordwind Großhandel GmbH", "Invoice", ["nordwind-invoice"])).toBe("nordwind-invoice-2");
    expect(suggestLayoutKey("Bäckerei Müller", "price-list", [])).toBe("backerei-price-list");
    expect(suggestLayoutKey(null, "Invoice", [])).toBe("supplier-invoice");
  });

  it("accepts what the API accepts", () => {
    expect(isLayoutKey("nordwind-invoice-webshop")).toBe(true);
    expect(isLayoutKey("a")).toBe(true);
    expect(isLayoutKey("-nordwind")).toBe(false);
    expect(isLayoutKey("nordwind-")).toBe(false);
    expect(isLayoutKey("Nordwind")).toBe(false);
    expect(isLayoutKey("x".repeat(65))).toBe(false);
  });
});

describe("approve and save as layout (docs/11 §12.10 step 6)", () => {
  it("sends nothing when the box is unticked", () => {
    expect(learnLayoutRequest(choice({ saveAsLayout: false }), EMPTY_DRAFT)).toBeNull();
  });

  it("saves a new layout under its key, with the marked rule or none (the API proposes one)", () => {
    expect(learnLayoutRequest(choice(), EMPTY_DRAFT)).toEqual({ saveAs: "newLayout", layoutKey: "nordwind-invoice-webshop" });
    const chip = newChip({ text: "RECHNUNG / INVOICE", page: 1, region: "header" }, "title");
    expect(learnLayoutRequest(choice({ name: " Web shop " }), { connective: "all", chips: [chip] })).toEqual({ saveAs: "newLayout", layoutKey: "nordwind-invoice-webshop", name: "Web shop", identification: { all: [chip.identifier] } });
  });

  it("confirms a pending Candidate without a key, and saves a new version with its confirmation", () => {
    expect(learnLayoutRequest(choice({ pendingLayoutVersionId: "v1" }), EMPTY_DRAFT)).toEqual({ pendingLayoutVersionId: "v1" });
    expect(learnLayoutRequest(choice({ saveAs: "newVersion", targetLayoutId: "l1", confirmNotRecognised: true }), EMPTY_DRAFT)).toEqual({ saveAs: "newVersion", targetLayoutId: "l1", confirmNotRecognised: true });
  });
});

describe("layout versions", () => {
  const layout = (activeVersion: number | null) =>
    ({
      activeVersion,
      versions: [
        { version: 2, status: "Candidate", reviewWarmup: 5, warmupSeen: 0 },
        { version: 1, status: "Active", reviewWarmup: 5, warmupSeen: 2 },
      ],
    }) as unknown as SupplierLayout;

  it("leads with the Active version, else the newest", () => {
    expect(Number(leadVersion(layout(1))?.version)).toBe(1);
    expect(Number(leadVersion(layout(null))?.version)).toBe(2);
  });

  it("says how much of the warm-up is left", () => {
    expect(warmupLeft({ reviewWarmup: 5, warmupSeen: 2 })).toEqual({ seen: 2, of: 5 });
    expect(warmupLeft({ reviewWarmup: 5, warmupSeen: 5 })).toBeNull();
    expect(warmupLeft({ reviewWarmup: 0, warmupSeen: 0 })).toBeNull();
  });

  it("lists the replay's fields lowest agreement first", () => {
    const rows = replayRows({ fields: [{ path: "invoiceNumber", agreement: 1, hits: 3, mismatches: 0, missing: 0 }, { path: "totalGross", agreement: 0.666, hits: 2, mismatches: 1, missing: 0 }] } as never);
    expect(rows.map((r) => [r.path, r.agreement])).toEqual([
      ["totalGross", 67],
      ["invoiceNumber", 100],
    ]);
    expect(replayRows(null)).toEqual([]);
  });
});
