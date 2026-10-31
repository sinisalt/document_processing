import { forwardRef } from "react";
import { useTranslation } from "react-i18next";
import { DateInput, datePlaceholder, type DateInputProps } from "@docproc/ui";

export type LocalizedDateInputProps = Omit<DateInputProps, "locale" | "calendarLabel"> & { calendarLabel?: string };

/**
 * `DateInput` in the UI language: German writes and reads `28.10.2026` with a `TT.MM.JJJJ` placeholder, English
 * `10/28/2026`, whatever the browser's own locale is. Use it instead of a native date input, type="date" (docs/05).
 */
export const LocalizedDateInput = forwardRef<HTMLInputElement, LocalizedDateInputProps>(function LocalizedDateInput({ calendarLabel, placeholder, ...props }, ref) {
  const { t, i18n } = useTranslation("common");
  const locale = i18n.language || "en";
  return (
    <DateInput
      ref={ref}
      locale={locale}
      calendarLabel={calendarLabel ?? t("dateInput.pick")}
      placeholder={placeholder ?? datePlaceholder(locale, { day: t("dateInput.day"), month: t("dateInput.month"), year: t("dateInput.year") })}
      {...props}
    />
  );
});
