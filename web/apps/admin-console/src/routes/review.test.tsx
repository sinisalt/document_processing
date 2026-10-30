import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, vi } from "vitest";
import { answer, devUsers, renderScreen, resetShell, routeComponent, shell, type DevUser } from "@/test/shell";
import { TENANT_B, documentSummary, page, reviewTask } from "@/test/fixtures";
import { reviewTaskAction } from "@/lib/review";
import { Route } from "./review";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

const Queue = routeComponent(Route);

const tasks = [
  reviewTask({ id: "t-open", document: documentSummary({ id: "d1", documentNumber: "OPEN-1", status: "NeedsReview" }), documentId: "d1" }),
  reviewTask({ id: "t-mine", assignedTo: "operator", status: "InProgress", document: documentSummary({ id: "d2", documentNumber: "MINE-1", status: "NeedsReview" }), documentId: "d2" }),
  reviewTask({ id: "t-other", assignedTo: "anna", status: "InProgress", tenantId: TENANT_B, tenantName: "Pizzeria Napoli", document: documentSummary({ id: "d3", documentNumber: "OTHER-1", status: "NeedsReview", tenantId: TENANT_B }), documentId: "d3" }),
];

function serve(user: DevUser) {
  resetShell(user);
  answer("GET /api/v1/review-tasks", page(tasks));
  answer("POST /api/v1/review-tasks/{id}:release", tasks[2]);
}

function row(documentNumber: string): HTMLElement {
  return screen.getByText(documentNumber).closest("tr") as HTMLElement;
}

describe("reviewTaskAction (ReviewTaskEndpoints rules)", () => {
  it("claims unassigned, releases own, and releases others' only with TenantAdmin", () => {
    expect(reviewTaskAction({ status: "Open", assignedTo: null }, "ops", false)).toBe("claim");
    expect(reviewTaskAction({ status: "InProgress", assignedTo: "ops" }, "ops", false)).toBe("release");
    expect(reviewTaskAction({ status: "InProgress", assignedTo: "anna" }, "ops", false)).toBeNull();
    expect(reviewTaskAction({ status: "InProgress", assignedTo: "anna" }, "root", true)).toBe("releaseOther");
    expect(reviewTaskAction({ status: "Done", assignedTo: null }, "ops", true)).toBeNull();
    expect(reviewTaskAction({ status: "Skipped", assignedTo: "ops" }, "ops", true)).toBeNull();
  });
});

describe("/review", () => {
  beforeEach(() => resetShell());

  it("as operator: Claim on open tasks, Release on own, nothing on someone else's, and a Tenant column", async () => {
    serve(devUsers.operator);
    renderScreen(<Queue />);

    expect(await screen.findByText("OPEN-1")).toBeInTheDocument();
    expect(within(row("OPEN-1")).getByRole("button", { name: "Claim review task for OPEN-1" })).toBeInTheDocument();
    expect(within(row("MINE-1")).getByRole("button", { name: "Release review task for MINE-1" })).toBeInTheDocument();
    expect(within(row("OTHER-1")).queryByRole("button")).not.toBeInTheDocument();

    expect(screen.getByRole("columnheader", { name: "Tenant" })).toBeInTheDocument();
    expect(within(row("OTHER-1")).getByText("Pizzeria Napoli")).toBeInTheDocument();
  });

  it("as superadmin: releasing someone else's task asks first", async () => {
    serve({ ...devUsers.superadmin, name: "operator" });
    renderScreen(<Queue />);

    await screen.findByText("OTHER-1");
    fireEvent.click(within(row("OTHER-1")).getByRole("button", { name: "Release review task for OTHER-1 held by anna" }));
    const confirm = await screen.findByRole("alertdialog");
    expect(confirm).toHaveTextContent("anna holds this task");
    expect(shell.calls).not.toContain("POST /api/v1/review-tasks/{id}:release");

    fireEvent.click(within(confirm).getByRole("button", { name: "Release" }));
    await waitFor(() => expect(shell.calls).toContain("POST /api/v1/review-tasks/{id}:release"));
    expect(shell.requests.find((r) => r.route === "POST /api/v1/review-tasks/{id}:release")?.init).toMatchObject({ params: { path: { id: "t-other" } } });
  });

  it("without the Review policy: no queue request and no action column", async () => {
    serve(devUsers.tenantBViewer);
    renderScreen(<Queue />);

    expect(await screen.findByText("The review queue needs a reviewer role")).toBeInTheDocument();
    expect(shell.calls).toEqual([]);
    expect(screen.queryByRole("button", { name: /Claim|Release/ })).not.toBeInTheDocument();
  });
});
