import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, vi } from "vitest";
import { i18n } from "@docproc/app-shell";
import { renderScreen, resetShell, shell } from "@/test/shell";
import { TenantFormDialog } from "./TenantFormDialog";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

/** Opens the dialog, empties the prefilled fields and submits. */
async function submitEmpty() {
  renderScreen(<TenantFormDialog />);
  fireEvent.click(screen.getByRole("button", { name: i18n.t("admin:tenants.create") }));
  const dialog = await screen.findByRole("dialog");
  for (const input of within(dialog).getAllByRole("textbox")) fireEvent.change(input, { target: { value: "" } });
  fireEvent.click(within(dialog).getByRole("button", { name: i18n.t("admin:tenants.submit") }));
  return dialog;
}

/** zod's English defaults ("Too small: expected string to have >=1 characters", "Invalid string: must match pattern …"). */
const ZOD_DEFAULT = /expected|Invalid|Too small|Too big|must match/;

describe("TenantFormDialog validation", () => {
  beforeEach(() => resetShell());
  afterEach(async () => {
    await i18n.changeLanguage("en");
  });

  it("shows the translated required message, not the code, in English", async () => {
    await i18n.changeLanguage("en");
    const dialog = await submitEmpty();

    await waitFor(() => expect(within(dialog).getAllByText("Required.").length).toBeGreaterThanOrEqual(3));
    expect(within(dialog).queryByText("required")).not.toBeInTheDocument();
    expect(within(dialog).getByText("A three-letter ISO 4217 code, e.g. EUR.")).toBeInTheDocument();
    expect(within(dialog).getByText("A two-letter ISO 3166-1 code, e.g. DE.")).toBeInTheDocument();
    expect(dialog.textContent).not.toMatch(ZOD_DEFAULT);
    expect(shell.calls).toEqual([]);
  });

  it("shows the translated required message in German", async () => {
    await i18n.changeLanguage("de");
    const dialog = await submitEmpty();

    await waitFor(() => expect(within(dialog).getAllByText("Pflichtfeld.").length).toBeGreaterThanOrEqual(3));
    expect(within(dialog).queryByText("Required.")).not.toBeInTheDocument();
    expect(within(dialog).queryByText("required")).not.toBeInTheDocument();
    expect(dialog.textContent).not.toMatch(ZOD_DEFAULT);
  });

  it("translates a check without a message of its own through the shell's error map", async () => {
    await i18n.changeLanguage("de");
    renderScreen(<TenantFormDialog />);
    fireEvent.click(screen.getByRole("button", { name: i18n.t("admin:tenants.create") }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByLabelText(i18n.t("admin:tenants.vatId")), { target: { value: "X".repeat(40) } });
    fireEvent.click(within(dialog).getByRole("button", { name: i18n.t("admin:tenants.submit") }));

    expect(await within(dialog).findByText("Höchstens 32 Zeichen.")).toBeInTheDocument();
  });
});
