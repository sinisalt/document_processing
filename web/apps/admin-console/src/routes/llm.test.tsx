import { screen } from "@testing-library/react";
import { beforeEach, vi } from "vitest";
import { answer, devUsers, renderScreen, resetShell, routeComponent, shell } from "@/test/shell";
import { page } from "@/test/fixtures";
import { Route } from "./llm";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

const LlmPage = routeComponent(Route);

function serve() {
  answer("GET /api/v1/llm/providers", []);
  answer("GET /api/v1/tenants", page([]));
  answer("GET /api/v1/llm/usage", { total: { calls: 0, errors: 0, estimatedCostUsd: 0 }, from: "2026-10-01", to: "2026-10-31", buckets: [] });
}

describe("/llm", () => {
  beforeEach(() => resetShell());

  it.each([
    ["tenant-a-admin", devUsers.tenantAAdmin],
    ["tenant-b-viewer", devUsers.tenantBViewer],
    ["operator", devUsers.operator],
  ])("as %s (deep link): no GET /tenants, which needs the Platform policy and feeds only the usage panel", async (_name, user) => {
    resetShell(user);
    serve();
    renderScreen(<LlmPage />);
    expect(await screen.findByText("Only super admins manage platform providers.")).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(shell.calls).not.toContain("GET /api/v1/tenants");
    expect(shell.calls).toEqual([]);
  });

  it("as superadmin: loads the providers, the usage and the tenant names", async () => {
    resetShell(devUsers.superadmin);
    serve();
    renderScreen(<LlmPage />);
    await vi.waitFor(() => expect(shell.calls).toEqual(expect.arrayContaining(["GET /api/v1/llm/providers", "GET /api/v1/tenants", "GET /api/v1/llm/usage"])));
  });

  it("says the providers did not load instead of claiming there are none", async () => {
    resetShell(devUsers.superadmin);
    serve();
    answer("GET /api/v1/llm/providers", { title: "Boom", status: 500, detail: "The provider store is down." }, 500);
    renderScreen(<LlmPage />);
    expect(await screen.findByText("This list could not be loaded")).toBeInTheDocument();
    expect(screen.getByText("The provider store is down.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
    expect(screen.queryByText("No providers")).not.toBeInTheDocument();
  });
});
