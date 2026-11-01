import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { beforeAll, beforeEach, vi } from "vitest";
import { i18n } from "@docproc/app-shell";
import { answer, renderScreen, resetShell, shell } from "@/test/shell";
import { documentEvent, documentSummary } from "@/test/fixtures";
import { ReprocessDialog } from "./DocumentActions";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

const ID = "d0000000-0000-0000-0000-000000000001";
const stageLabel = (stage: string) => i18n.t(`admin:stages.${stage}`);

/** Opens the stage Select with the keyboard and lists its options with their disabled state. */
async function stageOptions(trigger: HTMLElement): Promise<{ label: string; disabled: boolean }[]> {
  fireEvent.keyDown(trigger, { key: "Enter" });
  const listbox = await screen.findByRole("listbox");
  const options = within(listbox)
    .getAllByRole("option")
    .map((option) => ({ label: option.textContent ?? "", disabled: option.getAttribute("aria-disabled") === "true" }));
  fireEvent.keyDown(listbox, { key: "Escape" });
  return options;
}

describe("ReprocessDialog", () => {
  beforeAll(() => {
    // Radix Select uses pointer capture and scrollIntoView, which jsdom lacks.
    Element.prototype.hasPointerCapture ??= () => false;
    Element.prototype.releasePointerCapture ??= () => {};
    Element.prototype.scrollIntoView ??= () => {};
  });
  beforeEach(() => resetShell());

  it("defaults to Normalize for a document that failed in Normalize, disables the later stages and posts Normalize", async () => {
    const events = [documentEvent({ id: 1, eventType: "document.received", toStatus: "Received" }), documentEvent({ id: 2, eventType: "document.stage_failed", fromStatus: "Received", toStatus: "Failed", details: { stage: "Normalize" } })];
    answer(`POST /api/v1/documents/{id}:reprocess`, documentSummary({ status: "Received" }));
    renderScreen(<ReprocessDialog documentId={ID} documentLabel="RE-1001" status="Failed" events={events} open onOpenChange={() => {}} />);

    const dialog = await screen.findByRole("alertdialog");
    const trigger = within(dialog).getByRole("combobox", { name: i18n.t("admin:document.fromStage") });
    expect(trigger).toHaveTextContent(stageLabel("Normalize"));
    expect(within(dialog).getByTestId("reprocess-reached")).toHaveTextContent(i18n.t("common:documentStatus.Received"));
    expect(await stageOptions(trigger)).toEqual([
      { label: stageLabel("Normalize"), disabled: false },
      { label: stageLabel("Classify"), disabled: true },
      { label: stageLabel("Extract"), disabled: true },
      { label: stageLabel("Validate"), disabled: true },
    ]);

    fireEvent.click(within(dialog).getByRole("button", { name: i18n.t("admin:document.reprocess") }));
    await waitFor(() => expect(shell.calls).toContain("POST /api/v1/documents/{id}:reprocess"));
    expect(shell.requests.find((r) => r.route === "POST /api/v1/documents/{id}:reprocess")?.init).toMatchObject({ params: { path: { id: ID } }, body: { fromStage: "Normalize" } });
  });

  it("loads the timeline itself when the caller has none (dashboard Retry) and defaults to the latest reached stage", async () => {
    answer("GET /api/v1/documents/{id}/events", [
      documentEvent({ id: 1, fromStatus: "Received", toStatus: "Normalized" }),
      documentEvent({ id: 2, fromStatus: "Normalized", toStatus: "Split" }),
      documentEvent({ id: 3, fromStatus: "Split", toStatus: "Classified" }),
      documentEvent({ id: 4, fromStatus: "Classified", toStatus: "Matched" }),
      documentEvent({ id: 5, eventType: "document.stage_failed", fromStatus: "Matched", toStatus: "Failed" }),
    ]);
    renderScreen(<ReprocessDialog documentId={ID} documentLabel="RE-1001" status="Failed" open onOpenChange={() => {}} />);

    const dialog = await screen.findByRole("alertdialog");
    const trigger = within(dialog).getByRole("combobox", { name: i18n.t("admin:document.fromStage") });
    await waitFor(() => expect(trigger).toHaveTextContent(stageLabel("Extract")));
    expect(shell.calls).toEqual(["GET /api/v1/documents/{id}/events"]);
    const options = await stageOptions(trigger);
    expect(options.find((o) => o.label === stageLabel("Validate"))?.disabled).toBe(true);
    expect(options.find((o) => o.label === stageLabel("Extract"))?.disabled).toBe(false);
  });

  it("does not load the timeline while closed", () => {
    renderScreen(<ReprocessDialog documentId={ID} documentLabel="RE-1001" status="Failed" open={false} onOpenChange={() => {}} />);
    expect(shell.calls).toEqual([]);
  });

  it("falls back to the stages the status proves, with a retry, when the timeline does not load", async () => {
    answer("GET /api/v1/documents/{id}/events", { title: "Boom", status: 500 }, 500);
    renderScreen(<ReprocessDialog documentId={ID} documentLabel="RE-1001" status="Failed" open onOpenChange={() => {}} />);

    const dialog = await screen.findByRole("alertdialog");
    const trigger = within(dialog).getByRole("combobox", { name: i18n.t("admin:document.fromStage") });
    // Not stuck on "Loading the timeline": Normalize is always reachable, and the API's 409 guards the rest.
    await waitFor(() => expect(trigger).toHaveTextContent(stageLabel("Normalize")));
    expect(within(dialog).getByRole("button", { name: i18n.t("admin:document.reprocess") })).toBeEnabled();
    const failed = within(dialog).getByTestId("reprocess-timeline-failed");
    expect(failed).toHaveTextContent(i18n.t("admin:document.fromStageTimelineFailed"));

    answer("GET /api/v1/documents/{id}/events", [documentEvent({ id: 1, fromStatus: "Matched", toStatus: "Extracted" }), documentEvent({ id: 2, eventType: "document.stage_failed", fromStatus: "Extracted", toStatus: "Failed" })]);
    fireEvent.click(within(failed).getByRole("button", { name: i18n.t("common:errors.tryAgain") }));
    await waitFor(() => expect(trigger).toHaveTextContent(stageLabel("Validate")));
    expect(within(dialog).queryByTestId("reprocess-timeline-failed")).not.toBeInTheDocument();
  });
});
