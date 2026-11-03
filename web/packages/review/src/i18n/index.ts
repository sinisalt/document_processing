import en from "./en/review.json";
import de from "./de/review.json";

/**
 * The package's `review` namespace (form chrome, the built-in invoice labels, member and unit labels, the type editor),
 * to merge into an app's `createI18n({ resources })`. Field, section and option labels of a definition come from the
 * definition itself (docs/11 §19 "i18n").
 */
export const reviewResources = {
  en: { review: en },
  de: { review: de },
} as const;

export { en as reviewEn, de as reviewDe };
