import { beforeAll, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { i18n } from "@/i18n";
import { FieldHighlightOverlay, boxClassName } from "./FieldHighlightOverlay";
import type { ProvenanceBox } from "@/lib/overlay";

const boxes: ProvenanceBox[] = [
  { path: "invoiceNumber", page: 1, box: { x0: 600, y0: 100, x1: 800, y1: 130 }, historical: false },
  { path: "totalGross", page: 1, box: { x0: 700, y0: 900, x1: 900, y1: 930 }, historical: false },
  { path: "totalGross", page: 1, box: { x0: 100, y0: 900, x1: 200, y1: 930 }, historical: true },
];

/** The overlay pads every box by 2 px on each side so the stroke does not cover the text. */
const PAD = 2;

function rectOf(element: Element) {
  return ["x", "y", "width", "height"].map((name) => Number(element.getAttribute(name)));
}

describe("FieldHighlightOverlay", () => {
  beforeAll(async () => {
    await i18n.changeLanguage("en");
  });

  it("maps boxes from the normalized 1000 × 1000 space onto the rendered image size", () => {
    render(<FieldHighlightOverlay rendered={{ width: 500, height: 707 }} boxes={boxes} highlightedPath={null} />);
    const svg = screen.getByTestId("field-overlay");
    expect(svg.getAttribute("viewBox")).toBe("0 0 500 707");
    const [invoice] = screen.getAllByRole("button", { name: "Field invoiceNumber" });
    // x: 600 / 1000 × 500 = 300, y: 100 / 1000 × 707 = 70.7, width: 200 → 100, height: 30 → 21.21.
    const [x, y, width, height] = rectOf(invoice!);
    expect(x).toBeCloseTo(300 - PAD, 6);
    expect(y).toBeCloseTo(70.7 - PAD, 6);
    expect(width).toBeCloseTo(100 + 2 * PAD, 6);
    expect(height).toBeCloseTo(21.21 + 2 * PAD, 6);
  });

  it("follows a resize: the same box at twice the width lands twice as far right", () => {
    const { rerender } = render(<FieldHighlightOverlay rendered={{ width: 400, height: 400 }} boxes={boxes.slice(0, 1)} highlightedPath={null} />);
    const x1 = Number(screen.getByRole("button", { name: "Field invoiceNumber" }).getAttribute("x"));
    rerender(<FieldHighlightOverlay rendered={{ width: 800, height: 400 }} boxes={boxes.slice(0, 1)} highlightedPath={null} />);
    const x2 = Number(screen.getByRole("button", { name: "Field invoiceNumber" }).getAttribute("x"));
    expect(x1 + PAD).toBeCloseTo(240, 6);
    expect(x2 + PAD).toBeCloseTo(480, 6);
  });

  it("emphasises the highlighted field, marks history boxes and reports hover and click", () => {
    const onHover = vi.fn();
    const onSelect = vi.fn();
    render(<FieldHighlightOverlay rendered={{ width: 1000, height: 1000 }} boxes={boxes} highlightedPath="totalGross" onHover={onHover} onSelect={onSelect} />);
    const totals = screen.getAllByRole("button", { name: "Field totalGross" });
    expect(totals).toHaveLength(2);
    expect(totals.map((rect) => rect.getAttribute("data-active"))).toEqual(["true", "true"]);
    expect(totals.map((rect) => rect.getAttribute("data-historical"))).toEqual(["false", "true"]);
    expect(screen.getByRole("button", { name: "Field invoiceNumber" }).getAttribute("data-active")).toBe("false");

    const invoice = screen.getByRole("button", { name: "Field invoiceNumber" });
    fireEvent.mouseEnter(invoice);
    expect(onHover).toHaveBeenLastCalledWith("invoiceNumber");
    fireEvent.mouseLeave(invoice);
    expect(onHover).toHaveBeenLastCalledWith(null);
    fireEvent.click(invoice);
    expect(onSelect).toHaveBeenCalledWith("invoiceNumber");
    fireEvent.keyDown(totals[0]!, { key: "Enter" });
    expect(onSelect).toHaveBeenLastCalledWith("totalGross");
  });

  it("names and titles a box by the field's label when one is given", () => {
    render(<FieldHighlightOverlay rendered={{ width: 1000, height: 1000 }} boxes={boxes.slice(0, 1)} highlightedPath={null} labelOf={(path) => (path === "invoiceNumber" ? "Invoice number" : path)} />);
    const box = screen.getByRole("button", { name: "Field Invoice number" });
    expect(box.querySelector("title")).toHaveTextContent("Invoice number");
    expect(box).toHaveAttribute("data-path", "invoiceNumber");
  });

  it("colours boxes independently of the theme, because the page image is white in dark mode too", () => {
    for (const [active, historical] of [
      [false, false],
      [true, false],
      [false, true],
      [true, true],
    ] as const) {
      const classes = boxClassName(active, historical);
      // Theme tokens such as primary (near-white in dark mode) or muted-foreground would vanish on the white page.
      expect(classes).not.toMatch(/(fill|stroke)-(primary|foreground|muted|accent|ring|background)/);
    }
    expect(boxClassName(true, false)).toContain("stroke-blue-600");
    render(<FieldHighlightOverlay rendered={{ width: 1000, height: 1000 }} boxes={boxes.slice(0, 1)} highlightedPath="invoiceNumber" />);
    expect(screen.getByRole("button", { name: "Field invoiceNumber" }).getAttribute("class")).toContain("stroke-blue-600");
  });

  it("renders nothing before the image has a size", () => {
    const { container } = render(<FieldHighlightOverlay rendered={{ width: 0, height: 0 }} boxes={boxes} highlightedPath={null} />);
    expect(container.querySelector("svg")).toBeNull();
  });
});
