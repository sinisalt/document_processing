import type { DocumentStatus } from "../components/StatusBadge";

/** Stages `POST /documents/{id}:reprocess` accepts (DocumentEndpoints.ReprocessStart), in pipeline order. */
export const reprocessStages = ["Normalize", "Classify", "Extract", "Validate"] as const;
export type ReprocessStage = (typeof reprocessStages)[number];

/** The status a rerun resets the document to (DocumentEndpoints.ReprocessStart); the document must have been in it. */
export const reprocessStartStatus: Readonly<Record<ReprocessStage, DocumentStatus>> = {
  Normalize: "Received",
  Classify: "Split",
  Extract: "Matched",
  Validate: "Extracted",
};

/** The pipeline's progress states in order (DocumentEndpoints.PipelineOrder). */
export const pipelineOrder: readonly DocumentStatus[] = ["Received", "Normalized", "Split", "Classified", "Matched", "Extracted", "Validated"];

/**
 * The furthest progress state a document has been in, by the API's rule (DocumentEndpoints.FurthestPipelineStatusAsync):
 * the latest of every event's from/to status and the current status, Received when none is a progress state (a
 * document that failed on arrival).
 */
export function furthestPipelineStatus(events: readonly { fromStatus?: DocumentStatus | null; toStatus?: DocumentStatus | null }[], current?: DocumentStatus | null): DocumentStatus {
  let furthest = 0;
  for (const status of [...events.flatMap((event) => [event.fromStatus, event.toStatus]), current]) {
    if (status) furthest = Math.max(furthest, pipelineOrder.indexOf(status));
  }
  return pipelineOrder[furthest]!;
}

/** Whether a rerun from `stage` passes the API's "Stage not reached" check for a document that got as far as `furthest`. */
export function canReprocessFrom(stage: ReprocessStage, furthest: DocumentStatus): boolean {
  return pipelineOrder.indexOf(reprocessStartStatus[stage]) <= pipelineOrder.indexOf(furthest);
}

/** The latest stage the document can rerun from: the dialog's default, so Retry on an early failure does not 409. */
export function defaultReprocessStage(furthest: DocumentStatus): ReprocessStage {
  return [...reprocessStages].reverse().find((stage) => canReprocessFrom(stage, furthest)) ?? "Normalize";
}
