import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { FormProvider, useForm } from "react-hook-form";
import { i18n } from "@docproc/app-shell";
import { DefinitionForm } from "./DefinitionForm";
import { PAGE_SIZE } from "./RepeatingGroupGrid";
import { specsFromDefinition, type FormSpec, type FormSpecField } from "../lib/formSpec";
import { diffCorrections, type FieldCorrection } from "../lib/corrections";
import { fieldDomId, toFormValues } from "../lib/values";
import { extracted, missing, money } from "../test/fixtures";
import { forkWith, templates } from "../test/templates";

interface HarnessProps {
  spec: FormSpec;
  canonical: unknown;
  schemaVersion?: number;
  disabled?: boolean;
  onSave?: (corrections: FieldCorrection[]) => void;
  onStructuralChange?: (correction: FieldCorrection) => void;
  flaggedPaths?: string[];
}

/** The review screen's form in miniature: the generated form, and a save that sends only the diff. */
function Harness({ spec, canonical, schemaVersion = 2, disabled = false, onSave = () => undefined, onStructuralChange, flaggedPaths }: HarnessProps) {
  const layout = specsFromDefinition(spec, canonical, { schemaVersion });
  const initial = toFormValues(canonical, layout.specs, i18n.language);
  const form = useForm<Record<string, unknown>>({ defaultValues: initial });
  return (
    <FormProvider {...form}>
      <form aria-label="review" onSubmit={form.handleSubmit((values) => onSave(diffCorrections(layout.specs, initial, values)))}>
        <DefinitionForm layout={layout} canonical={canonical} disabled={disabled} onHighlight={() => undefined} onStructuralChange={onStructuralChange} flaggedPaths={flaggedPaths} />
        <button type="submit">save</button>
      </form>
    </FormProvider>
  );
}

const deliveryNote = {
  deliveryNoteNumber: extracted({ verbatim: "LS-4711", normalized: "LS4711" }),
  deliveryDate: extracted("2026-10-30"),
  supplier: extracted({ name: "Nordsee Fisch GmbH", address: { city: "Hamburg" } }),
  recipient: missing(),
  orderReferences: [extracted({ verbatim: "PO-1" })],
  carrier: missing(),
  signedBy: missing(),
  temperatureReadings: [],
  notes: missing(),
  lines: [
    { lineNumber: 1, description: extracted("Kabeljau"), quantityDelivered: extracted(4), unit: extracted("KGM"), unitRaw: "kg" },
    { lineNumber: 2, description: extracted("Lachs"), quantityDelivered: missing() },
  ],
};

function input(path: string): HTMLElement {
  const element = document.getElementById(fieldDomId(path));
  if (!element) throw new Error(`no input for ${path}`);
  return element;
}

beforeEach(async () => {
  await i18n.changeLanguage("en");
});
afterEach(cleanup);

