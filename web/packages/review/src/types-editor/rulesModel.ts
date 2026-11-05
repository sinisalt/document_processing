import type { Definition, LabelMap } from "./model";

/**
 * The rule list, the definition's own flags and the review gate of a type definition (docs/11 §5.2, §11, P5): what the
 * type editor's Rules tab edits. Plain JSON, kept as the definition holds it, so the editor saves what it read plus the
 * change. Pure, so each operation is tested without rendering.
 */
export interface RuleEntry {
  id: string;
  kind: string;
  params?: Record<string, unknown> | null;
  severity?: "blocking" | "warning" | "info" | null;
  message?: LabelMap | null;
  appliesWhen?: string | null;
  flag?: string | null;
  [other: string]: unknown;
}

export interface FlagEntry {
  code: string;
  severity?: "blocking" | "warning" | "info" | null;
  message?: LabelMap | null;
  description?: string | null;
}

export interface LinesGate {
  requireSource?: string[];
  requireLocated?: boolean;
  maxLowConfidenceRows?: number;
}

export interface ReviewGate {
  autoApprove?: boolean;
  minConfidence?: number;
  priority?: "normal" | "high" | "urgent";
  dueBy?: "validFrom" | "validUntil";
  requirePassedRules?: string[];
  blockOnWarnings?: string[];
  lines?: LinesGate;
  [other: string]: unknown;
}

export const severities = ["blocking", "warning", "info"] as const;
export const priorities = ["normal", "high", "urgent"] as const;
export const dueByRoles = ["validFrom", "validUntil"] as const;
/** The sources a row may come from for `review.lines.requireSource` (docs/11 §11). */
export const lineSources = ["Rule", "Structured", "Human", "Llm", "Lookup"] as const;

export const RULE_ID = /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/;
export const FLAG_CODE = /^X_[A-Z0-9_]{1,62}$/;

export function rulesOf(definition: Definition): RuleEntry[] {
  return Array.isArray(definition.rules) ? (definition.rules as RuleEntry[]) : [];
}

export function flagsOf(definition: Definition): FlagEntry[] {
  return Array.isArray(definition.flags) ? (definition.flags as FlagEntry[]) : [];
}

export function reviewOf(definition: Definition): ReviewGate {
  const processing = definition.processing as { review?: ReviewGate } | undefined;
  return processing?.review ?? {};
}

function withRules(definition: Definition, rules: RuleEntry[]): Definition {
  const next = { ...definition, rules } as Definition;
  return next;
}

/** A rule id not used yet, from the kind (`compare`, `compare2`, ...). */
export function newRuleId(definition: Definition, kind: string): string {
  const base = kind.replace(/[^A-Za-z0-9]/g, "").replace(/^[^a-z]+/, "") || "rule";
  const stem = base.charAt(0).toLowerCase() + base.slice(1);
  const taken = new Set(rulesOf(definition).map((r) => r.id));
  if (!taken.has(stem)) return stem;
  for (let n = 2; ; n++) if (!taken.has(`${stem}${n}`)) return `${stem}${n}`;
}

export function addRule(definition: Definition, kind: string): { definition: Definition; index: number } {
  const rules = [...rulesOf(definition), { id: newRuleId(definition, kind), kind }];
  return { definition: withRules(definition, rules), index: rules.length - 1 };
}

/** Changes one rule; a renamed rule is renamed in the review gate's lists too, so they keep naming it. */
export function updateRule(definition: Definition, index: number, patch: Partial<RuleEntry>): Definition {
  const rules = rulesOf(definition);
  const before = rules[index];
  if (!before) return definition;
  const after = clean({ ...before, ...patch });
  let next = withRules(definition, rules.map((r, i) => (i === index ? after : r)));
  if (after.id !== before.id) {
    const review = reviewOf(next);
    const rename = (list?: string[]) => list?.map((id) => (id === before.id ? after.id : id));
    next = setReview(next, { ...review, requirePassedRules: rename(review.requirePassedRules), blockOnWarnings: rename(review.blockOnWarnings) });
  }
  return next;
}

/** Removes a rule and its mentions in the review gate's lists. */
export function removeRule(definition: Definition, index: number): Definition {
  const rules = rulesOf(definition);
  const removed = rules[index];
  if (!removed) return definition;
  const next = withRules(definition, rules.filter((_, i) => i !== index));
  const review = reviewOf(next);
  const drop = (list?: string[]) => list?.filter((id) => id !== removed.id);
  return setReview(next, { ...review, requirePassedRules: drop(review.requirePassedRules), blockOnWarnings: drop(review.blockOnWarnings) });
}

