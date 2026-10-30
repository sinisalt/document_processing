import { createI18n } from "@docproc/app-shell";
import en from "./en/admin.json";
import de from "./de/admin.json";

/** The console's `admin` namespace on top of the shell's `common`; `t("key")` reads `admin` by default. */
export const i18n = createI18n({
  resources: { en: { admin: en }, de: { admin: de } },
  defaultNS: "admin",
});
