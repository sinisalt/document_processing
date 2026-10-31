import { render, screen } from "@testing-library/react";
import { PageHeader } from "./PageHeader";

describe("PageHeader", () => {
  it("lets its actions wrap and shrink, so four buttons on a phone stay on screen", () => {
    render(<PageHeader title="Document" actions={<button type="button">Approve</button>} />);
    // jsdom has no layout: the classes are the contract (flex-wrap breaks the row, min-w-0 lets it be narrower than its content).
    const actions = screen.getByRole("button", { name: "Approve" }).parentElement!;
    expect(actions).toHaveClass("flex-wrap", "min-w-0");
    expect(actions).not.toHaveClass("shrink-0");
  });
});
