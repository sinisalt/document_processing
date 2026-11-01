import * as React from "react"
import { CalendarIcon } from "lucide-react"
import type { Locale } from "react-day-picker"
import { de } from "react-day-picker/locale/de"
import { enGB } from "react-day-picker/locale/en-GB"
import { enUS } from "react-day-picker/locale/en-US"
import { hr } from "react-day-picker/locale/hr"

import { cn } from "@/lib/utils"
import { Button } from "@/components/button"
import { Calendar } from "@/components/calendar"
import { Input } from "@/components/input"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/popover"

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/

type DatePart = "day" | "month" | "year"

/** How a locale writes a numeric date: the order of day, month and year and the separator between them. */
export interface DatePattern {
  order: DatePart[]
  separator: string
}

/** `datePattern("de")` → day.month.year, `datePattern("en-US")` → month/day/year, from `Intl.DateTimeFormat`. */
export function datePattern(locale: string): DatePattern {
  const parts = new Intl.DateTimeFormat(locale, { day: "2-digit", month: "2-digit", year: "numeric" }).formatToParts(new Date(2026, 10, 22))
  const order = parts.filter((p): p is Intl.DateTimeFormatPart & { type: DatePart } => p.type === "day" || p.type === "month" || p.type === "year").map((p) => p.type)
  const separator = parts.find((p) => p.type === "literal")?.value.trim() || "."
  return { order: order.length === 3 ? order : ["year", "month", "day"], separator }
}

function isoOf(year: number, month: number, day: number): string | null {
  const date = new Date(year, month - 1, day)
  if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return null
  return `${String(year).padStart(4, "0")}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`
}

