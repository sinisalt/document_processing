import { describe, expect, it } from "vitest";
import { settingsSchema } from "./SettingsTab";

const valid = { autoApproveConfidenceThreshold: "0.8", retentionYears: "10", shareExtractorProfiles: false, alwaysReviewSupplierIds: [] };

describe("tenant settings form", () => {
  it("reads the number inputs' text", () => {
    expect(settingsSchema.parse(valid)).toMatchObject({ autoApproveConfidenceThreshold: 0.8, retentionYears: 10 });
    expect(settingsSchema.parse({ ...valid, autoApproveConfidenceThreshold: "0" }).autoApproveConfidenceThreshold).toBe(0);
  });

  it("refuses an emptied threshold instead of saving 0 (auto-approve everything)", () => {
    for (const blank of ["", "  "]) {
      const result = settingsSchema.safeParse({ ...valid, autoApproveConfidenceThreshold: blank });
      expect(result.success).toBe(false);
      expect(result.error?.issues[0]?.path).toEqual(["autoApproveConfidenceThreshold"]);
    }
  });

  it("refuses an emptied retention", () => {
    const result = settingsSchema.safeParse({ ...valid, retentionYears: "" });
    expect(result.success).toBe(false);
    expect(result.error?.issues[0]?.path).toEqual(["retentionYears"]);
  });
});
