import { useTranslation } from "react-i18next";
import { Ban, CaseSensitive, Check, CircleAlert, Expand, Loader2, Plus, Trash2, X } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Button, ToggleGroup, ToggleGroupItem } from "@docproc/ui";
import { canBeCaseSensitive, locationOf, toggleCaseSensitive, widen, type IdentificationDraft, type IdentificationTestResponse } from "@/lib/layouts";
import { testProblem, type IdentifierProposal } from "./useIdentify";
import type { IdentifierChip } from "@/lib/layouts";

export interface IdentificationCardProps {
  draft: IdentificationDraft;
  onDraftChange: (next: IdentificationDraft) => void;
  proposal: IdentifierProposal | null;
  onAdd: () => void;
  onCancel: () => void;
  onUpdate: (chipId: string, change: (chip: IdentifierChip) => IdentifierChip | null) => void;
  here: { data?: IdentificationTestResponse; isFetching: boolean; error: unknown };
  recent: { data?: IdentificationTestResponse; isFetching: boolean; error: unknown };
  documentId: string;
  /** Identify mode is on: the reviewer is marking identifiers on the page. */
  active: boolean;
  onActivate: () => void;
  /** The layout that identified the document, whose evidence the page shows. */
  identifiedBy?: string | null;
}

/**
 * The Identification card (docs/11 §12.9 step 3 and 4): the identifiers marked in Identify mode as chips, "All of
 * these" or "Any of these", per chip widen the location, case-sensitive, "must not contain" and remove; the rule's
 * live test on this document and on the supplier's recent documents, with the overlaps with other layouts in red.
 */
