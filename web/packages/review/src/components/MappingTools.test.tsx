import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { FormProvider, useForm } from "react-hook-form";
import { i18n } from "@docproc/app-shell";
import { DefinitionForm, MOVE_MIME } from "./DefinitionForm";
import { MapPreview } from "./MapPreview";
import { TablePageStrip } from "./TablePageStrip";
import { TableToolPanel, type TableToolPanelProps } from "./TableToolPanel";
import { specsFromDefinition } from "../lib/formSpec";
import { diffCorrections, type FieldCorrection } from "../lib/corrections";
import { rowFieldsOf, type TableReport, type TableToolState } from "../lib/tableTool";
import { toFormValues } from "../lib/values";
import { extracted, missing } from "../test/fixtures";
import { templates } from "../test/templates";

beforeEach(async () => {
  await i18n.changeLanguage("en");
});
afterEach(cleanup);

const report: TableReport = {
  rows: 412,
  rejectedRows: 2,
  pages: [1, 2, 3],
  pagesWithoutRows: [3],
  stoppedOnPage: null,
  perPage: [
    { page: 1, headerFound: true, rows: 22, rejected: 0, rejectedLines: [], skippedLines: 0, groupHeadings: 1, continuationLines: 1, columns: [], columnsFrom: "mapping", firstRows: [] },
    {
      page: 2,
      headerFound: true,
      rows: 390,
      rejected: 2,
      rejectedLines: [{ page: 2, text: "104 Wein 1,2x", bBox: { x0: 50, y0: 400, x1: 700, y1: 410 }, reason: "unreadable", message: "'1,2x' is not an amount.", field: "price" }],
      skippedLines: 0,
      groupHeadings: 0,
      continuationLines: 0,
      columns: [],
      columnsFrom: "header",
      firstRows: [],
    },
    { page: 3, headerFound: false, rows: 0, rejected: 0, rejectedLines: [], skippedLines: 0, groupHeadings: 0, continuationLines: 0, columns: [], columnsFrom: "previousHeader", firstRows: [] },
  ],
};

describe("TablePageStrip", () => {
  it("shows the totals, one chip per page and the rejected lines of the current page", () => {
    const onPickPage = vi.fn();
    const onPickRejected = vi.fn();
    render(<TablePageStrip report={report} currentPage={2} onPickPage={onPickPage} onPickRejected={onPickRejected} />);
    expect(screen.getByTestId("table-totals")).toHaveTextContent("412 rows, 2 rejected, 3 pages read");
    expect(screen.getByTestId("strip-page-1")).toHaveTextContent("p. 1: 22 rows");
    expect(screen.getByTestId("strip-page-2")).toHaveTextContent("p. 2: 390 rows, 2 rejected");
    expect(screen.getByTestId("strip-page-2")).toHaveAttribute("aria-current", "page");
    expect(screen.getByTestId("strip-page-3")).toHaveTextContent("p. 3: no rows");
    const rejected = screen.getByTestId("table-rejected");
    expect(rejected).toHaveTextContent("Page 2: 2 rejected lines");
    expect(rejected).toHaveTextContent("104 Wein 1,2x");
    expect(rejected).toHaveTextContent("and 1 more");
    fireEvent.click(screen.getByTestId("strip-page-3"));
    expect(onPickPage).toHaveBeenCalledWith(3);
    fireEvent.click(within(rejected).getByText("104 Wein 1,2x"));
    expect(onPickRejected).toHaveBeenCalledWith(expect.objectContaining({ page: 2, reason: "unreadable" }));
  });
});

const state: TableToolState = {
  page: 1,
  box: { x0: 45, y0: 280, x1: 945, y1: 935 },
  columns: [
    { x0: 50, x1: 150, field: "supplierSku", header: "Art.-Nr." },
    { x0: 150, x1: 500, field: "description", header: "Bezeichnung" },
    { x0: 500, x1: 650, field: null, header: "ab 10", tierMinQuantity: 10 },
  ],
  headerRow: { y0: 285, y1: 297 },
  rowAnchor: "supplierSku",
  path: "lines",
  allPages: true,
  groupHeadingTarget: "category",
};

