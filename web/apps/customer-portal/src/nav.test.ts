import { describe, expect, it } from "vitest";
import { insightsOffered, nav, navFor } from "./nav";

const allOff = { configurableSteps: false, configurableTypes: false, customTypes: false, outputs: false, sourceRouting: false, structuredSources: false, supplierLayouts: false };

describe("navFor", () => {
  it("drops the tenant-only screens for a platform account", () => {
    expect(navFor({ scope: "platform" }).map((item) => item.to)).toEqual(["/", "/documents", "/review"]);
    expect(nav.map((item) => item.to)).toContain("/insights");
  });

  it("keeps every entry in tenant scope for a tenant with a P4/P5 switch on", () => {
    expect(navFor({ scope: "tenant" }, { ...allOff, supplierLayouts: true })).toBe(nav);
    expect(navFor({ scope: "tenant" }, { ...allOff, configurableSteps: true })).toBe(nav);
  });

  it("leaves Insights out for a tenant without the P4/P5 switches, and while the switches or the user are unknown", () => {
    const without = nav.filter((item) => item.to !== "/insights");
    expect(navFor({ scope: "tenant" }, allOff)).toEqual(without);
    expect(navFor({ scope: "tenant" }, { ...allOff, configurableTypes: true, outputs: true })).toEqual(without);
    expect(navFor({ scope: "tenant" })).toEqual(without);
    expect(navFor(null)).toEqual(without);
  });
});

describe("insightsOffered", () => {
  it("is on with source routing, supplier layouts, custom types or configurable steps", () => {
    expect(insightsOffered(null)).toBe(false);
    expect(insightsOffered(allOff)).toBe(false);
    for (const key of ["sourceRouting", "supplierLayouts", "customTypes", "configurableSteps"] as const) {
      expect(insightsOffered({ ...allOff, [key]: true })).toBe(true);
    }
  });
});