/** An ISO `YYYY-MM-DD` as the locale writes it with two-digit day and month: `28.10.2026` in German. Other text is returned as is. */
export function formatIsoDate(iso: string, locale: string): string {
  const match = ISO_DATE.exec(iso)
  if (!match) return iso
  const [, y, m, d] = match
  if (!isoOf(Number(y), Number(m), Number(d))) return iso
  return new Intl.DateTimeFormat(locale, { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(Number(y), Number(m) - 1, Number(d)))
}

/**
 * What a person typed as an ISO date, read in the locale's order (`28.10.2026` in German, `10/28/2026` in US English),
 * with any of `.`, `/`, `-` or a space between the parts; an ISO date is always accepted. Null when it is not a date.
 */
export function parseLocalDate(text: string, locale: string): string | null {
  const value = text.trim()
  if (!value) return null
  const iso = ISO_DATE.exec(value)
  if (iso) return isoOf(Number(iso[1]), Number(iso[2]), Number(iso[3]))
  const pieces = value.split(/[./\-\s]+/).filter(Boolean)
  if (pieces.length !== 3 || pieces.some((piece) => !/^\d+$/.test(piece))) return null
  const { order } = datePattern(locale)
  const byPart = Object.fromEntries(order.map((part, i) => [part, Number(pieces[i])])) as Record<DatePart, number>
  const yearText = pieces[order.indexOf("year")] ?? ""
  if (yearText.length !== 4) return null
  return isoOf(byPart.year, byPart.month, byPart.day)
}

/** `dd.mm.yyyy` / `mm/dd/yyyy`, with the letters a caller passes for its UI language (`TT.MM.JJJJ` in German). */
export function datePlaceholder(locale: string, letters: Record<DatePart, string> = { day: "dd", month: "mm", year: "yyyy" }): string {
  const { order, separator } = datePattern(locale)
  return order.map((part) => letters[part]).join(separator)
}

function dayPickerLocale(locale: string): Locale {
  const language = locale.toLowerCase()
  if (language.startsWith("de")) return de
  if (language.startsWith("hr")) return hr
  if (language === "en-us" || language === "en") return enUS
  return language.startsWith("en") ? enGB : enUS
}

export interface DateInputProps extends Omit<React.ComponentProps<"input">, "value" | "defaultValue" | "onChange" | "type" | "min" | "max"> {
  /** ISO `YYYY-MM-DD`, empty for none; any other text is shown as is (a value the user has not finished). */
  value: string
  /**
   * Called with the ISO date as soon as the text is one, with "" when it is cleared, and with the text itself while it
   * is not (yet) a date, so a form can tell an unfinished value from an empty one.
   */
  onValueChange: (value: string) => void
  /** The UI language (BCP 47): how the date is written and read, and the calendar's language. */
  locale: string
  /** Accessible name of the calendar button. */
  calendarLabel: string
  /** ISO bounds for the calendar. */
  min?: string
  max?: string
  /** Classes for the text input itself (`className` sizes the wrapper). */
  inputClassName?: string
}

/**
 * A date field that writes and reads dates in the UI language, with a calendar to pick from. The native
 * `<input type="date">` formats by the browser's locale, not the app's, so a German UI showed US dates beside German
 * ones (docs/05 "Shared UI decisions": dates follow the UI language).
 */
const DateInput = React.forwardRef<HTMLInputElement, DateInputProps>(function DateInput(
  { value, onValueChange, locale, calendarLabel, min, max, className, inputClassName, disabled, placeholder, onBlur, ...props },
  ref,
) {
  const [text, setText] = React.useState(() => formatIsoDate(value, locale))
  const [open, setOpen] = React.useState(false)
  const reported = React.useRef(value)
  const shownLocale = React.useRef(locale)

  // A value set from outside (reset, a calendar pick, a URL) or a language switch rewrites the text; the value this
  // field reported itself does not, so typing is never reformatted under the caret.
  React.useEffect(() => {
    if (value !== reported.current || locale !== shownLocale.current) {
      reported.current = value
      shownLocale.current = locale
      setText(formatIsoDate(value, locale))
    }
  }, [value, locale])

  const report = (next: string) => {
    reported.current = next
    onValueChange(next)
  }

  const selected = ISO_DATE.test(value) ? new Date(Number(value.slice(0, 4)), Number(value.slice(5, 7)) - 1, Number(value.slice(8, 10))) : undefined
  const bound = (iso: string | undefined) => (iso && ISO_DATE.test(iso) ? new Date(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10))) : undefined)
  const from = bound(min)
  const to = bound(max)

  return (
    <div className={cn("relative", className)} data-slot="date-input">
      <Input
        ref={ref}
        type="text"
        inputMode="numeric"
        autoComplete="off"
        value={text}
        disabled={disabled}
        placeholder={placeholder ?? datePlaceholder(locale)}
        className={cn("pr-9 tabular-nums", inputClassName)}
        onChange={(event) => {
          const next = event.target.value
          setText(next)
          report(next.trim() === "" ? "" : parseLocalDate(next, locale) ?? next)
        }}
        onBlur={(event) => {
          const iso = parseLocalDate(text, locale)
          if (iso) setText(formatIsoDate(iso, locale))
          onBlur?.(event)
        }}
        {...props}
      />
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            disabled={disabled}
            aria-label={calendarLabel}
            className="absolute top-1/2 right-0.5 size-8 -translate-y-1/2 text-muted-foreground hover:text-foreground"
          >
            <CalendarIcon aria-hidden />
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0" align="end">
          <Calendar
            mode="single"
            locale={dayPickerLocale(locale)}
            selected={selected}
            defaultMonth={selected ?? to ?? from}
            disabled={[...(from ? [{ before: from }] : []), ...(to ? [{ after: to }] : [])]}
            onSelect={(date) => {
              if (!date) return
              const iso = isoOf(date.getFullYear(), date.getMonth() + 1, date.getDate())
              if (!iso) return
              setText(formatIsoDate(iso, locale))
              report(iso)
              setOpen(false)
            }}
          />
        </PopoverContent>
      </Popover>
    </div>
  )
})

export { DateInput }