export function IdentificationCard({ draft, onDraftChange, proposal, onAdd, onCancel, onUpdate, here, recent, documentId, active, onActivate, identifiedBy }: IdentificationCardProps) {
  const { t } = useTranslation();
  const holds = here.data?.documents.find((d) => d.documentId === documentId);
  const others = (recent.data?.documents ?? []).filter((d) => d.documentId !== documentId);
  const matched = others.filter((d) => d.matched);
  const perLayout = new Map<string, number>();
  for (const doc of matched) {
    const key = doc.layoutKey ?? t("review.identify.noLayout");
    perLayout.set(key, (perLayout.get(key) ?? 0) + 1);
  }
  const overlaps = recent.data?.overlaps ?? [];
  const problem = testProblem(here.error) ?? testProblem(recent.error);

  return (
    <section className="flex flex-col gap-3" aria-labelledby="identification-heading" data-testid="identification-card">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="identification-heading" className="text-sm font-semibold">
          {t("review.identify.title")}
        </h2>
        {!active && (
          <Button type="button" size="sm" variant="outline" onClick={onActivate} data-testid="identify-start">
            {t("review.identify.start")}
          </Button>
        )}
      </div>
      <p className="text-sm text-muted-foreground">{identifiedBy ? t("review.identify.identifiedBy", { layout: identifiedBy }) : t("review.identify.hint")}</p>

      {proposal && (
        <div className="flex flex-col gap-2 rounded-md border border-violet-300 bg-violet-50 p-3 dark:bg-violet-950/30" data-testid="identifier-proposal" aria-live="polite">
          {proposal.loading ? (
            <span className="flex items-center gap-2 text-sm">
              <Loader2 className="size-4 animate-spin" aria-hidden />
              {t("review.identify.proposing")}
            </span>
          ) : proposal.error ? (
            <span className="text-sm text-destructive">{proposal.error}</span>
          ) : (
            <>
              <span className="text-sm font-medium" data-testid="identifier-proposal-text">
                {proposal.describe}
              </span>
              {proposal.warnings.map((warning) => (
                <span key={warning} className="flex items-start gap-1 text-xs text-amber-700 dark:text-amber-400">
                  <CircleAlert className="mt-0.5 size-3 shrink-0" aria-hidden />
                  {warning}
                </span>
              ))}
            </>
          )}
          <div className="flex gap-2">
            <Button type="button" size="sm" onClick={onAdd} disabled={!proposal.identifier} data-testid="identifier-add">
              <Plus aria-hidden />
              {t("review.identify.add")}
            </Button>
            <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
              <X aria-hidden />
              {t("review.identify.cancel")}
            </Button>
          </div>
        </div>
      )}

      {draft.chips.length > 0 && (
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={draft.connective}
          onValueChange={(value) => value && onDraftChange({ ...draft, connective: value as "all" | "any" })}
          aria-label={t("review.identify.connective")}
        >
          <ToggleGroupItem value="all" data-testid="identify-all">
            {t("review.identify.all")}
          </ToggleGroupItem>
          <ToggleGroupItem value="any" data-testid="identify-any">
            {t("review.identify.any")}
          </ToggleGroupItem>
        </ToggleGroup>
      )}

      {draft.chips.length === 0 ? (
        <p className="rounded-md border border-dashed p-3 text-sm text-muted-foreground" data-testid="identify-empty">
          {t("review.identify.empty")}
        </p>
      ) : (
        <ul className="flex flex-col gap-2" aria-label={t("review.identify.chips")}>
          {draft.chips.map((chip) => {
            const location = locationOf(chip.identifier);
            return (
              <li key={chip.id} className="flex flex-wrap items-center gap-2 rounded-md border px-2 py-1.5" data-testid="identifier-chip" data-negative={chip.negative}>
                <Badge variant={chip.negative ? "destructive" : "secondary"}>{chip.negative ? t("review.identify.mustNot") : t("review.identify.must")}</Badge>
                <span className="min-w-0 flex-1 text-sm break-words">{chip.describe}</span>
                <Badge variant="outline" className="text-xs">
                  {t(`review.identify.location.${location}`)}
                </Badge>
                {chip.identifier.caseSensitive === true && <Badge variant="outline">{t("review.identify.caseSensitive")}</Badge>}
                <div className="flex gap-0.5">
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-xs"
                    disabled={location === "anyPage"}
                    aria-label={t("review.identify.widen")}
                    title={t("review.identify.widen")}
                    onClick={() => onUpdate(chip.id, (c) => ({ ...c, identifier: widen(c.identifier), describe: `${c.describe} (${t(`review.identify.location.${locationOf(widen(c.identifier))}`)})` }))}
                  >
                    <Expand aria-hidden />
                  </Button>
                  {canBeCaseSensitive(chip.identifier) && (
                    <Button type="button" variant="ghost" size="icon-xs" aria-label={t("review.identify.toggleCase")} title={t("review.identify.toggleCase")} onClick={() => onUpdate(chip.id, (c) => ({ ...c, identifier: toggleCaseSensitive(c.identifier) }))}>
                      <CaseSensitive aria-hidden />
                    </Button>
                  )}
                  <Button type="button" variant="ghost" size="icon-xs" aria-label={t("review.identify.toggleNegative")} title={t("review.identify.toggleNegative")} onClick={() => onUpdate(chip.id, (c) => ({ ...c, negative: !c.negative }))}>
                    <Ban aria-hidden />
                  </Button>
                  <Button type="button" variant="ghost" size="icon-xs" aria-label={t("review.identify.remove")} title={t("review.identify.remove")} onClick={() => onUpdate(chip.id, () => null)}>
                    <Trash2 aria-hidden />
                  </Button>
                </div>
              </li>
            );
          })}
        </ul>
      )}

      {draft.chips.some((c) => !c.negative) && (
        <div className="flex flex-col gap-2 text-sm" data-testid="identify-test" aria-live="polite">
          {here.isFetching || recent.isFetching ? (
            <span className="flex items-center gap-2 text-muted-foreground">
              <Loader2 className="size-4 animate-spin" aria-hidden />
              {t("review.identify.testing")}
            </span>
          ) : problem ? (
            <span className="text-destructive">{problem}</span>
          ) : (
            <>
              {holds && (
                <span className={holds.matched ? "flex items-center gap-1 text-emerald-700 dark:text-emerald-400" : "flex items-center gap-1 text-destructive"} data-testid="identify-holds" data-holds={holds.matched}>
                  {holds.matched ? <Check className="size-4" aria-hidden /> : <X className="size-4" aria-hidden />}
                  {holds.matched ? t("review.identify.holds") : t("review.identify.doesNotHold", { failed: holds.failed.join("; ") })}
                </span>
              )}
              {recent.data && (
                <span data-testid="identify-recent">
                  {t("review.identify.recent", { matched: matched.length, tested: others.length })}
                  {perLayout.size > 0 && ` (${[...perLayout.entries()].map(([key, count]) => `${key}: ${count}`).join(", ")})`}
                </span>
              )}
              {overlaps.length > 0 && (
                <Alert variant="destructive" data-testid="identify-overlap">
                  <AlertTitle>{t("review.identify.overlapTitle")}</AlertTitle>
                  <AlertDescription>{t("review.identify.overlap", { layouts: overlaps.map((o) => `${o.layoutKey} (${String(o.documents)})`).join(", ") })}</AlertDescription>
                </Alert>
              )}
            </>
          )}
        </div>
      )}
    </section>
  );
}
