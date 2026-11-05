import { afterEach, beforeAll, describe, expect, it } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { i18n } from "@/i18n";
import type { DocumentFlag } from "@/lib/queries";
import { FlagsList } from "./FlagsList";

/**
 * The flags strip of the review screen. A row of `deltaToPrevious` (the P5 demo: March's list, article 104539 up 40 %
 * against February) shows the data behind the flag, which the definition's own message leaves out (docs/09 step 10).
 */
const deltaRow = (overrides: Partial<DocumentFlag["params"]> = {}): DocumentFlag =>
  ({
    id: "f-delta-row",
    code: "RULE_FAILED",
    severity: "Warning",
    field: "lines[76].price.amount",
    message: "Unusual price change against the last approved list",
    params: {
      reason: "deltaRow",
      ruleId: "r_delta",
      field: "lines[76].price.amount",
      key: "104539",
      previous: "39.02",
      value: "54.63",
      changePercent: "40.01",
      previousDocumentId: "11111111-0000-4000-8000-000000000001",
      "message.en": "Unusual price change against the last approved list",
      "message.de": "Ungewöhnliche Preisänderung gegenüber der letzten freigegebenen Liste",
      ...overrides,
    },
    createdBy: "Pipeline",
    createdAt: "2026-11-05T06:41:00Z",
    resolvedAt: null,
    resolvedBy: null,
  }) as DocumentFlag;

/** Normalises the narrow no-break spaces Intl puts before `%` and between digit groups. */
const plain = (text: string | null) => (text ?? "").replace(/[  ]/g, " ");

function renderFlags(flags: DocumentFlag[]) {
  return render(<FlagsList flags={flags} selectedIndex={0} onSelect={() => undefined} onJump={() => undefined} fieldLabel={(path) => (path === "lines[76].price.amount" ? "Line 77 · Amount" : path)} validation="passed" />);
}

beforeAll(async () => {
  await i18n.changeLanguage("en");
});

afterEach(async () => {
  cleanup();
  await i18n.changeLanguage("en");
});

describe("FlagsList", () => {
  it("shows a price row's previous and new value and the change beside the definition's message", () => {
    renderFlags([deltaRow()]);
    const row = screen.getByTestId("flags-list");
    expect(within(row).getByText("Unusual price change against the last approved list")).toBeInTheDocument();
    expect(within(row).getByText("Line 77 · Amount")).toBeInTheDocument();
    expect(plain(screen.getByTestId("flag-detail").textContent)).toBe("104539: from 39.02 to 54.63 (+40.01%)");
  });

  it("says it in German with German numbers, and signs a fall", async () => {
    await i18n.changeLanguage("de");
    renderFlags([deltaRow({ previous: "54.63", value: "39.02", changePercent: "28.57" })]);
    expect(screen.getByText("Ungewöhnliche Preisänderung gegenüber der letzten freigegebenen Liste")).toBeInTheDocument();
    expect(plain(screen.getByTestId("flag-detail").textContent)).toBe("104539: von 54,63 auf 39,02 (-28,57 %)");
  });

  it("shows no detail for a flag without a price row's data", () => {
    renderFlags([deltaRow({ reason: "deltaToPrevious", key: undefined } as never)]);
    expect(screen.queryByTestId("flag-detail")).toBeNull();
  });
});