function panel(patch: Partial<TableToolPanelProps> = {}) {
  const fields = rowFieldsOf(templates.priceList, "lines");
  const props: TableToolPanelProps = {
    state,
    rowFields: fields.fields,
    hasTiers: fields.hasTiers,
    hasCategory: fields.hasCategory,
    report: null,
    reportKind: null,
    currentPage: 1,
    canWrite: true,
    onColumnTarget: vi.fn(),
    onRemoveDivider: vi.fn(),
    onAllPagesChange: vi.fn(),
    onGroupHeadingsChange: vi.fn(),
    onPreview: vi.fn(),
    onWrite: vi.fn(),
    onReset: vi.fn(),
    onPickPage: vi.fn(),
    ...patch,
  };
  render(<TableToolPanel {...props} />);
  return props;
}

describe("TableToolPanel", () => {
  it("asks for the table to be drawn before there is a mapping", () => {
    panel({ state: null });
    expect(screen.getByTestId("table-tool")).toHaveTextContent("Drag a box around the table");
  });

  it("lists the columns with their fields, and Write needs a preview of the current mapping", () => {
    const props = panel({ report, reportKind: "proposal" });
    expect(screen.getByTestId("table-column-0")).toHaveAttribute("data-field", "supplierSku");
    expect(screen.getByTestId("table-column-2")).toHaveTextContent("Tier price from 10");
    expect(screen.getByLabelText(/Apply to all pages/)).toBeChecked();
    expect(screen.getByLabelText(/Group headings fill the category/)).toBeChecked();
    expect(screen.getByTestId("table-preview")).toHaveTextContent("Read all pages");
    expect(screen.getByTestId("table-write")).toBeDisabled();
    fireEvent.click(screen.getByTestId("table-preview"));
    expect(props.onPreview).toHaveBeenCalled();
    fireEvent.click(screen.getByLabelText(/Apply to all pages/));
    expect(props.onAllPagesChange).toHaveBeenCalledWith(false);
    fireEvent.click(screen.getByRole("button", { name: "Merge column 2 into the one before it" }));
    expect(props.onRemoveDivider).toHaveBeenCalledWith(1);
    expect(screen.getByText("Proposal (page 1 only):")).toBeInTheDocument();
  });

  it("writes after a preview, and says when the anchor column is not mapped", () => {
    const props = panel({ report, reportKind: "preview" });
    expect(screen.getByTestId("table-write")).toHaveTextContent("Write 412 rows");
    fireEvent.click(screen.getByTestId("table-write"));
    expect(props.onWrite).toHaveBeenCalled();
    cleanup();
    panel({ state: { ...state, columns: state.columns.slice(1) }, report, reportKind: "preview" });
    expect(screen.getByTestId("table-problem")).toHaveTextContent("row anchor");
    expect(screen.getByTestId("table-preview")).toBeDisabled();
    expect(screen.getByTestId("table-write")).toBeDisabled();
  });

  it("a stale preview cannot be written", () => {
    panel({ report, reportKind: "stale" });
    expect(screen.getByTestId("table-write")).toBeDisabled();
    expect(screen.getByText(/the mapping changed since/)).toBeInTheDocument();
  });
});

describe("MapPreview", () => {
  const base = { fieldLabel: "Valid from", text: "01.01.2027", value: "2027-01-01", onValueChange: vi.fn(), fits: true, onAssign: vi.fn(), onSkip: vi.fn(), onCancel: vi.fn() };

  it("asks for a field, then for a selection", () => {
    const { rerender } = render(<MapPreview {...base} fieldLabel={null} text={null} />);
    expect(screen.getByTestId("map-preview")).toHaveAttribute("data-state", "no-field");
    rerender(<MapPreview {...base} text={null} />);
    expect(screen.getByTestId("map-preview")).toHaveTextContent("Valid from: click a word");
  });

  it("shows the text read and the value; Enter assigns, Tab skips, Esc cancels", () => {
    const props = { ...base, onAssign: vi.fn(), onSkip: vi.fn(), onCancel: vi.fn(), onValueChange: vi.fn() };
    render(<MapPreview {...props} />);
    expect(screen.getByTestId("map-text")).toHaveTextContent("01.01.2027");
    const value = screen.getByTestId("map-value");
    expect(value).toHaveValue("2027-01-01");
    expect(value).toHaveFocus();
    fireEvent.change(value, { target: { value: "2027-01-02" } });
    expect(props.onValueChange).toHaveBeenCalledWith("2027-01-02");
    fireEvent.keyDown(value, { key: "Enter" });
    expect(props.onAssign).toHaveBeenCalled();
    fireEvent.keyDown(value, { key: "Tab" });
    expect(props.onSkip).toHaveBeenCalled();
    fireEvent.keyDown(value, { key: "Escape" });
    expect(props.onCancel).toHaveBeenCalled();
  });

  it("offers region OCR where there is no text, and says why it cannot run", () => {
    const onOcr = vi.fn();
    render(<MapPreview {...base} text="" noWords onOcr={onOcr} error="No OCR is configured on this server" />);
    expect(screen.getByTestId("map-preview")).toHaveAttribute("data-state", "no-words");
    expect(screen.getByTestId("map-assign")).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Read with OCR" }));
    expect(onOcr).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("No OCR is configured");
  });
});