describe("generated form of a delivery note", () => {
  it("renders the definition's sections and labels, and the composite members in the UI language", () => {
    render(<Harness spec={templates.deliveryNote} canonical={deliveryNote} />);
    expect(screen.getByLabelText("Delivery note no.")).toHaveValue("LS-4711");
    expect(document.querySelector('[data-section="parties"] legend')).toHaveTextContent("Parties");
    expect(within(document.querySelector<HTMLElement>('[data-composite="supplier"]')!).getByLabelText("City")).toHaveValue("Hamburg");
    expect(screen.getByLabelText("Line 1, Quantity delivered")).toHaveValue("4");
    // The unit as printed beside the unit code it was normalised to.
    expect(screen.getByRole("combobox", { name: "Line 1, Unit" })).toHaveTextContent("Kilogram (KGM)");
    expect(screen.getByTestId("unit-raw")).toHaveTextContent("Printed: kg");
  });

  it("speaks German with the definition's German labels", async () => {
    await i18n.changeLanguage("de");
    render(<Harness spec={templates.deliveryNote} canonical={deliveryNote} />);
    expect(screen.getByLabelText("Lieferscheinnummer")).toHaveValue("LS-4711");
    expect(screen.getByLabelText("Position 1, Liefermenge")).toHaveValue("4");
  });

  it("sends the changed values as set corrections", async () => {
    const onSave = vi.fn();
    render(<Harness spec={templates.deliveryNote} canonical={deliveryNote} onSave={onSave} />);
    fireEvent.change(screen.getByLabelText("Carrier"), { target: { value: "DHL" } });
    fireEvent.change(screen.getByLabelText("Line 2, Quantity delivered"), { target: { value: "2,5" } });
    fireEvent.click(screen.getByRole("button", { name: "save" }));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual([
      { path: "carrier", value: "DHL", reason: null },
      { path: "lines[1].quantityDelivered", value: 2.5, reason: null },
    ]);
  });

  it("adds and removes rows of a repeating group", () => {
    const onStructuralChange = vi.fn();
    render(<Harness spec={templates.deliveryNote} canonical={deliveryNote} onStructuralChange={onStructuralChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Add row" }));
    expect(onStructuralChange).toHaveBeenLastCalledWith({ op: "append", path: "lines", value: null, reason: null });
    fireEvent.click(screen.getByRole("button", { name: "Remove row 2" }));
    expect(onStructuralChange).toHaveBeenLastCalledWith({ op: "remove", path: "lines[1]", value: null, reason: null });
  });

  it("adds and removes list entries", () => {
    const onStructuralChange = vi.fn();
    render(<Harness spec={templates.deliveryNote} canonical={deliveryNote} onStructuralChange={onStructuralChange} />);
    fireEvent.change(screen.getByLabelText("New entry for Order nos."), { target: { value: "PO-2" } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(onStructuralChange).toHaveBeenLastCalledWith({ op: "append", path: "orderReferences", value: "PO-2", reason: null });
    fireEvent.click(screen.getByRole("button", { name: "Remove Order nos. 1" }));
    expect(onStructuralChange).toHaveBeenLastCalledWith({ op: "remove", path: "orderReferences[0]", value: null, reason: null });
  });

  it("offers no structural edits on a version 1 tree, a settled document or without a handler", () => {
    const { unmount } = render(<Harness spec={templates.deliveryNote} canonical={deliveryNote} schemaVersion={1} onStructuralChange={vi.fn()} />);
    expect(screen.queryByRole("button", { name: "Add row" })).not.toBeInTheDocument();
    unmount();
    render(<Harness spec={templates.deliveryNote} canonical={deliveryNote} disabled onStructuralChange={vi.fn()} />);
    expect(screen.queryByRole("button", { name: "Add row" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("Delivery note no.")).toBeDisabled();
  });

  it("offers a row to an empty repeating group", () => {
    const onStructuralChange = vi.fn();
    render(<Harness spec={templates.deliveryNote} canonical={{ ...deliveryNote, lines: [] }} onStructuralChange={onStructuralChange} />);
    expect(screen.getByText("This document has no line items.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add row" }));
    expect(onStructuralChange).toHaveBeenCalledWith({ op: "append", path: "lines", value: null, reason: null });
  });

  it("shows a column a flag names and marks the flagged row", () => {
    render(<Harness spec={templates.deliveryNote} canonical={deliveryNote} flaggedPaths={["lines[1].backorderQuantity"]} />);
    expect(screen.getByLabelText("Line 2, Back-ordered quantity")).toBeInTheDocument();
    expect(document.querySelector('[data-line="1"] tr')).toHaveClass("bg-amber-500/5");
    expect(document.querySelector('[data-line="0"] tr')).not.toHaveClass("bg-amber-500/5");
  });

  it("shows a column a reviewer picks under Columns", async () => {
    render(<Harness spec={templates.deliveryNote} canonical={{ ...deliveryNote, lines: [{ lineNumber: 1, supplierSku: extracted("A-1"), description: extracted("x"), quantityDelivered: extracted(1), unit: extracted("KGM"), quantityOrdered: extracted(1), lotNumber: extracted("L1"), bestBefore: extracted("2026-12-01") }] }} />);
    expect(screen.queryByLabelText("Line 1, Back-ordered quantity")).not.toBeInTheDocument();
    fireEvent.pointerDown(screen.getByRole("button", { name: "Columns" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitemcheckbox", { name: "Back-ordered quantity" }));
    expect(await screen.findByLabelText("Line 1, Back-ordered quantity")).toBeInTheDocument();
  });
});

describe("generated form of a price list", () => {
  const row = (i: number) => ({ supplierSku: extracted({ verbatim: `A-${i}` }), description: extracted(`Article ${i}`), price: extracted(money(i)), tiers: i === 0 ? [{ minQuantity: extracted(10), price: extracted(money(0.5)) }] : [] });

  it("pages a long list instead of mounting every row", () => {
    const lines = Array.from({ length: 412 }, (_, i) => row(i));
    render(<Harness spec={templates.priceList} canonical={{ currency: extracted("EUR"), lines, ext: {} }} />);
    expect(screen.getByText(`Rows 1–${PAGE_SIZE} of 412`)).toBeInTheDocument();
    expect(document.querySelectorAll("[data-line]")).toHaveLength(PAGE_SIZE);
    expect(screen.getByText("1 entry")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Next rows" }));
    expect(screen.getByText(`Rows ${PAGE_SIZE + 1}–${2 * PAGE_SIZE} of 412`)).toBeInTheDocument();
    expect(input(`lines[${PAGE_SIZE}].supplierSku`)).toHaveValue(`A-${PAGE_SIZE}`);
  });

  it("opens a long list on the rows a check flagged, and shows all rows on request", () => {
    // The P5 demo: March's list has one article up 40 % against February; deltaToPrevious names that row.
    const lines = Array.from({ length: 412 }, (_, i) => row(i));
    render(<Harness spec={templates.priceList} canonical={{ currency: extracted("EUR"), lines, ext: {} }} flaggedPaths={["lines[300].price.amount", "lines", "lines[76].price.amount"]} />);
    expect([...document.querySelectorAll("[data-line]")].map((e) => e.getAttribute("data-line"))).toEqual(["76", "300"]);
    expect(screen.getByTestId("grid-filtered-lines")).toHaveTextContent("2 of 412 rows");
    expect(screen.queryByTestId("grid-pager")).toBeNull();
    fireEvent.click(screen.getByTestId("grid-filter-flagged-lines"));
    expect(screen.getByText(`Rows 1–${PAGE_SIZE} of 412`)).toBeInTheDocument();
    expect(screen.getByTestId("grid-filter-flagged-lines")).toHaveTextContent("Only the flagged rows (2)");
    fireEvent.click(screen.getByTestId("grid-filter-flagged-lines"));
    expect(document.querySelectorAll("[data-line]")).toHaveLength(2);
  });

  it("shows every row of a short list, flagged or not, and offers the filter", () => {
    render(<Harness spec={templates.priceList} canonical={{ currency: extracted("EUR"), lines: [row(1), row(2), row(3)], ext: {} }} flaggedPaths={["lines[1].price.amount"]} />);
    expect(document.querySelectorAll("[data-line]")).toHaveLength(3);
    fireEvent.click(screen.getByTestId("grid-filter-flagged-lines"));
    expect([...document.querySelectorAll("[data-line]")].map((e) => e.getAttribute("data-line"))).toEqual(["1"]);
  });

  it("names the grid after the definition's label of the group (Articles), not the invoice family's line items", () => {
    render(<Harness spec={templates.priceList} canonical={{ currency: extracted("EUR"), lines: [row(1), row(2)], ext: {} }} />);
    const grid = screen.getByTestId("line-item-grid");
    expect(within(grid).getByText("Articles · 2 rows")).toBeInTheDocument();
    expect(within(grid).getByRole("table", { name: "Articles" })).toBeInTheDocument();
    expect(screen.queryByText(/line items/)).toBeNull();
    expect(screen.getByLabelText("Articles 1, Price")).toBeInTheDocument();
  });

  it("edits, adds and removes supplier-specific attributes", () => {
    const onStructuralChange = vi.fn();
    render(<Harness spec={templates.priceList} canonical={{ lines: [], ext: { storeNo: extracted("0815") } }} onStructuralChange={onStructuralChange} />);
    expect(screen.getByLabelText("storeNo")).toHaveValue("0815");
    fireEvent.change(screen.getByLabelText("Attribute"), { target: { value: "promo code" } });
    expect(screen.getByRole("alert")).toHaveTextContent("A key starts with a letter");
    fireEvent.change(screen.getByLabelText("Attribute"), { target: { value: "promoCode" } });
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "X1" } });
    fireEvent.click(screen.getByRole("button", { name: "Add attribute" }));
    expect(onStructuralChange).toHaveBeenLastCalledWith({ path: "ext.promoCode", value: "X1", reason: null });
    fireEvent.click(screen.getByRole("button", { name: "Remove storeNo" }));
    expect(onStructuralChange).toHaveBeenLastCalledWith({ op: "remove", path: "ext.storeNo", value: null, reason: null });
  });

  it("marks required fields and selects codes by their definition labels", async () => {
    render(<Harness spec={templates.priceList} canonical={{ lines: [], listScope: extracted("Changes") }} />);
    expect(screen.getByLabelText("Valid from")).toHaveAttribute("aria-required", "true");
    expect(screen.getByRole("combobox", { name: "Scope" })).toHaveTextContent("Changes");
    await i18n.changeLanguage("de");
    cleanup();
    render(<Harness spec={templates.priceList} canonical={{ lines: [], listScope: extracted("Changes") }} />);
    expect(screen.getByRole("combobox", { name: "Umfang" })).toHaveTextContent("Änderungsliste");
  });
});

describe("custom fields", () => {
  const field = (extra: Partial<FormSpecField> & Pick<FormSpecField, "key" | "kind">): FormSpecField => ({ path: extra.key, section: "document", ...extra });
  const fork = forkWith(
    templates.creditNote,
    "CreditNote",
    field({ key: "supplierCustomerRef", kind: "identifier", indexed: true, label: { en: "Our customer no. at supplier", de: "Unsere Kundennummer beim Lieferanten" } }),
    field({ key: "returnApproved", kind: "boolean", label: { en: "Return approved" } }),
    field({ key: "creditReason", kind: "enum", options: [{ code: "Return", label: { en: "Return" } }, { code: "Bonus" }] }),
    field({ key: "returnNote", kind: "text", label: { en: "Return note" }, visibleWhen: { path: "creditReason", equals: "Return" } }),
  );
  const canonical = { kind: extracted("CreditNote"), invoiceNumber: extracted({ verbatim: "GS-1" }), supplierCustomerRef: missing(), returnApproved: missing(), creditReason: missing(), returnNote: missing(), lines: [] };

  it("are edited like every other field and saved by their path", async () => {
    const onSave = vi.fn();
    render(<Harness spec={fork} canonical={canonical} onSave={onSave} />);
    fireEvent.change(screen.getByLabelText("Our customer no. at supplier"), { target: { value: "0815" } });
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Return approved" }), { key: "Enter" });
    fireEvent.click(within(await screen.findByRole("listbox")).getByRole("option", { name: "Yes" }));
    fireEvent.click(screen.getByRole("button", { name: "save" }));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual([
      { path: "supplierCustomerRef", value: "0815", reason: null },
      { path: "returnApproved", value: true, reason: null },
    ]);
  });

  it("show a field with visibleWhen once its condition holds", async () => {
    render(<Harness spec={fork} canonical={canonical} />);
    expect(screen.queryByLabelText("Return note")).not.toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Credit reason" }), { key: "Enter" });
    fireEvent.click(within(await screen.findByRole("listbox")).getByRole("option", { name: "Return" }));
    expect(await screen.findByLabelText("Return note")).toBeInTheDocument();
  });
});
