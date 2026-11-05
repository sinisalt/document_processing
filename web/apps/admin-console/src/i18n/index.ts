import { createI18n } from "@docproc/app-shell";
import { reviewResources } from "@docproc/review";
import en from "./en/admin.json";
import de from "./de/admin.json";

/**
 * The console's `admin` namespace on top of the shell's `common`, plus the review package's `review` (the document type
 * editor); `t("key")` reads `admin` by default.
 */
export const i18n = createI18n({
  resources: { en: { admin: en, ...reviewResources.en }, de: { admin: de, ...reviewResources.de } },
  defaultNS: "admin",
});
