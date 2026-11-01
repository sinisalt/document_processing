import { screen, waitFor, within } from "@testing-library/react";
import { beforeEach, vi } from "vitest";
import { answer, devUsers, renderScreen, resetShell, routeComponent, shell, type DevUser } from "@/test/shell";
import { TENANT_B, documentSummary, page, reviewTask } from "@/test/fixtures";
import { Route } from "./index";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

const Dashboard = routeComponent(Route);

function serve(user: DevUser) {
  resetShell(user);
  shell.handlers.set("GET /api/v1/documents", ({ init }) => {
    const status = (init as { params: { query: { status?: string } } }).params.query.status;
    return { body: page(status === "Failed" ? [documentSummary({ tenantId: TENANT_B, tenantName: "Pizzeria Napoli" })] : []) };
  });
  answer("GET /api/v1/tenants", page([{ id: TENANT_B, name: "Pizzeria Napoli", status: "Active" }]));
  answer("GET /api/v1/review-tasks", page([reviewTask(), reviewTask({ id: "t2" })]));
  answer("GET /api/v1/llm/usage", { total: { calls: 0, estimatedCostUsd: 0 }, from: "2026-10-01", to: "2026-10-31", buckets: [] });
}

/** The value line of a stat card, found by its title. */
function stat(title: string): HTMLElement {
  const values = screen
    .getAllByText(title, { selector: "[data-slot=card-title]" })
    .map((element) => element.closest("[data-slot=card]")?.querySelector<HTMLElement>(".text-3xl"))
    .filter((element): element is HTMLElement => element != null);
  expect(values).toHaveLength(1);
  return values[0]!;
}

describe("dashboard role gating", () => {
  beforeEach(() => resetShell());

  it("as tenant-b-viewer: no tenants or review-queue request, '—' instead of skeletons, no Retry, own-tenant hints and a notice", async () => {
    serve(devUsers.tenantBViewer);
    renderScreen(<Dashboard />);

    expect(await screen.findByText("RE-1001")).toBeInTheDocument();
    await waitFor(() => expect(stat("Failed documents")).toHaveTextContent("1"));

    expect(shell.calls).not.toContain("GET /api/v1/tenants");
    expect(shell.calls).not.toContain("GET /api/v1/review-tasks");
    expect(shell.calls).not.toContain("GET /api/v1/llm/usage");
    expect(stat("Tenants")).toHaveTextContent("—");
    expect(stat("Review backlog")).toHaveTextContent("—");
    expect(screen.getByText("Visible to platform operators only.")).toBeInTheDocument();
    expect(screen.getByText("Visible to reviewers only.")).toBeInTheDocument();

    expect(screen.queryByRole("button", { name: /Retry/ })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Inspect" })).toBeInTheDocument();

    expect(screen.queryByText("Across all tenants")).not.toBeInTheDocument();
    expect(screen.getAllByText("In your tenant").length).toBeGreaterThan(0);
    expect(screen.getByText("Documents of your tenant.")).toBeInTheDocument();

    const notice = screen.getByTestId("platform-only-notice");
    expect(within(notice).getByText("This console is for platform operators")).toBeInTheDocument();
    expect(notice).toHaveTextContent("Viewer");
  });

  it("as operator: platform counts and Retry, but no spend and no notice", async () => {
    serve(devUsers.operator);
    renderScreen(<Dashboard />);

    await waitFor(() => expect(stat("Tenants")).toHaveTextContent("1"));
    await waitFor(() => expect(stat("Review backlog")).toHaveTextContent("2"));
    expect(screen.getByRole("button", { name: "Retry document RE-1001" })).toBeInTheDocument();
    expect(screen.getAllByText("Across all tenants").length).toBeGreaterThan(0);
    expect(screen.queryByTestId("platform-only-notice")).not.toBeInTheDocument();
    expect(shell.calls).not.toContain("GET /api/v1/llm/usage");
  });

  it("shows '—' when a query fails instead of a skeleton forever", async () => {
    serve(devUsers.superadmin);
    answer("GET /api/v1/review-tasks", { title: "Forbidden", status: 403 }, 403);
    renderScreen(<Dashboard />);

    await waitFor(() => expect(stat("Review backlog")).toHaveTextContent("—"));
    await waitFor(() => expect(stat("Tenants")).toHaveTextContent("1"));
  });
});
