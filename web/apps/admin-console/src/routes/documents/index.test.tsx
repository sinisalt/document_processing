import { screen, within } from "@testing-library/react";
import { beforeEach, vi } from "vitest";
import { queryKeys } from "@docproc/app-shell";
import { answer, devUsers, renderScreen, resetShell, routeComponent, shell } from "@/test/shell";
import { TENANT_A, TENANT_B, documentSummary, page } from "@/test/fixtures";
import { Route } from "./index";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

const Documents = routeComponent(Route);

const rows = [
  documentSummary({ id: "d1", documentNumber: "A-1", tenantId: TENANT_A, tenantName: "Gasthaus zur Post" }),
  documentSummary({ id: "d2", documentNumber: "B-1", tenantId: TENANT_B, tenantName: "Pizzeria Napoli" }),
];

describe("/documents", () => {
  beforeEach(() => resetShell());

  it("shows whose document each row is in platform scope", async () => {
    resetShell(devUsers.operator);
    answer("GET /api/v1/documents", page(rows));
    renderScreen(<Documents />);

    const a = (await screen.findByText("A-1")).closest("tr") as HTMLElement;
    expect(within(a).getByText("Gasthaus zur Post")).toBeInTheDocument();
    expect(within(screen.getByText("B-1").closest("tr") as HTMLElement).getByText("Pizzeria Napoli")).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Tenant" })).toBeInTheDocument();
    // The infinite list has its own cache entry, apart from plain `{limit}` lookups.
    expect(shell.queryClient.getQueryData(queryKeys.documents.pages({}))).toBeDefined();
  });

  it("hides the column for a tenant user, who only ever sees their own tenant", async () => {
    resetShell(devUsers.tenantBViewer);
    answer("GET /api/v1/documents", page([rows[1]!]));
    renderScreen(<Documents />);

    await screen.findByText("B-1");
    expect(screen.queryByRole("columnheader", { name: "Tenant" })).not.toBeInTheDocument();
  });
});
