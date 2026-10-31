import { useState } from "react"
import { describe, expect, it } from "vitest"
import { fireEvent, render, screen } from "@testing-library/react"
import { DateInput, datePattern, datePlaceholder, formatIsoDate, parseLocalDate } from "./date-input"

describe("date helpers", () => {
  it("writes and reads a date in the UI language, not the browser's", () => {
    expect(datePattern("de")).toEqual({ order: ["day", "month", "year"], separator: "." })
    expect(datePattern("en-US").order).toEqual(["month", "day", "year"])
    expect(formatIsoDate("2026-10-28", "de")).toBe("28.10.2026")
    expect(formatIsoDate("2026-11-11", "en-US")).toBe("11/11/2026")
    expect(parseLocalDate("28.10.2026", "de")).toBe("2026-10-28")
    expect(parseLocalDate("1.2.2026", "de")).toBe("2026-02-01")
    expect(parseLocalDate("10/28/2026", "en-US")).toBe("2026-10-28")
    expect(parseLocalDate("2026-10-28", "de")).toBe("2026-10-28")
    expect(datePlaceholder("de", { day: "TT", month: "MM", year: "JJJJ" })).toBe("TT.MM.JJJJ")
  })

  it("rejects text that is not a calendar date", () => {
    expect(parseLocalDate("31.02.2026", "de")).toBeNull()
    expect(parseLocalDate("28.10.26", "de")).toBeNull()
    expect(parseLocalDate("28.10", "de")).toBeNull()
    expect(parseLocalDate("", "de")).toBeNull()
    expect(formatIsoDate("28.10", "de")).toBe("28.10")
  })
})

function Harness({ initial, locale }: { initial: string; locale: string }) {
  const [value, setValue] = useState(initial)
  return (
    <>
      <DateInput aria-label="Due date" value={value} onValueChange={setValue} locale={locale} calendarLabel="Pick a date" />
      <output data-testid="value">{value}</output>
    </>
  )
}

describe("DateInput", () => {
  it("shows the ISO value in German and reports what is typed as ISO", () => {
    render(<Harness initial="2026-11-11" locale="de" />)
    const input = screen.getByLabelText("Due date")
    expect(input).toHaveValue("11.11.2026")

    fireEvent.change(input, { target: { value: "30.11.2026" } })
    expect(screen.getByTestId("value")).toHaveTextContent("2026-11-30")

    // Unfinished text is kept as typed and reported as is, so a form does not mistake it for "no date".
    fireEvent.change(input, { target: { value: "30.11" } })
    expect(input).toHaveValue("30.11")
    expect(screen.getByTestId("value")).toHaveTextContent("30.11")

    fireEvent.change(input, { target: { value: "" } })
    expect(screen.getByTestId("value")).toHaveTextContent("")
    expect(screen.getByRole("button", { name: "Pick a date" })).toBeInTheDocument()
  })

  it("tidies a typed date on blur", () => {
    render(<Harness initial="" locale="de" />)
    const input = screen.getByLabelText("Due date")
    fireEvent.change(input, { target: { value: "1.2.2026" } })
    fireEvent.blur(input)
    expect(input).toHaveValue("01.02.2026")
    expect(screen.getByTestId("value")).toHaveTextContent("2026-02-01")
  })
})