describe("match fields (move)", () => {
  const priceList = {
    priceListNumber: extracted({ verbatim: "01.01.2027", normalized: "01.01.2027" }),
    title: missing(),
    issueDate: missing(),
    validFrom: missing(),
    validUntil: missing(),
    listScope: missing(),
    currency: missing(),
    priceBasis: missing(),
    declaredLineCount: missing(),
    supplier: missing(),
    customer: missing(),
    supplierCustomerRef: missing(),
    lines: [],
    ext: {},
  };

  function Harness({ onMove }: { onMove: (c: FieldCorrection) => void }) {
    const layout = specsFromDefinition(templates.priceList, priceList, { schemaVersion: 2 });
    const form = useForm<Record<string, unknown>>({ defaultValues: toFormValues(priceList, layout.specs) });
    return (
      <FormProvider {...form}>
        <DefinitionForm layout={layout} canonical={priceList} disabled={false} onHighlight={() => undefined} onMove={onMove} />
      </FormProvider>
    );
  }

  it("moves a value from its menu, or by dragging its chip onto another field, as one move correction", async () => {
    const onMove = vi.fn();
    render(<Harness onMove={onMove} />);
    // Only a field with a value has a chip.
    expect(screen.queryByTestId("move-validFrom")).toBeNull();
    const chip = screen.getByTestId("move-priceListNumber");
    fireEvent.pointerDown(chip, { button: 0, pointerType: "mouse" });
    fireEvent.click(await screen.findByTestId("move-target-validFrom"));
    expect(onMove).toHaveBeenCalledWith({ op: "move", path: "validFrom", from: "priceListNumber", value: null, reason: null });

    onMove.mockClear();
    const target = document.querySelector('[data-drop-target]:has(#field-currency)') as HTMLElement;
    const data = new Map<string, string>([[MOVE_MIME, "priceListNumber"]]);
    const dataTransfer = { types: [MOVE_MIME], getData: (type: string) => data.get(type) ?? "", setData: vi.fn(), dropEffect: "none", effectAllowed: "all" };
    fireEvent.dragOver(target, { dataTransfer });
    fireEvent.drop(target, { dataTransfer });
    expect(onMove).toHaveBeenCalledWith(expect.objectContaining({ op: "move", path: "currency", from: "priceListNumber" }));
  });

  it("saves a value taken from the page with its source, unless it was retyped", () => {
    const layout = specsFromDefinition(templates.priceList, priceList, { schemaVersion: 2 });
    const initial = toFormValues(priceList, layout.specs);
    const source = { method: "click", page: 1, bBox: { x0: 1, y0: 2, x1: 3, y1: 4 }, words: [[1, 17]], rawText: "01.01.2027" };
    const mapped = { validFrom: { text: "2027-01-01", source } };
    const values = { ...initial, validFrom: "2027-01-01", currency: "EUR" };
    const corrections = diffCorrections(layout.specs, initial, values, null, mapped);
    expect(corrections.find((c) => c.path === "validFrom")).toEqual({ path: "validFrom", value: "2027-01-01", reason: null, source });
    expect(corrections.find((c) => c.path === "currency")?.source).toBeUndefined();
    expect(diffCorrections(layout.specs, initial, { ...values, validFrom: "2027-02-01" }, null, mapped).find((c) => c.path === "validFrom")?.source).toBeUndefined();
  });
});
