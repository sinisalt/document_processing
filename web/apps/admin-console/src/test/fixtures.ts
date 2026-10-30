import type { components } from "@docproc/api-client";

type DocumentSummary = components["schemas"]["DocumentSummary"];
type ReviewTask = components["schemas"]["ReviewTaskDto"];
type DocumentEvent = components["schemas"]["DocumentEventDto"];

export const TENANT_A = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
export const TENANT_B = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

export function documentSummary(overrides: Partial<DocumentSummary> = {}): DocumentSummary {
  return {
    id: "d0000000-0000-0000-0000-000000000001",
    tenantId: TENANT_A,
    tenantName: "Gasthaus zur Post",
    status: "Failed",
    type: "Invoice",
    typeConfidence: null,
    supplierId: null,
    supplierName: "METRO Deutschland GmbH",
    documentNumber: "RE-1001",
    issueDate: "2026-10-01",
    currency: "EUR",
    totalGross: 166.5,
    extractionMethod: null,
    pageCount: 1,
    blockingFlagCount: 0,
    warningFlagCount: 0,
    createdAt: "2026-10-01T08:00:00Z",
    updatedAt: "2026-10-01T08:05:00Z",
    approvedAt: null,
    ...overrides,
  };
}

export function reviewTask(overrides: Partial<ReviewTask> = {}): ReviewTask {
  return {
    id: "t0000000-0000-0000-0000-000000000001",
    tenantId: TENANT_A,
    tenantName: "Gasthaus zur Post",
    documentId: "d0000000-0000-0000-0000-000000000001",
    reason: "LOW_CONFIDENCE",
    assignedTo: null,
    status: "Open",
    createdAt: "2026-10-01T08:00:00Z",
    slaDueAt: null,
    completedAt: null,
    document: documentSummary({ status: "NeedsReview" }),
    ...overrides,
  };
}

export function documentEvent(overrides: Partial<DocumentEvent> = {}): DocumentEvent {
  return { id: 1, eventType: "document.stage_completed", fromStatus: null, toStatus: null, actor: "pipeline", details: null, at: "2026-10-01T08:00:00Z", ...overrides };
}

export function page<T>(items: T[], nextCursor: string | null = null) {
  return { items, nextCursor };
}
