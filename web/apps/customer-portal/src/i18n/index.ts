import { createI18n } from "@docproc/app-shell";
import { reviewResources } from "@docproc/review";
import en from "./en/portal.json";
import de from "./de/portal.json";

/**
 * The portal's `portal` namespace on top of the shell's `common`, plus the review package's `review` (the generated
 * form and the type editor); `t("key")` reads `portal` by default.
 */
export const i18n = createI18n({
  resources: { en: { portal: en, ...reviewResources.en }, de: { portal: de, ...reviewResources.de } },
  defaultNS: "portal",
});
