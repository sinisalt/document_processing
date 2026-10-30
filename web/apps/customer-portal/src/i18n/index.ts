import { createI18n } from "@docproc/app-shell";
import en from "./en/portal.json";
import de from "./de/portal.json";

/** The portal's `portal` namespace on top of the shell's `common`; `t("key")` reads `portal` by default. */
export const i18n = createI18n({
  resources: { en: { portal: en }, de: { portal: de } },
  defaultNS: "portal",
});
