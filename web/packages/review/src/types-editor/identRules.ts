import type { components } from "@docproc/api-client";

export type TenantRule = components["schemas"]["TypeIdentificationRuleResponse"];
export type TenantRuleVersion = components["schemas"]["TypeIdentificationRuleVersionResponse"];
export type TenantRuleTest = components["schemas"]["TenantRuleTestReport"];

/** The versions of a tenant rule, newest first. */
export function versionsOf(rule: TenantRule): TenantRuleVersion[] {
  return [...rule.versions].sort((a, b) => Number(b.version) - Number(a.version));
}

/** The rule's Draft version, the one that can be edited, tested and activated; null when it has none. */
export function draftOf(rule: TenantRule): TenantRuleVersion | null {
  return versionsOf(rule).find((v) => v.state === "Draft") ?? null;
}

/** The version a new Draft starts from: the active one, else the newest. */
export function baseOf(rule: TenantRule): TenantRuleVersion | null {
  const versions = versionsOf(rule);
  return versions.find((v) => Number(v.version) === Number(rule.activeVersion)) ?? versions[0] ?? null;
}

/** How often reviewers overrode the type the rule gave, as a share of its matches (0 when it never matched). */
export function overrideRate(rule: Pick<TenantRule, "matched" | "overridden">): number {
  const matched = Number(rule.matched);
  const overridden = Number(rule.overridden);
  const base = Math.max(matched, overridden);
  return base === 0 ? 0 : overridden / base;
}

/** A rule as JSON text for the editor. */
export function ruleText(rule: unknown): string {
  return rule === undefined || rule === null ? "" : JSON.stringify(rule, null, 2);
}

/** The editor's text read as a rule object (`{all|any|none: [...]}`), or why it does not read. */
export function parseRule(text: string): { rule: Record<string, unknown> } | { error: "json" | "shape" } {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return { error: "json" };
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) return { error: "shape" };
  const keys = Object.keys(value);
  if (keys.length === 0 || !keys.some((k) => k === "all" || k === "any")) return { error: "shape" };
  return { rule: value as Record<string, unknown> };
}

/** The 409 of `:activate` when the test matched documents of other types: the test it carries, or null for any other error. */
export function otherTypesTest(problem: unknown): TenantRuleTest | null {
  if (!problem || typeof problem !== "object") return null;
  const test = (problem as { test?: unknown }).test;
  return test && typeof test === "object" && typeof (test as TenantRuleTest).summary === "string" ? (test as TenantRuleTest) : null;
}
