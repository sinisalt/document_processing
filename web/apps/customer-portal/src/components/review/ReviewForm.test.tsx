import { beforeAll, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { FormProvider, useForm } from "react-hook-form";
import { i18n } from "@/i18n";
import { CanonicalForm } from "./CanonicalForm";
import { ArithmeticIndicator, LineItemGrid } from "./LineItemGrid";
import { allSpecs, fieldDomId, toFormValues, type CanonicalInvoice } from "@/lib/canonical";
import { diffCorrections, type FieldCorrection } from "@/lib/corrections";

const money = (amount: number) => ({ amount, currency: "EUR" });
const extracted = <T,>(value: T, confidence = 1) => ({ value, status: "Extracted" as const, confidence, source: { method: "StructuredXml" as const } });

const canonical: CanonicalInvoice = {
  kind: extracted("Invoice"),
  invoiceNumber: extracted({ verbatim: "INV-1001" }),
  issueDate: extracted("2026-10-01"),
  currency: extracted("EUR"),
  supplier: extracted({ name: "Nordsee Fisch GmbH", vatId: "DE123456789", address: { city: "Hamburg", countryCode: "DE" } }),
  customer: extracted({ name: "Restaurant Seeblick" }),
  subtotalNet: extracted(money(255)),
  totalTax: extracted(money(48.45)),
  totalGross: extracted(money(303.45)),
  lines: [
    { lineNumber: 1, description: extracted("Lachsfilet"), quantity: extracted(10), unitPrice: extracted(money(18)), lineNet: extracted(money(180)) },
    // 5 × 14 = 70, but the line says 75: a mismatch the grid must show.
    { lineNumber: 2, description: extracted("Kabeljau"), quantity: extracted(5), unitPrice: extracted(money(14)), lineNet: extracted(money(75)) },
    { lineNumber: 3, description: extracted("Versand") },
  ],
};

const specs = allSpecs(canonical);
const initial = toFormValues(canonical, specs);

/** The review screen's form in miniature: the header form, the line grid and a save that sends only the diff. */
function Harness({ onSave, disabled = false }: { onSave: (corrections: FieldCorrection[]) => void; disabled?: boolean }) {
  const form = useForm<Record<string, unknown>>({ defaultValues: initial });
  return (
    <FormProvider {...form}>
      <form aria-label="review" onSubmit={form.handleSubmit((values) => onSave(diffCorrections(specs, initial, values)))}>
        <CanonicalForm canonical={canonical} disabled={disabled} onHighlight={() => undefined} />
        <LineItemGrid canonical={canonical} disabled={disabled} onHighlight={() => undefined} />
        <button type="submit">save</button>
      </form>
    </FormProvider>
  );
}

function input(path: string): HTMLInputElement {
  const element = document.getElementById(fieldDomId(path));
  if (!(element instanceof HTMLInputElement)) throw new Error(`no input for ${path}`);
  return element;
}

function lineRow(index: number): HTMLElement {
  const row = document.querySelector<HTMLElement>(`tr[data-line="${index}"]`);
  if (!row) throw new Error(`no row ${index}`);
  return row;
}

describe("review form", () => {
  beforeAll(async () => {
    await i18n.changeLanguage("en");
  });

  it("renders the canonical values with labels the reviewer can find by name", () => {
    render(<Harness onSave={vi.fn()} />);
    expect(screen.getByLabelText("Invoice number")).toHaveValue("INV-1001");
    expect(screen.getByLabelText("Total (gross)")).toHaveValue("303.45");
    expect(input("supplier.address.city")).toHaveValue("Hamburg");
    expect(screen.getByLabelText("Line 1, Qty")).toHaveValue("10");
  });

  it("sends only the changed paths on save", async () => {
    const onSave = vi.fn();
    render(<Harness onSave={onSave} />);
    fireEvent.change(screen.getByLabelText("Invoice number"), { target: { value: "INV-1002" } });
    fireEvent.change(input("supplier.address.city"), { target: { value: "Bremen" } });
    fireEvent.change(screen.getByLabelText("Line 2, Net"), { target: { value: "70,00" } });
    // Typed and reverted: not a correction.
    fireEvent.change(screen.getByLabelText("Total (gross)"), { target: { value: "1" } });
    fireEvent.change(screen.getByLabelText("Total (gross)"), { target: { value: "303.45" } });
    fireEvent.click(screen.getByRole("button", { name: "save" }));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual([
      { path: "invoiceNumber", value: "INV-1002", reason: null },
      { path: "supplier.address.city", value: "Bremen", reason: null },
      { path: "lines[1].lineNet", value: 70, reason: null },
    ]);
  });

  it("sends nothing when the reviewer changed nothing", async () => {
    const onSave = vi.fn();
    render(<Harness onSave={onSave} />);
    fireEvent.click(screen.getByRole("button", { name: "save" }));
    await waitFor(() => expect(onSave).toHaveBeenCalledWith([]));
  });

  it("shows the per-line arithmetic and re-checks it as the reviewer types", async () => {
    render(<Harness onSave={vi.fn()} />);
    expect(lineRow(0).dataset.arithmetic).toBe("ok");
    expect(lineRow(1).dataset.arithmetic).toBe("mismatch");
    expect(lineRow(2).dataset.arithmetic).toBe("incomplete");
    expect(within(lineRow(1)).getByLabelText("Quantity × unit price is 70, the line says 75")).toBeInTheDocument();
    expect(screen.getByText("1 line does not add up")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Line 2, Net"), { target: { value: "70" } });
    await waitFor(() => expect(lineRow(1).dataset.arithmetic).toBe("ok"));
    expect(screen.getByText("Line arithmetic OK")).toBeInTheDocument();
  });

  it("disables every input of a settled document", () => {
    render(<Harness onSave={vi.fn()} disabled />);
    expect(screen.getByLabelText("Invoice number")).toBeDisabled();
    expect(screen.getByLabelText("Line 1, Qty")).toBeDisabled();
  });
});

describe("ArithmeticIndicator", () => {
  it("renders the signed difference of a mismatch", () => {
    render(<ArithmeticIndicator result={{ status: "mismatch", expected: 70, actual: 75, difference: 5 }} locale="en" />);
    expect(screen.getByText("+5.00")).toBeInTheDocument();
  });

  it("names an incomplete line", () => {
    render(<ArithmeticIndicator result={{ status: "incomplete", expected: null, actual: null, difference: null }} locale="en" />);
    expect(screen.getByLabelText("Quantity, unit price or line net missing")).toBeInTheDocument();
  });
});