export function moveRule(definition: Definition, index: number, delta: -1 | 1): Definition {
  const rules = [...rulesOf(definition)];
  const target = index + delta;
  if (target < 0 || target >= rules.length) return definition;
  [rules[index], rules[target]] = [rules[target]!, rules[index]!];
  return withRules(definition, rules);
}

export function setFlags(definition: Definition, flags: FlagEntry[]): Definition {
  const next = { ...definition } as Definition;
  if (flags.length === 0) delete next.flags;
  else next.flags = flags.map((f) => clean({ ...f }) as FlagEntry);
  return next;
}

/** A flag code not used yet (`X_FLAG`, `X_FLAG_2`, ...). */
export function newFlagCode(definition: Definition): string {
  const taken = new Set(flagsOf(definition).map((f) => f.code));
  if (!taken.has("X_FLAG")) return "X_FLAG";
  for (let n = 2; ; n++) if (!taken.has(`X_FLAG_${n}`)) return `X_FLAG_${n}`;
}

/** Removes a flag and the rules' references to it (they raise their kind's codes again). */
export function removeFlag(definition: Definition, code: string): Definition {
  const next = setFlags(definition, flagsOf(definition).filter((f) => f.code !== code));
  return withRules(next, rulesOf(next).map((r) => (r.flag === code ? (clean({ ...r, flag: null }) as RuleEntry) : r)));
}

/** Replaces the review gate (`processing.review`); empty lists and unset members are left out. */
export function setReview(definition: Definition, review: ReviewGate): Definition {
  const processing = { ...((definition.processing as Record<string, unknown> | undefined) ?? {}) };
  const next: ReviewGate = clean({ ...review }) as ReviewGate;
  for (const key of ["requirePassedRules", "blockOnWarnings"] as const) if (Array.isArray(next[key]) && next[key]!.length === 0) delete next[key];
  if (next.lines) {
    const lines = clean({ ...next.lines }) as LinesGate;
    if (Array.isArray(lines.requireSource) && lines.requireSource.length === 0) delete lines.requireSource;
    if (lines.requireLocated === false) delete lines.requireLocated;
    if (Object.keys(lines).length === 0) delete next.lines;
    else next.lines = lines;
  }
  processing.review = next;
  return { ...definition, processing } as Definition;
}

/** An object without its null, undefined and empty-string members (what the meta-schema would refuse as `null`). */
function clean<T extends Record<string, unknown>>(value: T): T {
  for (const key of Object.keys(value)) {
    const member = value[key];
    if (member === null || member === undefined || member === "") delete value[key];
    else if (typeof member === "object" && !Array.isArray(member) && Object.keys(member as object).length === 0 && key === "message") delete value[key];
  }
  return value;
}

export interface RuleIssue {
  index: number;
  code: "id" | "duplicate" | "flag" | "kind" | "required";
  detail?: string;
}

/** What keeps the rule list from saving: ids, duplicates, an undeclared flag, an unknown kind, missing required parameters. */
export function ruleIssues(definition: Definition, kinds: ReadonlyMap<string, { required: readonly string[] }>): RuleIssue[] {
  const issues: RuleIssue[] = [];
  const seen = new Set<string>();
  const declared = new Set(flagsOf(definition).map((f) => f.code));
  rulesOf(definition).forEach((rule, index) => {
    if (!RULE_ID.test(rule.id)) issues.push({ index, code: "id" });
    else if (seen.has(rule.id)) issues.push({ index, code: "duplicate" });
    seen.add(rule.id);
    if (rule.flag && !declared.has(rule.flag)) issues.push({ index, code: "flag", detail: rule.flag });
    const kind = kinds.get(rule.kind);
    if (kinds.size > 0 && !kind) issues.push({ index, code: "kind", detail: rule.kind });
    for (const name of kind?.required ?? []) {
      const value = rule.params?.[name];
      if (value === undefined || value === null || value === "") issues.push({ index, code: "required", detail: name });
    }
  });
  return issues;
}

/** What keeps the flags block from saving: a code of the wrong form or used twice. */
export function flagIssues(definition: Definition): { code: string; problem: "form" | "duplicate" }[] {
  const seen = new Set<string>();
  return flagsOf(definition).flatMap((flag) => {
    const problem = !FLAG_CODE.test(flag.code) ? "form" : seen.has(flag.code) ? "duplicate" : null;
    seen.add(flag.code);
    return problem ? [{ code: flag.code, problem }] : [];
  });
}
