import { describe, expect, it } from "vitest";
import { formatBytes, formatDate, formatDateTime, formatMoney, formatPercent, initials, parseDate, relativeTime, toNumber } from "./format";

// Intl output contains non-breaking and narrow no-break spaces; normalise them so the assertions read naturally.
const plain = (s: string) => s.replace(/[  ]/g, " ");

describe("formatMoney", () => {
  it("formats by locale and currency", () => {
    expect(plain(formatMoney(1234.5, "EUR", "de"))).toBe("1.234,50 €");
    expect(plain(formatMoney(1234.5, "EUR", "en"))).toBe("€1,234.50");
    expect(plain(formatMoney("99.9", "CHF", "de-CH"))).toBe("CHF 99.90");
  });

  it("accepts the generated client's number | string and rejects garbage", () => {
    expect(formatMoney("12.00", "EUR", "en")).toBe("€12.00");
    expect(formatMoney("abc", "EUR", "en")).toBe("—");
    expect(formatMoney(null, "EUR", "en")).toBe("—");
    expect(formatMoney(undefined, "EUR", "en")).toBe("—");
  });

  it("falls back to a plain number when the currency is missing or malformed", () => {
    expect(plain(formatMoney(5, null, "en"))).toBe("5.00");
    expect(plain(formatMoney(5, "EURO", "en"))).toBe("5.00 EURO");
    expect(plain(formatMoney(5, " eur ", "en"))).toBe("€5.00");
  });

  it("formats negatives (credit notes)", () => {
    expect(plain(formatMoney(-10, "EUR", "en"))).toBe("-€10.00");
  });
});

describe("toNumber and formatPercent", () => {
  it("parses numeric strings", () => {
    expect(toNumber("1.5")).toBe(1.5);
    expect(toNumber(2)).toBe(2);
    expect(toNumber("")).toBeNull();
    expect(toNumber("NaN")).toBeNull();
    expect(toNumber(null)).toBeNull();
  });

  it("formats ratios as percentages", () => {
    expect(plain(formatPercent(0.873, "en"))).toBe("87%");
    expect(plain(formatPercent(0.873, "de", 1))).toBe("87,3 %");
    expect(formatPercent(null, "en")).toBe("—");
  });
});

describe("dates", () => {
  it("treats date-only values as calendar dates, not UTC midnight", () => {
    const d = parseDate("2026-03-01");
    expect(d?.getFullYear()).toBe(2026);
    expect(d?.getMonth()).toBe(2);
    expect(d?.getDate()).toBe(1);
    expect(formatDate("2026-03-01", "en-GB")).toBe("1 Mar 2026");
    expect(formatDate("2026-03-01", "de")).toBe("01.03.2026");
  });

  it("formats date-times and tolerates missing or invalid values", () => {
    const iso = "2026-10-30T09:05:00Z";
    expect(formatDateTime(iso, "en-GB", { dateStyle: "medium", timeStyle: "short", timeZone: "UTC" })).toBe("30 Oct 2026, 09:05");
    expect(formatDateTime(null, "en")).toBe("—");
    expect(formatDate("not a date", "en")).toBe("—");
    expect(parseDate(new Date(Number.NaN))).toBeNull();
  });

  it("describes relative time in the right unit", () => {
    const now = new Date("2026-10-30T12:00:00Z");
    expect(relativeTime("2026-10-30T09:00:00Z", "en", now)).toBe("3 hours ago");
    expect(relativeTime("2026-10-30T11:59:30Z", "en", now)).toBe("now");
    expect(relativeTime("2026-10-30T11:58:00Z", "en", now)).toBe("2 minutes ago");
    expect(relativeTime("2026-10-29T12:00:00Z", "en", now)).toBe("yesterday");
    expect(relativeTime("2026-11-06T12:00:00Z", "en", now)).toBe("next week");
    expect(relativeTime("2025-10-30T12:00:00Z", "en", now)).toBe("last year");
    expect(relativeTime("2026-10-30T09:00:00Z", "de", now)).toBe("vor 3 Stunden");
    expect(relativeTime(null, "en", now)).toBe("—");
  });
});

describe("formatBytes and initials", () => {
  it("scales units", () => {
    expect(formatBytes(0, "en")).toBe("0 B");
    expect(formatBytes(1536, "en")).toBe("1.5 KB");
    expect(formatBytes(5 * 1024 * 1024, "en")).toBe("5 MB");
    expect(formatBytes("2048", "en")).toBe("2 KB");
    expect(formatBytes(null, "en")).toBe("—");
  });

  it("derives initials from names, usernames and e-mails", () => {
    expect(initials("Anna Admin")).toBe("AA");
    expect(initials("tenant-a-admin")).toBe("TA");
    expect(initials("superadmin")).toBe("S");
    expect(initials("olga@docproc.local")).toBe("OL");
    expect(initials("")).toBe("?");
    expect(initials(null)).toBe("?");
  });
});
