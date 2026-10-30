import { describe, expect, it } from "vitest";
import { isApplePlatform } from "./platform";

describe("isApplePlatform", () => {
  it("prefers the client hint and falls back to navigator.platform", () => {
    expect(isApplePlatform({ platform: "Win32", userAgentData: { platform: "macOS" } })).toBe(true);
    expect(isApplePlatform({ platform: "MacIntel" })).toBe(true);
    expect(isApplePlatform({ platform: "iPhone" })).toBe(true);
    expect(isApplePlatform({ platform: "Win32", userAgentData: { platform: "Windows" } })).toBe(false);
    expect(isApplePlatform({ platform: "Linux x86_64" })).toBe(false);
    expect(isApplePlatform({ platform: "" })).toBe(false);
    expect(isApplePlatform(undefined)).toBe(false);
  });
});
