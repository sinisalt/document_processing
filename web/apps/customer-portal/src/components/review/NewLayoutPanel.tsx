import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Check, Hourglass, Layers, Loader2, Sparkles } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Button, Checkbox, Input, Label, RadioGroup, RadioGroupItem, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { localized, useDocumentTypes } from "@docproc/review";
import { useLayoutSuggestions, useSupplierLayouts } from "@/lib/layoutQueries";
import { flagSuggestions, isLayoutKey, prefillOf, suggestLayoutKey, type LayoutSaveChoice } from "@/lib/layouts";
import type { DocumentDetail, DocumentFlag } from "@/lib/queries";
import { usableTypes } from "./ReviewDialogs";

/** The pre-fill sources in the order the panel lists them (docs/11 §12.10 step 4). */
export const PREFILL_SOURCES = ["pendingLayout", "otherLayout", "definitionLabel", "masterData", "knownValue", "llm"] as const;

/** The default save choice for a NEW_LAYOUT document: a new layout of the supplier, or confirming the pending Candidate. */
export function defaultLayoutChoice(document: DocumentDetail, flag: DocumentFlag | null, existingKeys: readonly string[]): LayoutSaveChoice {
  const pending = flag?.params?.pendingLayoutVersionId || document.pendingLayoutVersionId || null;
  const typeKey = document.definition?.key ?? document.type;
  return {
    saveAsLayout: true,
    saveAs: flag?.params?.saveAs === "newVersion" ? "newVersion" : "newLayout",
    targetLayoutId: flag?.params?.targetLayoutId || null,
    layoutKey: suggestLayoutKey(document.supplierName ?? flag?.params?.supplierName, typeKey, existingKeys),
    name: "",
    pendingLayoutVersionId: pending,
    confirmNotRecognised: false,
  };
}

export interface NewLayoutPanelProps {
  document: DocumentDetail;
  flag: DocumentFlag;
  locale: string;
  choice: LayoutSaveChoice;
  onChoiceChange: (next: LayoutSaveChoice) => void;
  /** The field's label for the pre-fill list. */
  fieldLabel: (path: string) => string;
  canReview: boolean;
}

/**
 * The New layout panel (docs/11 §12.10 step 2) for a document held with `NEW_LAYOUT` (or `LAYOUT_AMBIGUOUS`): the
 * supplier sent a design none of its layouts recognises. It leads with the pending Candidate when one holds ("confirm
 * against pending layout"), offers the type (the supplier's layout types first, the proposal preselected), the
 * suggestions, and how the approval saves it (a new layout, or a new version of one); `:match-layout` records the
 * choice and pre-fills again when the type or the basis changed. It lists where every pre-filled value came from.
 */
