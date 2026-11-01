import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import i18next from "i18next";
import enCommon from "../../../../packages/app-shell/src/i18n/en/common.json";
import deCommon from "../../../../packages/app-shell/src/i18n/de/common.json";
import en from "./en/admin.json";
import de from "./de/admin.json";
import { documentStatuses, documentTypes, reprocessStages } from "@/lib/documents";
import { apiKeyScopes } from "@/components/tenant/ApiKeysTab";
import { channelTypes } from "@/components/tenant/ChannelsTab";
import { dataResidencies, llmStages } from "@/components/AddProviderDialog";
import { flagCodes } from "@/lib/review";

type Tree = { [key: string]: string | Tree };

function leaves(tree: Tree, prefix = ""): string[] {
  return Object.entries(tree).flatMap(([key, value]) => (typeof value === "string" ? [`${prefix}${key}`] : leaves(value, `${prefix}${key}.`)));
}

/** A key exists when it, or a plural form of it, is a leaf. */
function has(keys: Set<string>, key: string): boolean {
  return keys.has(key) || keys.has(`${key}_one`) || keys.has(`${key}_other`);
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return name === "test" ? [] : sourceFiles(path);
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) && !name.endsWith(".gen.ts") ? [path] : [];
  });
}

const srcDir = join(__dirname, "..");
const enKeys = new Set(leaves(en as Tree));
const deKeys = new Set(leaves(de as Tree));
const commonKeys = new Set(leaves(enCommon as Tree));

/** Every literal key the console passes to `t()`, `translated()` or a nav `labelKey` (enum-built keys are below). */
function literalKeys(): string[] {
  const keys = new Set<string>();
  for (const file of sourceFiles(srcDir)) {
    // Comments may quote `t("key")` as an example; only code counts.
    const text = readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
    for (const match of text.matchAll(/\bt\(\s*"([^"]+)"/g)) keys.add(match[1]!);
    for (const match of text.matchAll(/\btranslated\(\s*"([^"]+)"/g)) keys.add(match[1]!);
    for (const match of text.matchAll(/labelKey:\s*"([^"]+)"/g)) keys.add(match[1]!);
  }
  return [...keys];
}

describe("admin console i18n resources", () => {
  it("has the same keys in en and de", () => {
    expect([...enKeys].filter((key) => !deKeys.has(key))).toEqual([]);
    expect([...deKeys].filter((key) => !enKeys.has(key))).toEqual([]);
  });

  it("has the same shell (common) keys in en and de", () => {
    const deCommonKeys = new Set(leaves(deCommon as Tree));
    expect([...commonKeys].filter((key) => !deCommonKeys.has(key))).toEqual([]);
    expect([...deCommonKeys].filter((key) => !commonKeys.has(key))).toEqual([]);
  });

  it("never shows a raw flag or enum code inside a sentence", () => {
    // "Fields below it raise LOW_CONFIDENCE" put an API code in front of the user; codes are translated by key.
    const values = (tree: Tree, prefix = ""): [string, string][] =>
      Object.entries(tree).flatMap(([key, value]) => (typeof value === "string" ? [[`${prefix}${key}`, value] as [string, string]] : values(value, `${prefix}${key}.`)));
    for (const bundle of [en, de]) {
      const offending = values(bundle as Tree).filter(([, text]) => /\b[A-Z]{2,}(?:_[A-Z]+)+\b/.test(text));
      expect(offending).toEqual([]);
    }
  });

  it("defines every literal key the screens use", () => {
    const literal = literalKeys();
    expect(literal.length).toBeGreaterThan(100);
    const missing = literal.filter((key) => {
      if (key.startsWith("common:")) return !has(commonKeys, key.slice("common:".length));
      // i18next falls back to `common` (createI18n's fallbackNS), so a bare key may live in either namespace.
      const bare = key.replace(/^admin:/, "");
      return !has(enKeys, bare) && !(key === bare && has(commonKeys, bare));
    });
    expect(missing).toEqual([]);
  });

  it("defines the keys the screens build from enums", () => {
    const expected = [
      ...reprocessStages.map((stage) => `stages.${stage}`),
      ...llmStages.map((stage) => `stages.${stage}`),
      ...documentTypes.map((type) => `documentType.${type}`),
      ...["Info", "Warning", "Blocking"].map((severity) => `flagSeverity.${severity}`),
      ...["received", "stage_completed", "stage_failed", "flagged", "review_requested", "auto_approved", "approved", "rejected", "corrected", "reprocessed"].map((kind) => `timeline.eventType.${kind}`),
      ...apiKeyScopes.map((scope) => `apiKeys.scope.${scope}`),
      ...channelTypes.map((type) => `channels.types.${type}`),
      ...dataResidencies.map((residency) => `llm.residency.${residency}`),
      ...flagCodes.map((code) => `flagCodes.${code}`),
    ];
    expect(expected.filter((key) => !has(enKeys, key))).toEqual([]);
    const common = [...documentStatuses.map((status) => `documentStatus.${status}`), ...["SuperAdmin", "Operator", "TenantAdmin", "Reviewer", "Viewer"].map((role) => `roles.${role}`)];
    expect(common.filter((key) => !has(commonKeys, key))).toEqual([]);
  });
});

describe("usage totals", () => {
  it("pluralises each count on its own ('1 error', not '1 errors')", async () => {
    const instance = i18next.createInstance();
    await instance.init({ lng: "en", fallbackLng: "en", defaultNS: "admin", resources: { en: { admin: en }, de: { admin: de } }, interpolation: { escapeValue: false } });
    const line = (lng: string, calls: number, errors: number) =>
      instance.t("usage.total", {
        lng,
        calls: instance.t("usage.totalCalls", { lng, count: calls, value: String(calls) }),
        errors: instance.t("usage.totalErrors", { lng, count: errors, value: String(errors) }),
        cost: "$1.78",
      });
    expect(line("en", 47, 1)).toBe("Total: 47 calls · 1 error · $1.78");
    expect(line("en", 1, 2)).toBe("Total: 1 call · 2 errors · $1.78");
    expect(line("de", 47, 1)).toBe("Gesamt: 47 Aufrufe · 1 Fehler · $1.78");
  });
});
