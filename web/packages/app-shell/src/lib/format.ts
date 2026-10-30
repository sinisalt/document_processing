import i18n from "i18next";

/** The generated client types every `double`/`int32` as `number | string`; these helpers accept both. */
export type Numeric = number | string | null | undefined;

export const EMPTY = "—";

function currentLocale(locale?: string): string {
  if (locale) return locale;
  const language = i18n.isInitialized ? i18n.language : undefined;
  return language && language.length > 0 ? language : "en";
}

export function toNumber(value: Numeric): number | null {
  if (value === null || value === undefined || value === "") return null;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : null;
}

/**
 * `formatMoney(1234.5, "EUR", "de")` → `1.234,50 €`. A missing amount is a dash; a missing or malformed currency
 * (the API upper-cases and checks three letters, but a document may carry none) falls back to a plain number with
 * the code appended.
 */
export function formatMoney(amount: Numeric, currency?: string | null, locale?: string): string {
  const n = toNumber(amount);
  if (n === null) return EMPTY;
  const loc = currentLocale(locale);
  const code = currency?.trim().toUpperCase();
  if (code && /^[A-Z]{3}$/.test(code)) {
    try {
      return new Intl.NumberFormat(loc, { style: "currency", currency: code }).format(n);
    } catch {
      // Unknown ISO code for this runtime: fall through to the plain form.
    }
  }
  const plain = new Intl.NumberFormat(loc, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(n);
  return code ? `${plain} ${code}` : plain;
}

export function formatNumber(value: Numeric, locale?: string, options?: Intl.NumberFormatOptions): string {
  const n = toNumber(value);
  if (n === null) return EMPTY;
  return new Intl.NumberFormat(currentLocale(locale), options).format(n);
}

/** `0.873` → `87 %` (the API's confidences are 0..1). */
export function formatPercent(ratio: Numeric, locale?: string, fractionDigits = 0): string {
  const n = toNumber(ratio);
  if (n === null) return EMPTY;
  return new Intl.NumberFormat(currentLocale(locale), { style: "percent", maximumFractionDigits: fractionDigits }).format(n);
}

const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * Parses what the API sends: an RFC 3339 date-time, or a date-only `YYYY-MM-DD` (issue dates), which is taken as a
 * calendar date rather than midnight UTC so it never shifts a day in western time zones.
 */
export function parseDate(value: string | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === "") return null;
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value;
  const dateOnly = DATE_ONLY.exec(value);
  if (dateOnly) {
    const [, y, m, d] = dateOnly;
    return new Date(Number(y), Number(m) - 1, Number(d));
  }
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

export function formatDate(value: string | Date | null | undefined, locale?: string, options?: Intl.DateTimeFormatOptions): string {
  const date = parseDate(value);
  if (!date) return EMPTY;
  return new Intl.DateTimeFormat(currentLocale(locale), options ?? { dateStyle: "medium" }).format(date);
}

export function formatDateTime(value: string | Date | null | undefined, locale?: string, options?: Intl.DateTimeFormatOptions): string {
  const date = parseDate(value);
  if (!date) return EMPTY;
  return new Intl.DateTimeFormat(currentLocale(locale), options ?? { dateStyle: "medium", timeStyle: "short" }).format(date);
}

const RELATIVE_UNITS: Array<[Intl.RelativeTimeFormatUnit, number]> = [
  ["year", 365 * 24 * 60 * 60 * 1000],
  ["month", 30 * 24 * 60 * 60 * 1000],
  ["week", 7 * 24 * 60 * 60 * 1000],
  ["day", 24 * 60 * 60 * 1000],
  ["hour", 60 * 60 * 1000],
  ["minute", 60 * 1000],
];

/** `relativeTime("2026-10-30T09:00:00Z", "en", new Date("2026-10-30T12:00:00Z"))` → `3 hours ago`; under a minute → `now`. */
export function relativeTime(value: string | Date | null | undefined, locale?: string, now: Date = new Date()): string {
  const date = parseDate(value);
  if (!date) return EMPTY;
  const diff = date.getTime() - now.getTime();
  const formatter = new Intl.RelativeTimeFormat(currentLocale(locale), { numeric: "auto" });
  for (const [unit, ms] of RELATIVE_UNITS) {
    if (Math.abs(diff) >= ms) {
      return formatter.format(Math.trunc(diff / ms), unit);
    }
  }
  return formatter.format(0, "second");
}

/** `formatBytes(1536)` → `1.5 KB`. */
export function formatBytes(bytes: Numeric, locale?: string): string {
  const n = toNumber(bytes);
  if (n === null) return EMPTY;
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = n;
  let i = 0;
  while (Math.abs(value) >= 1024 && i < units.length - 1) {
    value /= 1024;
    i++;
  }
  return `${new Intl.NumberFormat(currentLocale(locale), { maximumFractionDigits: i === 0 ? 0 : 1 }).format(value)} ${units[i]}`;
}

/** Initials for an avatar: "Anna Admin" → "AA", "tenant-a-admin" → "TA", "x" → "X". */
export function initials(name: string | null | undefined, fallback = "?"): string {
  if (!name) return fallback;
  const parts = name
    .split(/[\s\-_.@]+/)
    .map((part) => part.trim())
    .filter(Boolean);
  if (parts.length === 0) return fallback;
  const first = parts[0]?.[0] ?? "";
  const second = parts.length > 1 ? parts[parts.length - 1]?.[0] ?? "" : "";
  return (first + second).toUpperCase() || fallback;
}
