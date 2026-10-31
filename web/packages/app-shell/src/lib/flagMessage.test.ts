import { beforeAll, describe, expect, it } from "vitest";
import { createI18n } from "../i18n";
import en from "../i18n/en/common.json";
import de from "../i18n/de/common.json";
import { flagMessage, type FlagText } from "./flagMessage";

const labels: Record<string, string> = { dueDate: "Due date", invoiceNumber: "Invoice number", totalGross: "Gross total" };
const deLabels: Record<string, string> = { dueDate: "Fälligkeitsdatum", invoiceNumber: "Rechnungsnummer", totalGross: "Bruttobetrag" };

const lowConfidence: FlagText = {
  code: "LOW_CONFIDENCE",
  message: "'dueDate' has confidence 0.50, below the threshold 0.80.",
  field: "dueDate",
  params: { field: "dueDate", confidence: "0.50", threshold: "0.80" },
};

/** Normalises the narrow no-break spaces Intl puts before `%` and between digit groups. */
function plain(text: string): string {
  return text.replace(/[  ]/g, " ");
}

beforeAll(() => {
  createI18n({ lng: "en" });
});

describe("flagMessage", () => {
  it("phrases a flag from its code and parameters, in the UI language", () => {
    expect(plain(flagMessage(lowConfidence, { language: "en", fieldLabel: (path) => labels[path] ?? path }))).toBe(
      "Due date was read with 50% confidence, below the threshold of 80%.",
    );
    expect(plain(flagMessage(lowConfidence, { language: "de", fieldLabel: (path) => deLabels[path] ?? path }))).toBe(
      "Fälligkeitsdatum wurde mit 50 % Sicherheit gelesen, unter dem Schwellenwert von 80 %.",
    );
  });

  it("picks the variant by `reason` and formats dates and amounts for the locale", () => {
    const dates: FlagText = { code: "DATE_IMPLAUSIBLE", message: "Due date 2026-10-01 is before the issue date 2026-10-15.", params: { reason: "dueBeforeIssue", dueDate: "2026-10-01", issueDate: "2026-10-15" } };
    expect(flagMessage(dates, { language: "de" })).toBe("Das Fälligkeitsdatum 01.10.2026 liegt vor dem Rechnungsdatum 15.10.2026.");

    const totals: FlagText = { code: "TOTALS_MISMATCH", message: "Lines sum to 1234.50 but the subtotal is 140.00.", params: { reason: "lines", sum: "1234.50", subtotal: "140.00" } };
    expect(plain(flagMessage(totals, { language: "de" }))).toBe("Die Positionen ergeben 1.234,50, die Zwischensumme ist aber 140,00.");
    expect(flagMessage(totals, { language: "en" })).toBe("The lines sum to 1,234.50, but the subtotal is 140.00.");
  });

  it("uses the plural rule for counts", () => {
    const split = (count: string): FlagText => ({ code: "MULTI_DOCUMENT_SPLIT", message: `The attachment was split into ${count} documents.`, params: { count } });
    expect(flagMessage(split("3"), { language: "de" })).toBe("Der Anhang wurde in 3 Dokumente aufgeteilt.");
    expect(flagMessage(split("1"), { language: "en" })).toBe("The attachment was split into 1 document.");
  });

  it("falls back to the API's message for an unknown code or variant", () => {
    expect(flagMessage({ code: "SOMETHING_NEW", message: "A new check failed.", params: {} }, { language: "de" })).toBe("A new check failed.");
    expect(flagMessage({ code: "TOTALS_MISMATCH", message: "Something else is off.", params: { reason: "brandNew" } }, { language: "de" })).toBe("Something else is off.");
  });

  it("falls back to the message for a flag stored before parameters existed", () => {
    // LOW_CONFIDENCE's sentence needs field, confidence and threshold: without them the English text is the honest one.
    expect(flagMessage({ ...lowConfidence, params: {} }, { language: "de" })).toBe(lowConfidence.message);
    expect(flagMessage({ ...lowConfidence, params: null }, { language: "de" })).toBe(lowConfidence.message);
    // A code whose sentence needs nothing is translated even then.
    expect(flagMessage({ code: "TYPE_UNKNOWN", message: "The document type could not be determined.", params: {} }, { language: "de" })).toBe("Die Dokumentart konnte nicht bestimmt werden.");
  });

  it("shows the model's own words and identifiers as sent", () => {
    const region: FlagText = { code: "UNREADABLE_PAGES", message: "Page 2, stamp over totals: smudged", params: { reason: "region", page: "2", area: "stamp over totals", detail: "smudged" } };
    expect(flagMessage(region, { language: "de" })).toBe("Seite 2, stamp over totals: smudged");
    const duplicate: FlagText = { code: "DUPLICATE_SUSPECTED", message: "x", params: { number: "1234567" } };
    expect(flagMessage(duplicate, { language: "de" })).toBe("Ein anderes Dokument dieses Lieferanten trägt die Nummer 1234567.");
  });
});

