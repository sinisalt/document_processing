import { vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { ApiError } from "../api/ApiError";
import { createI18n } from "../i18n";
import { DataTable, createDataTableColumnHelper, type DataTableColumns } from "./DataTable";

createI18n({ lng: "en" });

interface Row {
  id: string;
  name: string;
  size: number;
  details: string | null;
}

const helper = createDataTableColumnHelper<Row>();
const columns: DataTableColumns<Row> = helper.columns([
  helper.accessor("name", { header: "Name" }),
  helper.accessor("size", { header: "Size", sortFn: "basic" }),
]);
const rows: Row[] = [
  { id: "a", name: "Alpha", size: 2, details: "alpha details" },
  { id: "b", name: "Beta", size: 1, details: null },
  { id: "c", name: "Gamma", size: 3, details: "gamma details" },
];

function names(): string[] {
  return screen
    .getAllByRole("row")
    .filter((row) => row.getAttribute("data-row") !== null)
    .map((row) => within(row).getAllByRole("cell")[1]!.textContent ?? "");
}

describe("DataTable", () => {
  it("renders no expander column without renderSubRow", () => {
    render(<DataTable columns={columns} data={rows} getRowId={(row) => row.id} />);
    expect(screen.queryByRole("button", { name: "Show details" })).not.toBeInTheDocument();
    expect(screen.getAllByRole("columnheader")).toHaveLength(2);
  });

  it("expands a row into a full-width panel and collapses it again", () => {
    render(
      <DataTable
        columns={columns}
        data={rows}
        getRowId={(row) => row.id}
        renderSubRow={(row) => <p>{row.details}</p>}
        getRowCanExpand={(row) => row.details !== null}
        rowAttributes={(row) => ({ "data-row": row.id })}
      />,
    );

    // Beta has no details: no chevron.
    expect(screen.getAllByRole("button", { name: "Show details" })).toHaveLength(2);
    expect(within(screen.getAllByRole("row").find((row) => row.getAttribute("data-row") === "b")!).queryByRole("button")).toBeNull();

    const toggle = within(screen.getAllByRole("row").find((row) => row.getAttribute("data-row") === "a")!).getByRole("button", { name: "Show details" });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(toggle).toHaveAccessibleName("Hide details");
    const panel = document.getElementById(toggle.getAttribute("aria-controls")!)!;
    expect(panel).toHaveTextContent("alpha details");
    expect(within(panel).getByRole("cell")).toHaveAttribute("colspan", "3");

    fireEvent.click(toggle);
    expect(screen.queryByText("alpha details")).not.toBeInTheDocument();
  });

  it("sorts like any grid and keeps the expanded row open across a sort and a refetch", () => {
    const { rerender } = render(
      <DataTable columns={columns} data={rows} getRowId={(row) => row.id} renderSubRow={(row) => <p>{row.details}</p>} rowAttributes={(row) => ({ "data-row": row.id })} />,
    );
    fireEvent.click(within(screen.getAllByRole("row").find((row) => row.getAttribute("data-row") === "c")!).getByRole("button", { name: "Show details" }));
    expect(names()).toEqual(["Alpha", "Beta", "Gamma"]);

    // A numeric column sorts descending first, and the button says so.
    const sizeButton = within(screen.getAllByRole("columnheader")[2]!).getByRole("button", { name: "Size" });
    expect(sizeButton).toHaveAccessibleDescription("Sort descending");
    fireEvent.click(sizeButton);
    expect(names()).toEqual(["Gamma", "Alpha", "Beta"]);
    expect(screen.getByText("gamma details")).toBeInTheDocument();

    rerender(
      <DataTable columns={columns} data={rows.map((row) => ({ ...row }))} getRowId={(row) => row.id} renderSubRow={(row) => <p>{row.details}</p>} rowAttributes={(row) => ({ "data-row": row.id })} />,
    );
    expect(screen.getByText("gamma details")).toBeInTheDocument();
  });

  it("names a sortable header by its column, with the sort action as its description", () => {
    render(<DataTable columns={columns} data={rows} getRowId={(row) => row.id} />);
    expect(screen.getByRole("columnheader", { name: "Name" })).toHaveAttribute("aria-sort", "none");
    const button = screen.getByRole("button", { name: "Name" });
    expect(button).toHaveAccessibleDescription("Sort ascending");
    fireEvent.click(button);
    expect(screen.getByRole("columnheader", { name: "Name" })).toHaveAttribute("aria-sort", "ascending");
    expect(button).toHaveAccessibleDescription("Sort descending");
  });

  it("shows a failed request as an error with Retry, not as the empty state", () => {
    const refetch = vi.fn();
    const error = new ApiError(500, { title: "Boom", status: 500, detail: "The database is down." }, "/api/v1/documents");
    render(<DataTable columns={columns} data={[]} getRowId={(row) => row.id} emptyTitle="No documents" query={{ isError: true, error, refetch }} />);
    expect(screen.queryByText("No documents")).not.toBeInTheDocument();
    expect(screen.getByText("This list could not be loaded")).toBeInTheDocument();
    expect(screen.getByText("The database is down.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it("keeps the rows of a failed refresh and says that they may be outdated", () => {
    render(<DataTable columns={columns} data={rows} getRowId={(row) => row.id} query={{ isError: true, error: new Error("offline"), refetch: () => undefined }} />);
    expect(screen.getByTestId("table-refresh-error")).toHaveTextContent("The list could not be refreshed");
    expect(screen.getByText("Alpha")).toBeInTheDocument();
  });

  it("shows the empty state when the query succeeded with no rows", () => {
    render(<DataTable columns={columns} data={[]} getRowId={(row) => row.id} emptyTitle="No documents" query={{ isError: false, error: null, refetch: () => undefined }} />);
    expect(screen.getByText("No documents")).toBeInTheDocument();
    expect(screen.queryByText("This list could not be loaded")).not.toBeInTheDocument();
  });
});
