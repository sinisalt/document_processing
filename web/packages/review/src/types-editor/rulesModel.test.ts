import { describe, expect, it } from "vitest";
import type { Definition } from "./model";
import { addRule, flagIssues, moveRule, newFlagCode, removeFlag, removeRule, reviewOf, ruleIssues, rulesOf, setFlags, setReview, updateRule } from "./rulesModel";
import { baseOf, draftOf, otherTypesTest, overrideRate, parseRule, versionsOf, type TenantRule } from "./identRules";

const priceList = (): Definition => ({
  key: "price-list",
  version: 2,
  name: { en: "Price list" },
  fields: [],
  rules: [
    { id: "r_complete", kind: "tableComplete", params: { path: "lines" } },
    { id: "r_delta", kind: "deltaToPrevious", params: { path: "lines[*].unitPrice", joinOn: "supplierSku" }, flag: "X_PRICE_JUMP" },
  ],
  flags: [{ code: "X_PRICE_JUMP", severity: "warning", message: { en: "Price jump", de: "Preissprung" } }],
  processing: { review: { autoApprove: true, requirePassedRules: ["r_complete", "r_delta"], blockOnWarnings: ["r_delta"] } },
});

const kinds = new Map([
  ["tableComplete", { required: ["path"] }],
  ["deltaToPrevious", { required: ["path", "joinOn"] }],
  ["compare", { required: ["left", "op"] }],
]);

describe("the rule list of a definition", () => {
  it("adds a rule of a kind with a free id and reports its missing parameters", () => {
    const { definition, index } = addRule(priceList(), "compare");
    expect(index).toBe(2);
    expect(rulesOf(definition)[2]).toEqual({ id: "compare", kind: "compare" });
    expect(addRule(definition, "compare").definition.rules).toContainEqual({ id: "compare2", kind: "compare" });
    expect(ruleIssues(definition, kinds)).toEqual([
      { index: 2, code: "required", detail: "left" },
      { index: 2, code: "required", detail: "op" },
    ]);
  });

  it("renames a rule in the review gate's lists and removes it from them", () => {
    const renamed = updateRule(priceList(), 1, { id: "r_prices" });
    expect(reviewOf(renamed)).toMatchObject({ requirePassedRules: ["r_complete", "r_prices"], blockOnWarnings: ["r_prices"] });
    const removed = removeRule(renamed, 1);
    expect(rulesOf(removed).map((r) => r.id)).toEqual(["r_complete"]);
    expect(reviewOf(removed)).toEqual({ autoApprove: true, requirePassedRules: ["r_complete"] });
  });

  it("drops emptied members rather than storing nulls the meta-schema refuses", () => {
    const changed = updateRule(priceList(), 1, { flag: null, severity: null, appliesWhen: "" });
    expect(rulesOf(changed)[1]).toEqual({ id: "r_delta", kind: "deltaToPrevious", params: { path: "lines[*].unitPrice", joinOn: "supplierSku" } });
  });

  it("moves rules and reports ids, duplicates, undeclared flags and unknown kinds", () => {
    expect(rulesOf(moveRule(priceList(), 1, -1)).map((r) => r.id)).toEqual(["r_delta", "r_complete"]);
    expect(moveRule(priceList(), 0, -1)).toEqual(priceList());
    let bad = updateRule(priceList(), 0, { id: "r_delta" });
    bad = updateRule(bad, 1, { flag: "X_NOPE" });
    bad = addRule(bad, "nosuch").definition;
    expect(ruleIssues(bad, kinds).map((i) => i.code)).toEqual(["duplicate", "flag", "kind"]);
    expect(ruleIssues(updateRule(priceList(), 0, { id: "1st" }), kinds).map((i) => i.code)).toEqual(["id"]);
  });
});

describe("definition flags and the review gate", () => {
  it("adds a free code, checks codes and removes a flag with the rules' references", () => {
    expect(newFlagCode(priceList())).toBe("X_FLAG");
    const flagged = setFlags(priceList(), [...(priceList().flags as never[]), { code: "x_lower" }, { code: "X_PRICE_JUMP" }]);
    expect(flagIssues(flagged)).toEqual([
      { code: "x_lower", problem: "form" },
      { code: "X_PRICE_JUMP", problem: "duplicate" },
    ]);
    const removed = removeFlag(priceList(), "X_PRICE_JUMP");
    expect(removed.flags).toBeUndefined();
    expect(rulesOf(removed)[1]!.flag).toBeUndefined();
  });

  it("writes the gate keys and leaves out empty lists and empty line requirements", () => {
    const gate = setReview(priceList(), { ...reviewOf(priceList()), blockOnWarnings: [], priority: "urgent", dueBy: "validUntil", lines: { requireSource: [], requireLocated: false } });
    expect(reviewOf(gate)).toEqual({ autoApprove: true, requirePassedRules: ["r_complete", "r_delta"], priority: "urgent", dueBy: "validUntil" });
    const lines = setReview(priceList(), { lines: { requireSource: ["Rule", "Human"], requireLocated: true, maxLowConfidenceRows: 0 } });
    expect(reviewOf(lines).lines).toEqual({ requireSource: ["Rule", "Human"], requireLocated: true, maxLowConfidenceRows: 0 });
  });
});

const tenantRule = (versions: Array<{ version: number; state: string }>, extra: Partial<TenantRule> = {}): TenantRule =>
  ({
    id: "r1",
    documentTypeKey: "price-list",
    name: "Preisliste im Titel",
    priority: 100,
    activeVersion: 1,
    status: "Active",
    createdFromDocumentId: null,
    matched: 40,
    overridden: 4,
    createdBy: null,
    createdAt: "2026-11-01T00:00:00Z",
    updatedAt: "2026-11-01T00:00:00Z",
    versions: versions.map((v) => ({ id: `v${v.version}`, version: v.version, state: v.state, rule: { all: [] }, describe: "", ruleHash: "", excludedSupplierIds: [], test: null, revision: 1, approvedBy: null, approvedAt: null, createdBy: null, createdAt: "2026-11-01T00:00:00Z" })),
    ...extra,
  }) as TenantRule;

describe("tenant fallback rules", () => {
  it("finds the draft and the version a new draft starts from", () => {
    const rule = tenantRule([
      { version: 1, state: "Active" },
      { version: 2, state: "Draft" },
    ]);
    expect(versionsOf(rule).map((v) => Number(v.version))).toEqual([2, 1]);
    expect(Number(draftOf(rule)!.version)).toBe(2);
    expect(Number(baseOf(rule)!.version)).toBe(1);
    expect(draftOf(tenantRule([{ version: 1, state: "Active" }]))).toBeNull();
    expect(overrideRate(rule)).toBeCloseTo(0.1);
    expect(overrideRate({ matched: 0, overridden: 0 })).toBe(0);
  });

  it("reads a rule from the editor's text and the other-types test from a 409", () => {
    expect(parseRule('{"all":[{"text":"Preisliste"}]}')).toEqual({ rule: { all: [{ text: "Preisliste" }] } });
    expect(parseRule("{")).toEqual({ error: "json" });
    expect(parseRule('{"none":[]}')).toEqual({ error: "shape" });
    expect(parseRule("[]")).toEqual({ error: "shape" });
    expect(otherTypesTest({ title: "Matches other types", test: { summary: "would have matched 3 price-list documents, 2 other documents" } })?.summary).toContain("2 other");
    expect(otherTypesTest({ title: "Not a draft" })).toBeNull();
  });
});
