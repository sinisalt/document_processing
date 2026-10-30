import { fireEvent, render, screen, within } from "@testing-library/react";
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
    fireEvent.click(within(screen.getAllByRole("columnheader")[2]!).getByRole("button", { name: "Sort descending" }));
    expect(names()).toEqual(["Gamma", "Alpha", "Beta"]);
    expect(screen.getByText("gamma details")).toBeInTheDocument();

    rerender(
      <DataTable columns={columns} data={rows.map((row) => ({ ...row }))} getRowId={(row) => row.id} renderSubRow={(row) => <p>{row.details}</p>} rowAttributes={(row) => ({ "data-row": row.id })} />,
    );
    expect(screen.getByText("gamma details")).toBeInTheDocument();
  });
});
