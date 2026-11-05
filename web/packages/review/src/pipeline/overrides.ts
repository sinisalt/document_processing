import type { components } from "@docproc/api-client";

export type StepKind = components["schemas"]["StepKindResponse"];
export type RuleKind = components["schemas"]["RuleKindResponse"];
export type ResolvedPipeline = components["schemas"]["ResolvedPipelineResponse"];
export type ResolvedStep = components["schemas"]["ResolvedStepResponse"];
export type LookupTestResponse = components["schemas"]["LookupTestResponse"];

/** The phases in the order a document runs them (docs/11 §10.1). */
export const phases = ["Split", "Classify", "Match", "Extract", "Validate", "Publish"] as const;

export const HTTP_LOOKUP = "http-lookup";

/**
 * One entry of `settings.pipelineOverrides` (docs/11 §10.1, as built in P5 part A): `disable` a step, set its `param`s,
 * `insertAfter` a step (only an `http-lookup`, only in Extract) or `replace` one with another of the same kind.
 */
export interface PipelineOverride {
  op: "disable" | "param" | "insertAfter" | "replace";
  step: string;
  params?: Record<string, unknown>;
  with?: { id: string; kind: string; params?: Record<string, unknown> };
}

/** The overrides stored in a supplier's or group's settings; an empty list when there are none or they do not read. */
export function overridesOf(settings: unknown): PipelineOverride[] {
  const list = settings && typeof settings === "object" ? (settings as { pipelineOverrides?: unknown }).pipelineOverrides : undefined;
  return Array.isArray(list) ? list.filter((o): o is PipelineOverride => !!o && typeof o === "object" && typeof (o as PipelineOverride).op === "string" && typeof (o as PipelineOverride).step === "string") : [];
}

/** The settings with the overrides replaced (an empty list removes the member), every other member kept as stored. */
export function settingsWithOverrides(settings: unknown, overrides: readonly PipelineOverride[]): Record<string, unknown> {
  const next: Record<string, unknown> = settings && typeof settings === "object" ? { ...(settings as Record<string, unknown>) } : {};
  if (overrides.length === 0) delete next.pipelineOverrides;
  else next.pipelineOverrides = overrides;
  return next;
}

/** The steps of a resolved pipeline by phase, in the phases' order. */
export function stepsByPhase(steps: readonly ResolvedStep[]): { phase: string; steps: ResolvedStep[] }[] {
  return phases.map((phase) => ({ phase, steps: steps.filter((s) => s.phase === phase) })).filter((group) => group.steps.length > 0);
}

/** The steps an override may disable or set parameters of: every step but the fixed ones (docs/11 §10.1). */
export function overridableSteps(steps: readonly ResolvedStep[]): ResolvedStep[] {
  return steps.filter((s) => !s.fixed);
}

/** Where an `http-lookup` may go: after any step of Extract. */
export function lookupAnchors(steps: readonly ResolvedStep[]): ResolvedStep[] {
  return steps.filter((s) => s.phase === "Extract");
}

/** Disables a step (an override already disabling it is not added twice). */
export function addDisable(list: readonly PipelineOverride[], step: string): PipelineOverride[] {
  return list.some((o) => o.op === "disable" && o.step === step) ? [...list] : [...list, { op: "disable", step }];
}

/** Sets parameters of a step; the step's earlier `param` override is replaced, so each step has one. */
export function setParams(list: readonly PipelineOverride[], step: string, params: Record<string, unknown>): PipelineOverride[] {
  const rest = list.filter((o) => !(o.op === "param" && o.step === step));
  return Object.keys(params).length === 0 ? rest : [...rest, { op: "param", step, params }];
}

/** Adds an `http-lookup` after `after`, or replaces the stored one of the same id. */
export function putLookup(list: readonly PipelineOverride[], after: string, id: string, params: Record<string, unknown>): PipelineOverride[] {
  const entry: PipelineOverride = { op: "insertAfter", step: after, with: { id, kind: HTTP_LOOKUP, params } };
  const index = list.findIndex((o) => o.op === "insertAfter" && o.with?.id === id);
  if (index < 0) return [...list, entry];
  const next = [...list];
  next[index] = entry;
  return next;
}

export function removeOverride(list: readonly PipelineOverride[], index: number): PipelineOverride[] {
  return list.filter((_, i) => i !== index);
}

/** The `http-lookup` steps the overrides add. */
export function lookupsOf(list: readonly PipelineOverride[]): { index: number; after: string; id: string; params: Record<string, unknown> }[] {
  return list.flatMap((o, index) => (o.op === "insertAfter" && o.with?.kind === HTTP_LOOKUP ? [{ index, after: o.step, id: o.with.id, params: o.with.params ?? {} }] : []));
}

const STEP_ID = /^[A-Za-z][A-Za-z0-9._-]{0,63}$/;

/** Why a new step id cannot be used: its form, or a step of the pipeline or another lookup that has it already. */
export function stepIdProblem(id: string, steps: readonly ResolvedStep[], list: readonly PipelineOverride[], editing?: string): "form" | "taken" | null {
  if (!STEP_ID.test(id)) return "form";
  if (id === editing) return null;
  if (steps.some((s) => s.id === id) || list.some((o) => o.with?.id === id)) return "taken";
  return null;
}
