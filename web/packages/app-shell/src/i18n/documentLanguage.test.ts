import { afterEach, describe, expect, it } from "vitest";
import i18next from "i18next";
import { syncDocumentLanguage } from "./index";

describe("syncDocumentLanguage", () => {
  afterEach(() => {
    document.documentElement.lang = "en";
  });

  it("puts the UI language on <html lang> at start-up and on every change", async () => {
    document.documentElement.lang = "en";
    const instance = i18next.createInstance();
    syncDocumentLanguage(instance);
    await instance.init({ lng: "de-AT", supportedLngs: ["en", "de"], nonExplicitSupportedLngs: true, load: "languageOnly", fallbackLng: "en", resources: { en: { x: {} }, de: { x: {} } } });
    expect(document.documentElement.lang).toBe("de");

    await instance.changeLanguage("en");
    expect(document.documentElement.lang).toBe("en");

    await instance.changeLanguage("de");
    expect(document.documentElement.lang).toBe("de");
  });

  it("falls back to English for a language the apps do not ship", async () => {
    document.documentElement.lang = "de";
    const instance = i18next.createInstance();
    syncDocumentLanguage(instance);
    await instance.init({ lng: "fr", fallbackLng: "en", resources: { en: { x: {} } } });
    expect(document.documentElement.lang).toBe("en");
  });
});
