import { beforeAll, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { i18n } from "@/i18n";
import { FieldHighlightOverlay } from "./FieldHighlightOverlay";

// jsdom lays nothing out: the SVG sits at (0, 0), so on a 1000 × 1000 rendering client pixels are page units.
const rendered = { width: 1000, height: 1000 };
const words = [
  { text: "gültig", box: { x0: 40, y0: 200, x1: 80, y1: 212 } },
  { text: "01.01.2027", box: { x0: 150, y0: 200, x1: 210, y1: 212 } },
];

describe("FieldHighlightOverlay in map mode and with the table tool (docs/11 §12.3, §12.4)", () => {
  beforeAll(async () => {
    await i18n.changeLanguage("en");
  });

  it("has no selection layer outside map mode", () => {
    render(<FieldHighlightOverlay rendered={rendered} boxes={[]} words={words} highlightedPath={null} />);
    expect(screen.queryByTestId("selection-layer")).toBeNull();
  });

  it("picks the word under a click, shift-click adds, and a drag draws a box", () => {
    const onPick = vi.fn();
    render(<FieldHighlightOverlay rendered={rendered} boxes={[]} words={words} highlightedPath={null} interaction="map" onPick={onPick} />);
    const layer = screen.getByTestId("selection-layer");
    fireEvent.pointerDown(layer, { button: 0, clientX: 180, clientY: 205 });
    fireEvent.pointerUp(layer, { clientX: 181, clientY: 206 });
    expect(onPick).toHaveBeenLastCalledWith({ kind: "word", index: 1, box: words[1]!.box, additive: false });

    fireEvent.pointerDown(layer, { button: 0, clientX: 60, clientY: 205 });
    fireEvent.pointerUp(layer, { clientX: 60, clientY: 205, shiftKey: true });
    expect(onPick).toHaveBeenLastCalledWith(expect.objectContaining({ kind: "word", index: 0, additive: true }));

    // A click on no word picks nothing.
    onPick.mockClear();
    fireEvent.pointerDown(layer, { button: 0, clientX: 600, clientY: 600 });
    fireEvent.pointerUp(layer, { clientX: 600, clientY: 600 });
    expect(onPick).not.toHaveBeenCalled();

    fireEvent.pointerDown(layer, { button: 0, clientX: 300, clientY: 100 });
    fireEvent.pointerMove(layer, { clientX: 500, clientY: 150 });
    expect(screen.getByTestId("drag-rect")).toBeInTheDocument();
    fireEvent.pointerUp(layer, { clientX: 500, clientY: 150 });
    expect(onPick).toHaveBeenLastCalledWith({ kind: "region", box: { x0: 300, y0: 100, x1: 500, y1: 150 } });
    expect(screen.queryByTestId("drag-rect")).toBeNull();
  });

  it("draws the selection, the table, its header band, rejected lines and movable dividers", () => {
    const onDividerMove = vi.fn();
    const onDividerAdd = vi.fn();
    render(
      <FieldHighlightOverlay
        rendered={rendered}
        boxes={[]}
        highlightedPath={null}
        interaction="table"
        selection={[{ x0: 1, y0: 1, x1: 2, y1: 2 }]}
        table={{ box: { x0: 50, y0: 280, x1: 950, y1: 930 }, dividers: [50, 150, 500, 950], headerRow: { y0: 285, y1: 297 }, rejected: [{ x0: 50, y0: 400, x1: 700, y1: 410 }], onDividerMove, onDividerAdd }}
      />,
    );
    expect(screen.getAllByTestId("map-selection")).toHaveLength(1);
    expect(screen.getByTestId("table-header-band")).toBeInTheDocument();
    expect(screen.getAllByTestId("table-rejected-box")).toHaveLength(1);
    expect(screen.getAllByTestId("table-divider").map((d) => d.getAttribute("data-x"))).toEqual(["50", "150", "500", "950"]);

    const handle = screen.getByTestId("divider-handle-2");
    fireEvent.pointerDown(handle, { button: 0, clientX: 500, clientY: 500 });
    fireEvent.pointerMove(handle, { clientX: 470, clientY: 500 });
    fireEvent.pointerUp(handle, { clientX: 470, clientY: 500 });
    expect(onDividerMove).toHaveBeenLastCalledWith(2, 470);
    fireEvent.keyDown(screen.getByRole("slider", { name: "Column divider 2" }), { key: "ArrowRight", shiftKey: true });
    expect(onDividerMove).toHaveBeenLastCalledWith(1, 160);

    fireEvent.doubleClick(screen.getByTestId("selection-layer"), { clientX: 300, clientY: 500 });
    expect(onDividerAdd).toHaveBeenCalledWith(300);
    fireEvent.doubleClick(screen.getByTestId("selection-layer"), { clientX: 300, clientY: 990 });
    expect(onDividerAdd).toHaveBeenCalledTimes(1);
  });

  it("draws a read-only table on the other pages: no handles", () => {
    render(<FieldHighlightOverlay rendered={rendered} boxes={[]} highlightedPath={null} interaction="table" table={{ box: { x0: 50, y0: 0, x1: 950, y1: 930 }, dividers: [50, 950] }} />);
    expect(screen.getAllByTestId("table-divider")).toHaveLength(2);
    expect(screen.queryByRole("slider")).toBeNull();
  });
});
