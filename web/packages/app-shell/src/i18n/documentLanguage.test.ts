import { createI18n, i18n, supportedLanguageOf } from "./index";

createI18n({ lng: "en" });

describe("the document language", () => {
  afterEach(async () => {
    await i18n.changeLanguage("en");
  });

  it("follows the UI language, so <html lang> never says English under a German screen", async () => {
    expect(document.documentElement.lang).toBe("en");
    await i18n.changeLanguage("de");
    expect(document.documentElement.lang).toBe("de");
    await i18n.changeLanguage("en");
    expect(document.documentElement.lang).toBe("en");
  });

  it("maps a regional or unsupported code to a supported language", () => {
    expect(supportedLanguageOf("de-AT")).toBe("de");
    expect(supportedLanguageOf("en-GB")).toBe("en");
    expect(supportedLanguageOf("hr")).toBe("en");
    expect(supportedLanguageOf(undefined)).toBe("en");
  });
});
