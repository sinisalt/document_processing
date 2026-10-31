import { describe, expect, it } from "vitest";
import { createInstance } from "i18next";
import en from "../i18n/en/admin.json";
import de from "../i18n/de/admin.json";
import { reviewReasonLabels } from "./review";

async function translator(lng: "en" | "de") {
  const instance = createInstance();
  await instance.init({ lng, defaultNS: "admin", ns: ["admin"], resources: { en: { admin: en }, de: { admin: de } } });
  return instance.t;
}

describe("reviewReasonLabels", () => {
  it("translates every code of a comma-separated reason, and the task reasons that are not flag codes", async () => {
    const t = await translator("en");
    expect(reviewReasonLabels("LOW_CONFIDENCE, TOTALS_MISMATCH", t).map((r) => r.code)).toEqual(["LOW_CONFIDENCE", "TOTALS_MISMATCH"]);
    expect(reviewReasonLabels("LOW_CONFIDENCE, TOTALS_MISMATCH", t).map((r) => r.label)).toEqual([en.flagCodes.LOW_CONFIDENCE, en.flagCodes.TOTALS_MISMATCH]);
    expect(reviewReasonLabels("ALWAYS_REVIEW", t)[0]!.label).toBe("Supplier is always reviewed");
    expect((await translator("de"))("review.reasons.MANUAL_REVIEW")).toBe("Prüfung erforderlich");
  });

  it("keeps an unknown code as it is and drops empty parts", async () => {
    const t = await translator("en");
    expect(reviewReasonLabels("SOMETHING_NEW, ", t)).toEqual([{ code: "SOMETHING_NEW", label: "SOMETHING_NEW" }]);
    expect(reviewReasonLabels(null, t)).toEqual([]);
  });
});
