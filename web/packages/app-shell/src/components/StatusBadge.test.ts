import { describe, expect, it } from "vitest";
import { documentStatusVariant, reviewTaskStatusVariant } from "./StatusBadge";
import { tenantLabel } from "../layout/SignInPage";

describe("status colours", () => {
  it("keeps red for failures: a document waiting for review and an open task read amber", () => {
    expect(documentStatusVariant.Failed).toBe("destructive");
    expect(documentStatusVariant.NeedsReview).toBe("warning");
    expect(reviewTaskStatusVariant.Open).toBe("warning");
    expect(Object.entries(documentStatusVariant).filter(([, variant]) => variant === "destructive").map(([status]) => status)).toEqual(["Failed"]);
  });

  it("shows approvals as a calm green, not the loud primary pill", () => {
    expect(documentStatusVariant.AutoApproved).toBe("success");
    expect(documentStatusVariant.Approved).toBe("success");
    expect(Object.values(documentStatusVariant)).not.toContain("default");
  });
});

describe("tenantLabel", () => {
  const t = (key: string, options?: Record<string, unknown>) => `${key}:${String(options?.id)}`;

  it("names a dev user's tenant instead of printing a fragment of its id", () => {
    expect(tenantLabel({ tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", tenantName: "Gasthaus zur Post GmbH" }, t)).toBe("Gasthaus zur Post GmbH");
    expect(tenantLabel({ tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" }, t)).toBe("auth.tenant:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
  });
});
