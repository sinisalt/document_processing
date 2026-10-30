import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import enCommon from "../../../../packages/app-shell/src/i18n/en/common.json";
import en from "./en/portal.json";
import de from "./de/portal.json";
import { fieldLabelKey } from "@/components/review/CanonicalForm";
import { allSpecs, type CanonicalInvoice } from "@/lib/canonical";

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
      ...["document", "supplier", "customer", "totals", "payment", "notes"].map((section) => `review.sections.${section}`),
      ...["received", "stage_completed", "stage_failed", "flagged", "review_requested", "auto_approved", "approved", "rejected", "corrected", "reprocessed"].map((event) => `events.document.${event}`),
      ...["Invoice", "CreditNote", "Proforma", "BankTransfer", "DirectDebit", "Card", "Cash", "Other"].map((value) => `enums.${value}`),
      ...["general", "members", "apiKeys", "channels", "ai"].map((tab) => `settings.tabs.${tab}`),
      ...["none", "notValidated", "stale"].map((state) => `review.flags.${state}`),
      ...["ApiUpload", "Mailbox", "WatchedFolder", "PortalUpload"].flatMap((type) => [`settings.channels.types.${type}`, `settings.channels.configHints.${type}`]),
      ...["Anthropic", "AnthropicFoundry", "OpenAi", "AzureOpenAi", "OpenAiCompatible"].map((kind) => `settings.ai.kinds.${kind}`),
      ...["ingest", "read"].flatMap((scope) => [`settings.apiKeys.scopes.${scope}`, `settings.apiKeys.scopeHints.${scope}`]),
      ...["name", "legalName", "slug", "vatId", "countryCode", "defaultCurrency", "timeZone"].map((key) => `settings.general.identity.${key}`),
    ];
    expect(expected.filter((key) => !has(enKeys, key))).toEqual([]);
  });

  it("labels every field of the review form", () => {
    const field = { value: null, status: "Missing" as const };
    const canonical: CanonicalInvoice = { orderReferences: [field], deliveryNoteReferences: [field], lines: [{ lineNumber: 1 }] };
    const paths = [...allSpecs(canonical).map((spec) => spec.path), "supplier", "customer", "paymentTerms", "lines[0]", "lines[0].lineNumber"];
    expect(paths.filter((path) => !enKeys.has(fieldLabelKey(path).key))).toEqual([]);
  });
});
