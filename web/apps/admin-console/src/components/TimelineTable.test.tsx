import { fireEvent, render, screen, within } from "@testing-library/react";
import { beforeAll } from "vitest";
import { documentEvent } from "@/test/fixtures";
import { TimelineTable } from "./TimelineTable";

const events = [
  documentEvent({ id: 1, eventType: "document.received", toStatus: "Received", at: "2026-10-01T08:00:00Z" }),
  documentEvent({ id: 2, eventType: "document.stage_completed", fromStatus: "Received", toStatus: "Normalized", at: "2026-10-01T08:01:00Z" }),
  documentEvent({ id: 3, eventType: "document.stage_failed", fromStatus: "Normalized", toStatus: "Failed", at: "2026-10-01T08:02:00Z", details: { stage: "Split", error: "PDF is encrypted", tried: ["qpdf"] } }),
];

function eventRows(): HTMLElement[] {
  return screen.getAllByRole("row").filter((row) => row.hasAttribute("data-event-type"));
}

describe("TimelineTable", () => {
  beforeAll(() => {
    Element.prototype.hasPointerCapture ??= () => false;
    Element.prototype.scrollIntoView ??= () => {};
  });

  it("renders one row per event on the shell's grid, with the failed one highlighted", () => {
    render(<TimelineTable events={events} />);
    expect(eventRows().map((row) => row.getAttribute("data-event-type"))).toEqual(["document.received", "document.stage_completed", "document.stage_failed"]);
    expect(eventRows()[2]).toHaveClass("bg-destructive/5");
    expect(within(eventRows()[2]!).getByText("PDF is encrypted")).toBeInTheDocument();
    expect(within(eventRows()[2]!).getByText("Split")).toBeInTheDocument();
  });

  it("expands only rows with details into the key/value list and the raw JSON", () => {
    render(<TimelineTable events={events} />);
    expect(within(eventRows()[0]!).queryByRole("button", { name: "Show details" })).not.toBeInTheDocument();

    fireEvent.click(within(eventRows()[2]!).getByRole("button", { name: "Show details" }));
    const details = document.querySelector<HTMLElement>('dl[aria-label="Details"]')!;
    expect(within(details).getByText("tried[0]")).toBeInTheDocument();
    expect(within(details).getByText("qpdf")).toBeInTheDocument();
    expect(screen.getByText(/"error": "PDF is encrypted"/)).toBeInTheDocument();
  });

  it("sorts by time like the other grids", () => {
    render(<TimelineTable events={events} />);
    const when = screen.getAllByRole("columnheader").find((header) => header.textContent?.includes("When"))!;
    const order = () => eventRows().map((row) => row.getAttribute("data-event-type"));
    fireEvent.click(within(when).getByRole("button", { name: "Sort ascending" }));
    expect(order()).toEqual(["document.received", "document.stage_completed", "document.stage_failed"]);
    fireEvent.click(within(when).getByRole("button", { name: "Sort descending" }));
    expect(eventRows().map((row) => row.getAttribute("data-event-type"))).toEqual(["document.stage_failed", "document.stage_completed", "document.received"]);
  });

  it("offers the column menu to hide columns", () => {
    render(<TimelineTable events={events} />);
    expect(screen.getByRole("button", { name: "Choose columns" })).toBeInTheDocument();
  });

  it("shows the empty state without events", () => {
    render(<TimelineTable events={[]} />);
    expect(screen.getByText("No events")).toBeInTheDocument();
  });
});