type Tree = { [key: string]: string | Tree };

function leaves(tree: Tree, prefix = ""): [string, string][] {
  return Object.entries(tree).flatMap(([key, value]) => (typeof value === "string" ? [[`${prefix}${key}`, value] as [string, string]] : leaves(value, `${prefix}${key}.`)));
}

function placeholders(text: string): string[] {
  return [...text.matchAll(/\{\{(\w+)\}\}/g)].map((match) => match[1]!).sort();
}

describe("the flag message catalogue", () => {
  const enMessages = new Map(leaves(en.flagMessages as Tree));
  const deMessages = new Map(leaves(de.flagMessages as Tree));

  it("has the same variants and placeholders in en and de", () => {
    expect([...deMessages.keys()].sort()).toEqual([...enMessages.keys()].sort());
    for (const [key, text] of enMessages) expect([key, placeholders(deMessages.get(key)!)]).toEqual([key, placeholders(text)]);
  });

  it("covers every variant the API raises (docs/02 §5 \"Flag parameters\")", () => {
    const variants = [
      "UNREADABLE_PAGES.region",
      "UNREADABLE_PAGES.noTextLayer",
      "TYPE_UNKNOWN.default",
      "SUPPLIER_UNMATCHED.default",
      "CUSTOMER_MISMATCH.vatId",
      "CUSTOMER_MISMATCH.noName",
      "CUSTOMER_MISMATCH.name",
      "MISSING_REQUIRED.notFound",
      "MISSING_REQUIRED.unreadable",
      "MISSING_REQUIRED.supplierName",
      "MISSING_REQUIRED.taxBreakdown",
      "TOTALS_MISMATCH.lines",
      "TOTALS_MISMATCH.gross",
      "TOTALS_MISMATCH.amountDue",
      "TAX_MISMATCH.row",
      "TAX_MISMATCH.total",
      "TAX_MISMATCH.taxable",
      "LINE_ARITHMETIC.default",
      "LOW_CONFIDENCE.default",
      "DUPLICATE_SUSPECTED.default",
      "LLM_PROVIDER_UNAVAILABLE.default",
      "MULTI_DOCUMENT_SPLIT.default_one",
      "MULTI_DOCUMENT_SPLIT.default_other",
      "CURRENCY_INFERRED.default",
      "DATE_IMPLAUSIBLE.future",
      "DATE_IMPLAUSIBLE.tooOld",
      "DATE_IMPLAUSIBLE.dueBeforeIssue",
      "DATE_IMPLAUSIBLE.servicePeriodInverted",
      "INVALID_VALUE.currencyCode",
      "INVALID_VALUE.currencyMismatch",
      "UNSUPPORTED_TYPE.default",
    ];
    expect(variants.filter((key) => !enMessages.has(key))).toEqual([]);
  });
});
