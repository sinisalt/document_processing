import i18n from "i18next";
import { EMPTY, formatDate, formatNumber, formatPercent } from "./format";

/** What `flagMessage` reads of a `DocumentFlagDto`. */
export interface FlagText {
  code: string;
  /** The API's English text: shown when the code or its variant has no translation, or a value it needs is missing. */
  message: string;
  field?: string | null;
  /** Invariant strings from the API (docs/02 §5 "Flag parameters"); `reason` picks the variant. */
  params?: Readonly<Record<string, string>> | null;
}

export interface FlagMessageOptions {
  /** Defaults to the current UI language. */
  language?: string;
  /** Turns a canonical path (`dueDate`, `lines[0].lineNet`) into its label on screen; the path itself otherwise. */
  fieldLabel?: (path: string) => string;
  /** Turns a `DocumentType` name into its label (UNSUPPORTED_TYPE); the name itself otherwise. */
  typeLabel?: (type: string) => string;
}

/** Parameter keys by how they are shown; anything else (VAT ids, names, codes, the model's words) is shown as sent. */
const dateKeys = new Set(["issueDate", "dueDate"]);
const amountKeys = new Set(["sum", "subtotal", "net", "tax", "gross", "amountDue", "total", "taxable", "expected", "printed", "actual", "unitPrice"]);
const ratioKeys = new Set(["confidence", "threshold"]);
const numberKeys = new Set(["quantity", "rate", "line", "page", "count", "maxAgeDays"]);

/** The flag's parameters as the sentence shows them: dates, amounts and ratios in the locale's format, paths as labels. */
export function flagMessageValues(params: Readonly<Record<string, string>>, options: FlagMessageOptions = {}): Record<string, string> {
  const language = options.language;
  const values: Record<string, string> = {};
  for (const [key, raw] of Object.entries(params)) {
    if (key === "reason") continue;
    if (key === "field") values[key] = options.fieldLabel ? options.fieldLabel(raw) : raw;
    else if (key === "type") values[key] = options.typeLabel ? options.typeLabel(raw) : raw;
    else if (dateKeys.has(key)) {
      const date = formatDate(raw, language);
      values[key] = date === EMPTY ? raw : date;
    }
    else if (amountKeys.has(key)) values[key] = Number.isFinite(Number(raw)) ? formatNumber(raw, language, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : raw;
    else if (ratioKeys.has(key)) values[key] = Number.isFinite(Number(raw)) ? formatPercent(raw, language) : raw;
    else if (numberKeys.has(key)) values[key] = Number.isFinite(Number(raw)) ? formatNumber(raw, language, { maximumFractionDigits: 4 }) : raw;
    else values[key] = raw;
  }
  return values;
}

/**
 * A flag in the UI language: `common:flagMessages.<CODE>.<reason>` (or `.default` for a code with one phrasing) with
 * the flag's parameters, formatted for the locale. The API's English `message` is the answer whenever that cannot be
 * done faithfully: an unknown code or variant, a flag stored before parameters existed, or a sentence that needs a
 * value the flag does not carry.
 */
export function flagMessage(flag: FlagText, options: FlagMessageOptions = {}): string {
  const params = flag.params ?? {};
  // A rule's own message from its type definition (docs/11 §11, definition flags): `message.<locale>` in the parameters,
  // in the UI language when the definition has it, else the definition's English text the API sends as `message`.
  const language = (options.language ?? (i18n.isInitialized ? i18n.language : undefined) ?? "en").split("-")[0];
  const own = params[`message.${language}`];
  if (own) return own;
  if (Object.keys(params).some((key) => key.startsWith("message."))) return flag.message;
  if (!i18n.isInitialized) return flag.message;
  const key = `flagMessages.${flag.code}.${params.reason ?? "default"}`;
  const lng = options.language;
  const count = params.count !== undefined && Number.isFinite(Number(params.count)) ? Number(params.count) : undefined;
  const lookup = { ns: "common", lng, ...(count !== undefined ? { count } : {}) };
  if (!i18n.exists(key, lookup)) return flag.message;

  let missing = false;
  const text = i18n.t(key, {
    ...lookup,
    ...flagMessageValues(params, options),
    // `count` stays the number (plural rule); the sentence shows it formatted like the other numbers.
    ...(count !== undefined ? { count } : {}),
    missingInterpolationHandler: () => {
      missing = true;
      return "";
    },
  } as Record<string, unknown>);
  return missing || typeof text !== "string" || text.length === 0 ? flag.message : text;
}

/**
 * The data behind a flag that its sentence does not show, in the UI language, or null when there is none to show. A
 * row of `deltaToPrevious` (`reason: deltaRow`, docs/11 §11) carries the row's key, the previous and the new value and
 * the change, which a definition's own message ("Unusual price change against the last approved list") leaves out:
 * `104539: from 39.02 to 54.63 (+40.01%)`. It keys on the reason, so a definition flag with its own code shows it too.
 */
export function flagDetail(flag: FlagText, options: FlagMessageOptions = {}): string | null {
  const params = flag.params ?? {};
  if (params.reason !== "deltaRow" || !i18n.isInitialized) return null;
  const previous = Number(params.previous);
  const value = Number(params.value);
  const change = Number(params.changePercent);
  if (!params.key || !Number.isFinite(previous) || !Number.isFinite(value) || !Number.isFinite(change)) return null;
  const lng = options.language;
  const amount = (n: number) => formatNumber(n, lng, { minimumFractionDigits: 2, maximumFractionDigits: 4 });
  // changePercent is the size of the move; the direction is the one from the previous value to the new one.
  const signed = value < previous ? -Math.abs(change) : Math.abs(change);
  const percent = new Intl.NumberFormat(lng ?? (i18n.language || "en"), { style: "percent", signDisplay: "exceptZero", maximumFractionDigits: 2 }).format(signed / 100);
  const text = i18n.t("flagDetails.deltaRow", { ns: "common", lng, row: params.key, previous: amount(previous), value: amount(value), change: percent, interpolation: { escapeValue: false } });
  return typeof text === "string" && text.length > 0 && text !== "flagDetails.deltaRow" ? text : null;
}
