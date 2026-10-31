import { describe, expect, it } from "vitest";
import { countryName, percentTextToRatio, ratioToPercentText } from "./GeneralTab";

describe("auto-approve threshold in percent", () => {
  it("shows the stored ratio as the percent the hint and the review screen speak of", () => {
    expect(ratioToPercentText(0.8)).toBe("80");
    expect(ratioToPercentText(0.875)).toBe("87.5");
    expect(ratioToPercentText(Number.NaN)).toBe("");
  });

  it("stores what is typed as the API's 0..1 ratio", () => {
    expect(percentTextToRatio("80")).toBe(0.8);
    expect(percentTextToRatio("100")).toBe(1);
    expect(percentTextToRatio(" ")).toBeNaN();
  });
});

describe("countryName", () => {
  it("names the country in the UI language and keeps the code", () => {
    expect(countryName("DE", "en")).toBe("Germany (DE)");
    expect(countryName("de", "de")).toBe("Deutschland (DE)");
    expect(countryName(null, "en")).toBe("—");
  });
});
