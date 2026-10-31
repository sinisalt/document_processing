import { describe, expect, it } from "vitest";
import { byStatusScope, statValue } from "./dashboard";

describe("byStatusScope", () => {
  it("tells only the role with the tenant switcher to pick a tenant in the header", () => {
    expect(byStatusScope({ selectedTenant: null, isPlatform: true, canSwitchTenant: true })).toBe("platformSwitchable");
    // An Operator sees every tenant, but the header has no switcher for them.
    expect(byStatusScope({ selectedTenant: null, isPlatform: true, canSwitchTenant: false })).toBe("platform");
  });

  it("names the picked tenant, and pins tenant roles to their own", () => {
    expect(byStatusScope({ selectedTenant: "Gasthaus zur Post GmbH", isPlatform: true, canSwitchTenant: true })).toBe("tenant");
    expect(byStatusScope({ selectedTenant: null, isPlatform: false, canSwitchTenant: false })).toBe("ownTenant");
  });
});

describe("statValue", () => {
  it("shows a dash for a role the query does not run for, the rendered value once it answered", () => {
    expect(statValue({ data: undefined, isError: false }, false, () => "x", "skeleton")).toBe("—");
    expect(statValue({ data: 3, isError: false }, true, (n) => `n=${n}`, "skeleton")).toBe("n=3");
  });
});
