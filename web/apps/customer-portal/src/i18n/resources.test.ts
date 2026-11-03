import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import enCommon from "../../../../packages/app-shell/src/i18n/en/common.json";
import en from "./en/portal.json";
import de from "./de/portal.json";

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
    if (statSync(path).isDirectory()) return sourceFiles(path);
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) && !name.endsWith(".gen.ts") ? [path] : [];
  });
}

const srcDir = join(__dirname, "..");
const enKeys = new Set(leaves(en as Tree));
const deKeys = new Set(leaves(de as Tree));
const commonKeys = new Set(leaves(enCommon as Tree));

/** Every literal key the portal passes to `t()` (template keys are covered by the enum lists below). */
function literalKeys(): string[] {
  const keys = new Set<string>();
  for (const file of sourceFiles(srcDir)) {
    // Comments may quote `t("key")` as an example; only code counts.
    const text = readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
    for (const match of text.matchAll(/\bt\(\s*"([^"]+)"/g)) keys.add(match[1]!);
    for (const match of text.matchAll(/labelKey:\s*"([^"]+)"/g)) keys.add(match[1]!);
  }
  return [...keys];
}

describe("portal i18n resources", () => {
  it("has the same keys in en and de", () => {
    expect([...enKeys].filter((key) => !deKeys.has(key))).toEqual([]);
    expect([...deKeys].filter((key) => !enKeys.has(key))).toEqual([]);
  });

  it("defines every literal key the screens use", () => {
    const missing = literalKeys().filter((key) => {
      if (key.startsWith("common:")) return !has(commonKeys, key.slice("common:".length));
      return !has(enKeys, key.replace(/^portal:/, ""));
    });
    expect(missing).toEqual([]);
  });

  it("defines the keys the screens build from enums", () => {
    const expected = [
      ...["needsReview", "processing", "approved", "rejected", "failed", "all"].flatMap((tab) => [`inbox.tabs.${tab}`, `inbox.empty.${tab}`]),
      ...["Unknown", "Invoice", "CreditNote", "PurchaseOrder", "DeliveryNote", "Statement", "Reminder", "Other"].map((type) => `documentType.${type}`),
      ...["Info", "Warning", "Blocking"].map((severity) => `flagSeverity.${severity}`),
      ...["Normalize", "Classify", "Extract", "Validate", "Split", "Synthesize"].map((stage) => `stages.${stage}`),
      ...["received", "stage_completed", "stage_failed", "flagged", "review_requested", "auto_approved", "approved", "rejected", "corrected", "reprocessed"].map((event) => `events.document.${event}`),
      ...["general", "members", "apiKeys", "channels", "ai", "types"].map((tab) => `settings.tabs.${tab}`),
      ...["none", "notValidated", "stale", "staleSettled"].map((state) => `review.flags.${state}`),
      // FlagCodes (src/DocProc.Domain/Flags/DocumentFlag.cs).
      ...[
        "UNREADABLE_PAGES",
        "TYPE_UNKNOWN",
        "SUPPLIER_UNMATCHED",
        "CUSTOMER_MISMATCH",
        "MISSING_REQUIRED",
        "TOTALS_MISMATCH",
        "TAX_MISMATCH",
        "LINE_ARITHMETIC",
        "LOW_CONFIDENCE",
        "DUPLICATE_SUSPECTED",
        "PROFILE_FALLBACK",
        "LLM_PROVIDER_UNAVAILABLE",
        "MULTI_DOCUMENT_SPLIT",
        "CURRENCY_INFERRED",
        "PO_UNMATCHED",
        "DATE_IMPLAUSIBLE",
        "INVALID_VALUE",
        "UNSUPPORTED_TYPE",
  "FIELD_REQUIRED",
  "FIELD_INVALID",
  "RULE_FAILED",
  "REVISION_OF",
      ].map((code) => `flagCodes.${code}`),
      ...["ApiUpload", "Mailbox", "WatchedFolder", "PortalUpload"].flatMap((type) => [`settings.channels.types.${type}`, `settings.channels.configHints.${type}`]),
      ...["Anthropic", "AnthropicFoundry", "OpenAi", "AzureOpenAi", "OpenAiCompatible"].map((kind) => `settings.ai.kinds.${kind}`),
      ...["ingest", "read"].flatMap((scope) => [`settings.apiKeys.scopes.${scope}`, `settings.apiKeys.scopeHints.${scope}`]),
      ...["name", "legalName", "slug", "vatId", "countryCode", "defaultCurrency", "timeZone"].map((key) => `settings.general.identity.${key}`),
    ];
    expect(expected.filter((key) => !has(enKeys, key))).toEqual([]);
  });
});
