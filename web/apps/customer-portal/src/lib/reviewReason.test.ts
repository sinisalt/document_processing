import { afterEach, describe, expect, it } from "vitest";
import { i18n } from "@/i18n";
import { reviewReasonCodes, reviewReasonLabels } from "./reviewReason";

describe("review task reasons", () => {
  afterEach(async () => {
    await i18n.changeLanguage("en");
  });

  it("splits the pipeline's comma list of flag codes", () => {
    expect(reviewReasonCodes("LOW_CONFIDENCE, TOTALS_MISMATCH")).toEqual(["LOW_CONFIDENCE", "TOTALS_MISMATCH"]);
    expect(reviewReasonCodes("ALWAYS_REVIEW")).toEqual(["ALWAYS_REVIEW"]);
    expect(reviewReasonCodes("")).toEqual([]);
    expect(reviewReasonCodes(null)).toEqual([]);
  });

  it("shows names in the UI language, never the raw codes the queue used to print", async () => {
    await i18n.changeLanguage("en");
    expect(reviewReasonLabels("LOW_CONFIDENCE, TOTALS_MISMATCH", i18n.t)).toEqual(["Low confidence", "Totals do not add up"]);
    expect(reviewReasonLabels("ALWAYS_REVIEW", i18n.t)).toEqual(["Supplier is always reviewed"]);
    expect(reviewReasonLabels("MANUAL_REVIEW", i18n.t)).toEqual(["Review required"]);
    await i18n.changeLanguage("de");
    expect(reviewReasonLabels("UNSUPPORTED_TYPE", i18n.t)).toEqual([i18n.t("flagCodes.UNSUPPORTED_TYPE")]);
    expect(reviewReasonLabels("UNSUPPORTED_TYPE", i18n.t)[0]).not.toBe("UNSUPPORTED_TYPE");
    expect(reviewReasonLabels("ALWAYS_REVIEW", i18n.t)).toEqual(["Lieferant wird immer geprüft"]);
  });

  it("keeps a code it has no name for", () => {
    expect(reviewReasonLabels("SOMETHING_NEW", i18n.t)).toEqual(["SOMETHING_NEW"]);
  });
});
