import { describe, expect, it } from "vitest";
import { nav, navFor } from "./nav";

describe("navFor", () => {
  it("drops the tenant-only screens for a platform account", () => {
    expect(navFor({ scope: "platform" }).map((item) => item.to)).toEqual(["/", "/documents", "/review"]);
  });

  it("keeps every entry in tenant scope and while the user is unknown", () => {
    expect(navFor({ scope: "tenant" })).toBe(nav);
    expect(navFor(null)).toBe(nav);
  });
});
