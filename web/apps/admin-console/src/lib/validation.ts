import { i18n } from "@docproc/app-shell";

/**
 * A zod check's message as an i18next key of the `admin` namespace, resolved when the check fails (so the language is
 * the one on screen at that moment): `z.string().min(1, translated("tenants.validation.required"))`. Checks without
 * one get the shell's global error map (`common:validation.*`), so no zod default ever reaches the screen.
 */
export function translated(key: string): { error: () => string } {
  return { error: () => i18n.t(key, { ns: "admin" }) };
}
