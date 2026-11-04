import { fireEvent, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { i18n } from "@docproc/app-shell";
import { answer, renderScreen, resetShell, shell } from "@/test/shell";
import { FeatureSwitches, withSwitch } from "./FeatureSwitches";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

const off = { configurableTypes: false, customTypes: false, outputs: false, sourceRouting: false, structuredSources: false, supplierLayouts: false };
const settings = { autoApproveConfidenceThreshold: 0.85, retentionYears: 10, shareExtractorProfiles: false, alwaysReviewSupplierIds: ["s1"], features: off };

describe("feature switches", () => {
  beforeEach(async () => {
    resetShell();
    await i18n.changeLanguage("en");
  });

  it("keeps custom types behind configurable types", () => {
    expect(withSwitch(off, "configurableTypes", true)).toMatchObject({ configurableTypes: true, customTypes: false });
    expect(withSwitch(off, "customTypes", true)).toMatchObject({ configurableTypes: true, customTypes: true });
    expect(withSwitch({ ...off, configurableTypes: true, customTypes: true }, "configurableTypes", false)).toMatchObject({ configurableTypes: false, customTypes: false });
    expect(withSwitch({ ...off, outputs: true }, "configurableTypes", true).outputs).toBe(true);
  });

  it("switches configurable types on with the rest of the settings as they are", async () => {
    answer("GET /api/v1/settings", { tenant: {}, settings });
    shell.handlers.set("PUT /api/v1/settings", ({ init }) => ({ body: { tenant: {}, settings: (init as { body: unknown }).body } }));
    renderScreen(<FeatureSwitches />);
    const configurable = await screen.findByRole("switch", { name: /Configurable document types/ });
    expect(configurable).not.toBeChecked();
    fireEvent.click(configurable);
    await waitFor(() => expect(shell.calls).toContain("PUT /api/v1/settings"));
    const put = shell.requests.find((r) => r.route === "PUT /api/v1/settings")!.init as { body: unknown };
    expect(put.body).toEqual({ ...settings, features: { ...off, configurableTypes: true } });
    await waitFor(() => expect(screen.getByRole("switch", { name: /Configurable document types/ })).toBeChecked());
    expect(screen.getByRole("switch", { name: /Types of the tenant's own/ })).not.toBeChecked();
  });

  it("switches outputs on by itself, leaving the type catalogue as it is (docs/11 P3)", async () => {
    expect(withSwitch(off, "outputs", true)).toEqual({ ...off, outputs: true });
    answer("GET /api/v1/settings", { tenant: {}, settings });
    shell.handlers.set("PUT /api/v1/settings", ({ init }) => ({ body: { tenant: {}, settings: (init as { body: unknown }).body } }));
    renderScreen(<FeatureSwitches />);
    const outputs = await screen.findByRole("switch", { name: /Outputs/ });
    fireEvent.click(outputs);
    await waitFor(() => expect(shell.calls).toContain("PUT /api/v1/settings"));
    const put = shell.requests.find((r) => r.route === "PUT /api/v1/settings")!.init as { body: unknown };
    expect(put.body).toEqual({ ...settings, features: { ...off, outputs: true } });
  });

  it("switches source routing and supplier layouts on, each by itself (docs/11 P4)", async () => {
    expect(withSwitch(off, "sourceRouting", true)).toEqual({ ...off, sourceRouting: true });
    expect(withSwitch(off, "supplierLayouts", true)).toEqual({ ...off, supplierLayouts: true });
    answer("GET /api/v1/settings", { tenant: {}, settings });
    shell.handlers.set("PUT /api/v1/settings", ({ init }) => ({ body: { tenant: {}, settings: (init as { body: unknown }).body } }));
    renderScreen(<FeatureSwitches />);
    fireEvent.click(await screen.findByRole("switch", { name: /Supplier layouts/ }));
    await waitFor(() => expect(shell.calls).toContain("PUT /api/v1/settings"));
    const put = shell.requests.find((r) => r.route === "PUT /api/v1/settings")!.init as { body: unknown };
    expect(put.body).toEqual({ ...settings, features: { ...off, supplierLayouts: true } });
    expect(screen.getByRole("switch", { name: /Source routing/ })).not.toBeChecked();
    expect(screen.queryByRole("switch", { name: /structuredSources/ })).toBeNull();
  });
});
