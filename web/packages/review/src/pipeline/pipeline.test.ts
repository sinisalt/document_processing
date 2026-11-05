import { describe, expect, it } from "vitest";
import { controlOf, displayValue, missingRequired, paramFields, readValue, takesNoParams, withParam, type JsonSchema } from "./paramsSchema";
import { addDisable, lookupsOf, overridesOf, putLookup, removeOverride, setParams, settingsWithOverrides, stepIdProblem, stepsByPhase, type ResolvedStep } from "./overrides";
import { paramsText } from "./PipelineSteps";

// The schemas as GET /step-kinds and GET /rule-kinds send them (DocProc.Definitions StepKindCatalogue, RuleKinds).
const httpLookup: JsonSchema = {
  type: "object",
  required: ["destinationId", "response"],
  additionalProperties: false,
  properties: {
    destinationId: { type: "string", format: "uuid", minLength: 36, maxLength: 36 },
    method: { enum: ["GET", "POST"] },
    request: { type: "string", maxLength: 16384 },
    response: { type: "string", maxLength: 16384 },
    timeoutMs: { type: "integer", minimum: 100, maximum: 5000 },
    retries: { type: "integer", minimum: 0, maximum: 2 },
    onFailure: { enum: ["warn", "block", "ignore"] },
  },
};
const compare: JsonSchema = {
  type: "object",
  required: ["left", "op"],
  properties: {
    left: { type: "string", maxLength: 200 },
    op: { enum: ["eq", "ne", "lt", "le", "gt", "ge"] },
    value: { type: ["number", "string", "boolean"] },
    tolerance: { type: "number", minimum: 0 },
    scopeBy: { type: "array", items: { type: "string" } },
    then: { type: "object", properties: { path: { type: "string" } } },
  },
};

describe("parameter forms from a kind's schema", () => {
  it("maps each property to a control, required ones first", () => {
    const fields = paramFields(httpLookup);
    expect(fields.map((f) => f.name)).toEqual(["destinationId", "response", "method", "request", "timeoutMs", "retries", "onFailure"]);
    expect(Object.fromEntries(fields.map((f) => [f.name, f.control]))).toEqual({
      destinationId: "text",
      response: "expression",
      method: "enum",
      request: "expression",
      timeoutMs: "integer",
      retries: "integer",
      onFailure: "enum",
    });
    expect(fields.find((f) => f.name === "timeoutMs")).toMatchObject({ min: 100, max: 5000 });
    expect(fields.find((f) => f.name === "onFailure")?.options).toEqual(["warn", "block", "ignore"]);
  });

  it("treats rule expressions, scalars, lists and nested objects", () => {
    expect(controlOf("predicate", { type: "string", maxLength: 2000 }, ["predicate"])).toBe("expression");
    const fields = paramFields(compare);
    expect(fields.find((f) => f.name === "value")?.control).toBe("scalar");
    expect(fields.find((f) => f.name === "scopeBy")?.control).toBe("list");
    expect(fields.find((f) => f.name === "then")?.control).toBe("json");
    expect(paramFields({ type: "object", additionalProperties: false })).toEqual([]);
    expect(takesNoParams({ type: "object", additionalProperties: false })).toBe(true);
    expect(takesNoParams({ type: "object" })).toBe(false);
  });

  it("reads typed text into parameter values and refuses what does not fit", () => {
    const [timeout] = paramFields(httpLookup).filter((f) => f.name === "timeoutMs");
    expect(readValue(timeout!, "2500")).toEqual({ value: 2500 });
    expect(readValue(timeout!, "50")).toEqual({ error: "range" });
    expect(readValue(timeout!, "2.5")).toEqual({ error: "integer" });
    expect(readValue(timeout!, "  ")).toEqual({ value: undefined });
    const scalar = paramFields(compare).find((f) => f.name === "value")!;
    expect(readValue(scalar, "0,5")).toEqual({ value: 0.5 });
    expect(readValue(scalar, "true")).toEqual({ value: true });
    expect(readValue(scalar, "EUR")).toEqual({ value: "EUR" });
    const list = paramFields(compare).find((f) => f.name === "scopeBy")!;
    expect(readValue(list, "lines[*].unit, lines[*].sku\n")).toEqual({ value: ["lines[*].unit", "lines[*].sku"] });
    expect(displayValue(list, ["a", "b"])).toBe("a, b");
    const json = paramFields(compare).find((f) => f.name === "then")!;
    expect(readValue(json, "{bad")).toEqual({ error: "json" });
    expect(readValue(json, '{"path":"price"}')).toEqual({ value: { path: "price" } });
  });

  it("leaves an emptied parameter out and names the missing required ones", () => {
    const fields = paramFields(httpLookup);
    const values = withParam(withParam({}, "destinationId", "d-1"), "retries", 1);
    expect(withParam(values, "retries", undefined)).toEqual({ destinationId: "d-1" });
    expect(missingRequired(fields, values)).toEqual(["response"]);
  });
});