export function NewLayoutPanel({ document, flag, locale, choice, onChoiceChange, fieldLabel, canReview }: NewLayoutPanelProps) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const supplierName = document.supplierName ?? flag.params.supplierName ?? t("review.noSupplier");
  const types = useDocumentTypes();
  const layouts = useSupplierLayouts(document.supplierId);
  const suggestions = useLayoutSuggestions(document.id, !!document.supplierId && flag.code === "NEW_LAYOUT");
  const currentType = document.definition?.key ?? (document.type === "Unknown" || document.type === "Other" ? "" : document.type);
  const proposedType = flag.params.proposedType ?? "";
  const pendingId = flag.params.pendingLayoutVersionId || document.pendingLayoutVersionId || null;
  const pendingKey = flag.params.pendingLayoutKey || layouts.data?.find((l) => l.versions.some((v) => v.id === pendingId))?.layoutKey || null;
  const basedOn = flag.params.basedOnLayoutId ?? null;

  // The supplier's layout types first, then the proposal, then every usable type (§12.10 step 2 "Type").
  const typeOptions = useMemo(() => {
    const usable = usableTypes(types.data);
    const first = new Set([...(layouts.data ?? []).map((l) => l.documentTypeKey), proposedType].filter(Boolean));
    return [...usable].sort((a, b) => Number(first.has(b.key)) - Number(first.has(a.key)));
  }, [types.data, layouts.data, proposedType]);
  const typeKey = currentType || proposedType;
  const sameTypeLayouts = (layouts.data ?? []).filter((l) => l.documentTypeKey === typeKey && l.status !== "Retired");
  const suggestionList = suggestions.data && suggestions.data.length > 0 ? suggestions.data.map((s) => ({ layoutId: s.layoutId, layoutKey: s.layoutKey, documentTypeKey: s.documentTypeKey, similarity: Number(s.similarity) })) : flagSuggestions(flag);
  const prefill = prefillOf(document.appliedConfig);
  const sources = prefill ? Object.entries(prefill.sources).sort(([a], [b]) => a.localeCompare(b)) : [];

  const match = useMutation({
    mutationFn: async (body: { typeKey: string; basedOnLayoutId?: string | null; pendingLayoutVersionId?: string | null }) =>
      unwrap(
        await client.POST("/api/v1/documents/{id}:match-layout", {
          params: { path: { id: document.id } },
          body: {
            typeKey: body.typeKey,
            basedOnLayoutId: body.basedOnLayoutId ?? null,
            pendingLayoutVersionId: body.pendingLayoutVersionId ?? null,
            saveAs: choice.saveAs,
            targetLayoutId: choice.saveAs === "newVersion" ? choice.targetLayoutId : null,
          },
        }),
      ),
    onSuccess: (updated, body) => {
      queryClient.setQueryData(queryKeys.documents.detail(document.id), updated);
      void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
      toast.success(body.pendingLayoutVersionId ? t("review.newLayout.confirmedPending", { layout: pendingKey ?? "" }) : t("review.newLayout.matched", { type: body.typeKey }));
    },
  });

  if (flag.code === "LAYOUT_AMBIGUOUS") {
    return (
      <Alert data-testid="layout-ambiguous-panel">
        <Layers aria-hidden />
        <AlertTitle>{t("review.newLayout.ambiguousTitle")}</AlertTitle>
        <AlertDescription>{t("review.newLayout.ambiguous", { supplier: supplierName, layouts: flag.params.layouts ?? "" })}</AlertDescription>
      </Alert>
    );
  }

  const keyValid = choice.saveAs === "newVersion" || !!choice.pendingLayoutVersionId || isLayoutKey(choice.layoutKey.trim());
  return (
    <section className="flex flex-col gap-4 rounded-lg border border-amber-300 bg-amber-50/60 p-4 dark:bg-amber-950/20" aria-labelledby="new-layout-heading" data-testid="new-layout-panel">
      <div className="flex items-start gap-3">
        <Sparkles className="mt-0.5 size-5 shrink-0 text-amber-600" aria-hidden />
        <div className="grid gap-1">
          <h2 id="new-layout-heading" className="text-base font-semibold">
            {t("review.newLayout.title")}
          </h2>
          <p className="text-sm" data-testid="new-layout-message">
            {t("review.newLayout.message", { supplier: supplierName })}
          </p>
        </div>
      </div>

      {pendingId && (
        <div className="flex flex-col gap-2 rounded-md border bg-background p-3" data-testid="pending-layout">
          <span className="flex items-center gap-2 text-sm font-medium">
            <Hourglass className="size-4 text-amber-600" aria-hidden />
            {t("review.newLayout.pending", { layout: pendingKey ?? pendingId })}
          </span>
          <span className="text-xs text-muted-foreground">{t("review.newLayout.pendingHint")}</span>
          {canReview && (
            <div>
              <Button
                type="button"
                size="sm"
                variant={choice.pendingLayoutVersionId ? "secondary" : "default"}
                disabled={match.isPending || !typeKey}
                onClick={() => {
                  onChoiceChange({ ...choice, saveAsLayout: true, pendingLayoutVersionId: pendingId });
                  match.mutate({ typeKey, pendingLayoutVersionId: pendingId });
                }}
                data-testid="confirm-pending-layout"
              >
                {choice.pendingLayoutVersionId ? <Check aria-hidden /> : null}
                {t("review.newLayout.confirmPending")}
              </Button>
            </div>
          )}
        </div>
      )}

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="flex flex-col gap-1">
          <Label htmlFor="new-layout-type">{t("review.newLayout.type")}</Label>
          <Select
            value={typeKey}
            onValueChange={(value) => match.mutate({ typeKey: value, basedOnLayoutId: basedOn })}
            disabled={!canReview || types.isPending || match.isPending}
          >
            <SelectTrigger id="new-layout-type" data-testid="new-layout-type">
              <SelectValue placeholder={t("review.newLayout.pickType")} />
            </SelectTrigger>
            <SelectContent>
              {typeOptions.map((type) => (
                <SelectItem key={type.key} value={type.key}>
                  {localized(type.name, locale) ?? type.key}
                  {type.key === proposedType && <span className="ml-2 text-xs text-muted-foreground">{t("review.newLayout.proposed")}</span>}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          {document.typeSource === "Proposed" && <span className="text-xs text-muted-foreground">{t("review.newLayout.typeProposed")}</span>}
        </div>
        {canReview && typeKey && (
          <div className="flex items-end">
            <Button type="button" onClick={() => match.mutate({ typeKey, basedOnLayoutId: basedOn })} disabled={match.isPending} data-testid="match-layout-submit">
              {match.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Check aria-hidden />}
              {t("review.newLayout.match", { type: typeOptions.find((o) => o.key === typeKey) ? (localized(typeOptions.find((o) => o.key === typeKey)!.name, locale) ?? typeKey) : typeKey })}
            </Button>
          </div>
        )}
      </div>

      {suggestionList.length > 0 && (
        <div className="flex flex-col gap-1" data-testid="layout-suggestions">
          <span className="text-sm font-medium">{t("review.newLayout.suggestions")}</span>
          <ul className="flex flex-wrap gap-2">
            {suggestionList.map((s) => (
              <li key={s.layoutId}>
                <Button
                  type="button"
                  size="xs"
                  variant={basedOn === s.layoutId ? "secondary" : "outline"}
                  disabled={!canReview || match.isPending}
                  onClick={() => match.mutate({ typeKey: s.documentTypeKey, basedOnLayoutId: s.layoutId })}
                  title={t("review.newLayout.suggestionHint")}
                >
                  {t("review.newLayout.similar", { layout: s.layoutKey, percent: Math.round(s.similarity * 100) })}
                </Button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {!choice.pendingLayoutVersionId && (
        <fieldset className="flex flex-col gap-2">
          <legend className="mb-1 text-sm font-medium">{t("review.newLayout.saveAs")}</legend>
          <RadioGroup
            value={choice.saveAs}
            onValueChange={(value) => onChoiceChange({ ...choice, saveAs: value as LayoutSaveChoice["saveAs"], targetLayoutId: value === "newVersion" ? (choice.targetLayoutId ?? sameTypeLayouts[0]?.id ?? null) : null })}
            disabled={!canReview}
          >
            <div className="flex items-center gap-2">
              <RadioGroupItem value="newLayout" id="save-as-new" />
              <Label htmlFor="save-as-new">{t("review.newLayout.saveAsNew")}</Label>
            </div>
            <div className="flex items-center gap-2">
              <RadioGroupItem value="newVersion" id="save-as-version" disabled={sameTypeLayouts.length === 0} />
              <Label htmlFor="save-as-version">{t("review.newLayout.saveAsVersion")}</Label>
            </div>
          </RadioGroup>
          {choice.saveAs === "newLayout" ? (
            <div className="grid gap-2 sm:grid-cols-2">
              <div className="flex flex-col gap-1">
                <Label htmlFor="new-layout-key">{t("review.newLayout.layoutKey")}</Label>
                <Input
                  id="new-layout-key"
                  value={choice.layoutKey}
                  onChange={(event) => onChoiceChange({ ...choice, layoutKey: event.target.value })}
                  aria-invalid={!keyValid}
                  disabled={!canReview}
                  className="font-mono"
                  data-testid="new-layout-key"
                />
                {!keyValid && <span className="text-xs text-destructive">{t("review.newLayout.layoutKeyInvalid")}</span>}
              </div>
              <div className="flex flex-col gap-1">
                <Label htmlFor="new-layout-name">{t("review.newLayout.name")}</Label>
                <Input id="new-layout-name" value={choice.name} onChange={(event) => onChoiceChange({ ...choice, name: event.target.value })} disabled={!canReview} placeholder={choice.layoutKey} />
              </div>
            </div>
          ) : (
            <div className="flex flex-col gap-2">
              <Select value={choice.targetLayoutId ?? ""} onValueChange={(value) => onChoiceChange({ ...choice, targetLayoutId: value })} disabled={!canReview}>
                <SelectTrigger aria-label={t("review.newLayout.target")} className="sm:w-80">
                  <SelectValue placeholder={t("review.newLayout.target")} />
                </SelectTrigger>
                <SelectContent>
                  {sameTypeLayouts.map((l) => (
                    <SelectItem key={l.id} value={l.id}>
                      {l.layoutKey}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <p className="text-xs text-amber-700 dark:text-amber-400">{t("review.newLayout.versionWarning")}</p>
              <div className="flex items-center gap-2">
                <Checkbox id="confirm-not-recognised" checked={choice.confirmNotRecognised} onCheckedChange={(checked) => onChoiceChange({ ...choice, confirmNotRecognised: checked === true })} />
                <Label htmlFor="confirm-not-recognised" className="text-sm font-normal">
                  {t("review.newLayout.confirmNotRecognised")}
                </Label>
              </div>
            </div>
          )}
        </fieldset>
      )}

      <div className="flex flex-col gap-1" data-testid="prefill-sources">
        <span className="text-sm font-medium">{t("review.newLayout.prefilled", { count: sources.length })}</span>
        {sources.length === 0 ? (
          <span className="text-sm text-muted-foreground">{t("review.newLayout.nothingPrefilled")}</span>
        ) : (
          <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
            {sources.map(([path, source]) => (
              <li key={path} className="flex items-center gap-1.5" data-testid="prefill-source" data-path={path} data-source={source}>
                <span>{fieldLabel(path)}</span>
                <Badge variant="outline" className="text-xs">
                  {t(`review.newLayout.source.${source}`, { defaultValue: source })}
                </Badge>
              </li>
            ))}
          </ul>
        )}
        {prefill && !prefill.filledByLlm && <span className="text-xs text-muted-foreground">{t("review.newLayout.llmSkipped")}</span>}
      </div>
    </section>
  );
}
