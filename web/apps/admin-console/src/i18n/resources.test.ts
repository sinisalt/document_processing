import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import enCommon from "../../../../packages/app-shell/src/i18n/en/common.json";
import deCommon from "../../../../packages/app-shell/src/i18n/de/common.json";
import en from "./en/admin.json";
import de from "./de/admin.json";
import { documentStatuses, documentTypes, reprocessStages } from "@/lib/documents";
import { apiKeyScopes } from "@/components/tenant/ApiKeysTab";
import { channelTypes } from "@/components/tenant/ChannelsTab";
import { dataResidencies, llmStages } from "@/components/AddProviderDialog";

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
    ];
    expect(expected.filter((key) => !has(enKeys, key))).toEqual([]);
    const common = [...documentStatuses.map((status) => `documentStatus.${status}`), ...["SuperAdmin", "Operator", "TenantAdmin", "Reviewer", "Viewer"].map((role) => `roles.${role}`)];
    expect(common.filter((key) => !has(commonKeys, key))).toEqual([]);
  });
});
