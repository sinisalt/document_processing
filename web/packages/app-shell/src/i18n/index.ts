import i18n, { type Resource, type ResourceLanguage } from "i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import { initReactI18next } from "react-i18next";
import enCommon from "./en/common.json";
import deCommon from "./de/common.json";

export const supportedLanguages = ["en", "de"] as const;
export type SupportedLanguage = (typeof supportedLanguages)[number];

export const LANGUAGE_STORAGE_KEY = "docproc.lang";

/** The shell's own strings; apps merge their namespaces on top. */
export const commonResources: Record<SupportedLanguage, { common: typeof enCommon }> = {
  en: { common: enCommon },
  de: { common: deCommon },
};

export interface CreateI18nOptions {
  /** `{ en: { portal: {...} }, de: { portal: {...} } }`: the app's namespaces, merged with `common`. */
  resources?: Resource;
  /** The namespace `t("key")` reads without a prefix; the shell always addresses its own as `common:`. */
  defaultNS?: string;
  /** Forces a language (tests); otherwise the browser detector decides. */
  lng?: string;
  debug?: boolean;
}

function mergeResources(extra: Resource | undefined): Resource {
  const merged: Resource = {};
  for (const language of supportedLanguages) {
    merged[language] = { ...commonResources[language] } as ResourceLanguage;
  }
  if (extra) {
    for (const [language, namespaces] of Object.entries(extra)) {
      merged[language] = { ...(merged[language] ?? {}), ...namespaces } as ResourceLanguage;
    }
  }
  return merged;
}

/**
 * Initialises the shared i18next instance once per app (call it from the app's `src/i18n/index.ts` before rendering):
 * `common` from this package plus the app's namespaces, en and de, language from `localStorage` then the browser.
 * A second call only adds resources, so tests and HMR do not re-initialise.
 */
export function createI18n(options: CreateI18nOptions = {}): typeof i18n {
  const resources = mergeResources(options.resources);
  if (i18n.isInitialized) {
    for (const [language, namespaces] of Object.entries(resources)) {
      for (const [ns, bundle] of Object.entries(namespaces)) {
        i18n.addResourceBundle(language, ns, bundle, true, true);
      }
    }
    return i18n;
  }

  const namespaces = new Set<string>(["common"]);
  for (const bundles of Object.values(resources)) for (const ns of Object.keys(bundles)) namespaces.add(ns);

  void i18n
    .use(LanguageDetector)
    .use(initReactI18next)
    .init({
      resources,
      lng: options.lng,
      fallbackLng: "en",
      supportedLngs: [...supportedLanguages],
      nonExplicitSupportedLngs: true,
      load: "languageOnly",
      ns: [...namespaces],
      defaultNS: options.defaultNS ?? "common",
      fallbackNS: "common",
      interpolation: { escapeValue: false },
      returnNull: false,
      debug: options.debug ?? false,
      detection: {
        order: ["localStorage", "navigator"],
        lookupLocalStorage: LANGUAGE_STORAGE_KEY,
        caches: ["localStorage"],
      },
    });
  return i18n;
}

export function isSupportedLanguage(value: string): value is SupportedLanguage {
  return (supportedLanguages as readonly string[]).includes(value);
}

/** The two-letter language i18next resolved (`de-AT` → `de`). */
export function currentLanguage(): SupportedLanguage {
  const resolved = i18n.resolvedLanguage ?? i18n.language ?? "en";
  const short = resolved.split("-")[0] ?? "en";
  return isSupportedLanguage(short) ? short : "en";
}

export { i18n };
