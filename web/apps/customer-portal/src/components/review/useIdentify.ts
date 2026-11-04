import { useCallback, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ApiError, unwrap, useApi } from "@docproc/app-shell";
import { grow, union, type Box } from "@docproc/review";
import type { PagePick } from "./FieldHighlightOverlay";
import { newChip, ruleOf, type Identifier, type IdentificationDraft, type IdentifierChip } from "@/lib/layouts";

/** A selection on the page that the API turned into an identifier, before the reviewer adds it as a chip. */
export interface IdentifierProposal {
  page: number;
  words: { index: number; box: Box }[];
  box: Box;
  identifier: Identifier | null;
  describe: string;
  holds: boolean;
  warnings: string[];
  loading: boolean;
  error: string | null;
}

/**
 * Identify mode (docs/11 §12.9): a click on a word, a shift-click adding one, or a box drawn becomes a proposal through
 * `POST /documents/{id}/identifiers:propose` (a title-band word becomes `{text, page, region: title}`, the supplier's
 * VAT id a `supplierValue`); adding it makes a chip of the Identification card. Every change of the chips runs
 * `POST /identification-rules:test` on this document and on the supplier's last 50 documents.
 */
export function useIdentify(documentId: string, supplierId: string | null, draft: IdentificationDraft, onDraftChange: (next: IdentificationDraft) => void, typeKey: string | null) {
  const { client } = useApi();
  const [proposal, setProposal] = useState<IdentifierProposal | null>(null);
  const request = useRef(0);

  const propose = useCallback(
    async (page: number, words: { index: number; box: Box }[], box: Box, drawn: boolean) => {
      const id = ++request.current;
      setProposal({ page, words, box, identifier: null, describe: "", holds: false, warnings: [], loading: true, error: null });
      try {
        const body = drawn ? { page, bBox: box } : { page, words: words.map((w) => w.index) };
        const result = unwrap(await client.POST("/api/v1/documents/{id}/identifiers:propose", { params: { path: { id: documentId } }, body }));
        if (id !== request.current) return;
        setProposal({ page, words, box, identifier: result.identifier as unknown as Identifier, describe: result.describe, holds: result.holds, warnings: result.warnings, loading: false, error: null });
      } catch (e) {
        if (id !== request.current) return;
        const message = e instanceof ApiError ? (e.problem?.detail ?? e.title) : e instanceof Error ? e.message : String(e);
        setProposal({ page, words, box, identifier: null, describe: "", holds: false, warnings: [], loading: false, error: message });
      }
    },
    [client, documentId],
  );

  const pick = useCallback(
    (page: number, picked: PagePick) => {
      if (picked.kind === "region") {
        void propose(page, [], picked.box, true);
        return;
      }
      const keep = picked.additive && proposal?.page === page ? proposal.words : [];
      const words = keep.some((w) => w.index === picked.index) ? keep.filter((w) => w.index !== picked.index) : [...keep, { index: picked.index, box: picked.box }];
      if (words.length === 0) {
        setProposal(null);
        return;
      }
      void propose(page, words, grow(union(words.map((w) => w.box))!), false);
    },
    [proposal, propose],
  );

  const add = useCallback(() => {
    if (!proposal?.identifier) return;
    onDraftChange({ ...draft, chips: [...draft.chips, newChip(proposal.identifier, proposal.describe)] });
    setProposal(null);
  }, [proposal, draft, onDraftChange]);

  const cancel = useCallback(() => {
    request.current++;
    setProposal(null);
  }, []);

  const update = useCallback(
    (chipId: string, change: (chip: IdentifierChip) => IdentifierChip | null) => {
      const chips = draft.chips.map((c) => (c.id === chipId ? change(c) : c)).filter((c): c is IdentifierChip => c !== null);
      onDraftChange({ ...draft, chips });
    },
    [draft, onDraftChange],
  );

  const rule = useMemo(() => ruleOf(draft), [draft]);
  const ruleJson = rule ? JSON.stringify(rule) : null;
  const scope = supplierId ? { supplierId } : undefined;

  // "Holds for this document" and the evidence drawn on the page.
  const here = useQuery({
    queryKey: ["supplier-layouts", "identify", documentId, "here", ruleJson],
    queryFn: async () => unwrap(await client.POST("/api/v1/identification-rules:test", { body: { rule: rule as never, scope, documentIds: [documentId] } })),
    enabled: rule !== null && supplierId !== null,
    retry: false,
    meta: { silent: true },
  });
  // Against the supplier's last 50 documents: how many it matches per layout, and the overlaps with other layouts.
  const recent = useQuery({
    queryKey: ["supplier-layouts", "identify", documentId, "recent", ruleJson],
    queryFn: async () => unwrap(await client.POST("/api/v1/identification-rules:test", { body: { rule: rule as never, scope, lastN: 50, typeKey: typeKey ?? undefined } })),
    enabled: rule !== null && supplierId !== null,
    retry: false,
    meta: { silent: true },
  });

  return { proposal, pick, add, cancel, update, rule, here, recent };
}

/** The problem of a failed rule test in words: the API's detail (a 400 names the place, `rule.all[1]`). */
export function testProblem(error: unknown): string | null {
  if (!error) return null;
  if (error instanceof ApiError) return error.problem?.detail ?? error.title;
  return error instanceof Error ? error.message : String(error);
}