const step = (id: string, phase: string, extra: Partial<ResolvedStep> = {}): ResolvedStep => ({ id, kind: id, phase, params: {}, enabled: true, source: "default", fixed: false, runs: true, ...extra });

describe("supplier pipeline overrides", () => {
  it("reads only well-formed overrides and keeps every other setting when they are written", () => {
    const settings = { review: { mode: "always" }, pipelineOverrides: [{ op: "disable", step: "duplicate" }, { nonsense: true }, "x"] };
    expect(overridesOf(settings)).toEqual([{ op: "disable", step: "duplicate" }]);
    expect(overridesOf(null)).toEqual([]);
    expect(settingsWithOverrides(settings, [])).toEqual({ review: { mode: "always" } });
    expect(settingsWithOverrides({ locale: { culture: "de-DE" } }, [{ op: "disable", step: "llm" }])).toEqual({ locale: { culture: "de-DE" }, pipelineOverrides: [{ op: "disable", step: "llm" }] });
  });

  it("disables once, keeps one param override per step and drops it when emptied", () => {
    let list = addDisable([], "duplicate");
    list = addDisable(list, "duplicate");
    expect(list).toHaveLength(1);
    list = setParams(list, "llm", { effort: "high" });
    list = setParams(list, "llm", { effort: "low", maxCostPerDocument: 0.5 });
    expect(list).toEqual([
      { op: "disable", step: "duplicate" },
      { op: "param", step: "llm", params: { effort: "low", maxCostPerDocument: 0.5 } },
    ]);
    expect(setParams(list, "llm", {})).toEqual([{ op: "disable", step: "duplicate" }]);
    expect(removeOverride(list, 0)).toEqual([{ op: "param", step: "llm", params: { effort: "low", maxCostPerDocument: 0.5 } }]);
  });

  it("puts an http-lookup after an Extract step and replaces it by its id", () => {
    let list = putLookup([], "llm", "erpVendor", { destinationId: "d-1", response: "{}" });
    list = putLookup(list, "map", "erpVendor", { destinationId: "d-2", response: "{}" });
    expect(list).toEqual([{ op: "insertAfter", step: "map", with: { id: "erpVendor", kind: "http-lookup", params: { destinationId: "d-2", response: "{}" } } }]);
    expect(lookupsOf(list)).toEqual([{ index: 0, after: "map", id: "erpVendor", params: { destinationId: "d-2", response: "{}" } }]);
  });

  it("refuses a step id of the wrong form or one taken by a step or another lookup", () => {
    const steps = [step("llm", "Extract"), step("map", "Extract")];
    const list = putLookup([], "llm", "erpVendor", { destinationId: "d-1", response: "{}" });
    expect(stepIdProblem("1x", steps, list)).toBe("form");
    expect(stepIdProblem("llm", steps, list)).toBe("taken");
    expect(stepIdProblem("erpVendor", steps, list)).toBe("taken");
    expect(stepIdProblem("erpVendor", steps, list, "erpVendor")).toBeNull();
    expect(stepIdProblem("poOwner", steps, list)).toBeNull();
  });

  it("groups resolved steps by phase in the order a document runs them", () => {
    const groups = stepsByPhase([step("schema", "Validate"), step("split.heuristic", "Split"), step("llm", "Extract"), step("review-gate", "Validate", { fixed: true })]);
    expect(groups.map((g) => g.phase)).toEqual(["Split", "Extract", "Validate"]);
    expect(groups[2]!.steps.map((s) => s.id)).toEqual(["schema", "review-gate"]);
    expect(paramsText({ effort: "high", maxCostPerDocument: 0.5 })).toBe("effort: high · maxCostPerDocument: 0.5");
    expect(paramsText({})).toBe("");
  });
});
