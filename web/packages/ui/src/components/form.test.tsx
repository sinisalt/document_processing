import { useEffect, type ReactNode } from "react";
import { render, screen } from "@testing-library/react";
import { useForm } from "react-hook-form";
import { Form, FormControl, FormField, FormItem, FormMessage } from "./form";

/** One field `name`; `error` is set on mount so the message has something to show. */
function Harness({ error, message }: { error?: string; message?: ReactNode }) {
  const form = useForm<{ name: string }>({ defaultValues: { name: "" } });
  useEffect(() => {
    if (error !== undefined) form.setError("name", { type: "manual", message: error });
  }, [error, form]);
  return (
    <Form {...form}>
      <FormField
        control={form.control}
        name="name"
        render={({ field }) => (
          <FormItem>
            <FormControl>
              <input aria-label="name" {...field} />
            </FormControl>
            <FormMessage data-testid="message">{message}</FormMessage>
          </FormItem>
        )}
      />
    </Form>
  );
}

describe("FormMessage", () => {
  it("renders nothing without an error or children", () => {
    render(<Harness />);
    expect(screen.queryByTestId("message")).not.toBeInTheDocument();
  });

  it("renders the error's message when no children are given", async () => {
    render(<Harness error="Pflichtfeld." />);
    expect(await screen.findByTestId("message")).toHaveTextContent("Pflichtfeld.");
  });

  it("renders the children instead of the error's message when both are there", async () => {
    render(<Harness error="required" message="Required." />);
    const message = await screen.findByTestId("message");
    expect(message).toHaveTextContent("Required.");
    expect(message).not.toHaveTextContent("required");
    expect(screen.getByLabelText("name")).toHaveAttribute("aria-invalid", "true");
  });

  it("falls back to the error's message when the children are empty", async () => {
    render(<Harness error="Too long." message={undefined} />);
    expect(await screen.findByTestId("message")).toHaveTextContent("Too long.");
  });

  it("renders children without an error (a hint)", () => {
    render(<Harness message="Shown always." />);
    expect(screen.getByTestId("message")).toHaveTextContent("Shown always.");
  });
});
