import { describe, expect, it } from "vitest";
import { describeFailure, parseFailureReason } from "./failure";

const REASON = "CorruptPdf: The file is not a readable PDF: Could not find an xref trailer or stream dictionary in the input file.";

describe("parseFailureReason", () => {
  it("splits the DocumentProcessingFailure code off the reason FailurePolicy wrote", () => {
    expect(parseFailureReason(REASON)).toEqual({ code: "CorruptPdf", message: "The file is not a readable PDF: Could not find an xref trailer or stream dictionary in the input file." });
    expect(parseFailureReason("TooLarge: 40 MB exceeds the 25 MB limit").code).toBe("TooLarge");
  });

  it("leaves any other reason whole", () => {
    expect(parseFailureReason("LLM provider RateLimited: slow down")).toEqual({ code: null, message: "LLM provider RateLimited: slow down" });
    expect(parseFailureReason("InvalidOperationException: boom")).toEqual({ code: null, message: "InvalidOperationException: boom" });
  });
});

describe("describeFailure", () => {
  it("puts the translated label where the raw enum code was", () => {
    const label = (code: string) => ({ CorruptPdf: "Unreadable PDF" })[code] ?? code;
    expect(describeFailure(REASON, label)).toBe("Unreadable PDF: The file is not a readable PDF: Could not find an xref trailer or stream dictionary in the input file.");
    expect(describeFailure("CorruptPdf:", label)).toBe("Unreadable PDF");
    expect(describeFailure("Stage failed for an unknown reason", label)).toBe("Stage failed for an unknown reason");
  });
});
