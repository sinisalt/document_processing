import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Plus, Save, Trash2 } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Alert, AlertDescription, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Input, Progress, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@docproc/ui";
import { ApiError, formatMoney, queryKeys, unwrap, useApi } from "@docproc/app-shell";

type Budget = components["schemas"]["LlmBudgetResponse"];

export const budgetPeriods = ["day", "month", "document"] as const;
export const budgetStages = ["Classify", "Split", "Extract", "Synthesize"] as const;
const ALL = "__all__";

export interface BudgetDraft {
  period: (typeof budgetPeriods)[number];
  stage: string | null;
  limit: string;
}

/** The stored budgets as rows of the editor. */
export function budgetDrafts(budgets: readonly Budget[]): BudgetDraft[] {
  return budgets.map((b) => ({ period: (budgetPeriods as readonly string[]).includes(b.period) ? (b.period as BudgetDraft["period"]) : "month", stage: b.stage ?? null, limit: String(Number(b.limitUsd)) }));
}

/** The body of `PUT /llm-budgets`, or the index of the first row whose limit does not read and why. */
export function budgetsBody(drafts: readonly BudgetDraft[]): { budgets: { period: string; stage: string | null; limitUsd: number }[] } | { invalid: number; reason: "limit" | "duplicate" } {
  const seen = new Set<string>();
  const budgets: { period: string; stage: string | null; limitUsd: number }[] = [];
  for (let i = 0; i < drafts.length; i++) {
    const draft = drafts[i]!;
    const limit = Number(draft.limit.replace(",", "."));
    if (!draft.limit.trim() || !Number.isFinite(limit) || limit < 0) return { invalid: i, reason: "limit" };
    const key = `${draft.period}:${draft.stage ?? ""}`;
    if (seen.has(key)) return { invalid: i, reason: "duplicate" };
    seen.add(key);
    budgets.push({ period: draft.period, stage: draft.stage, limitUsd: Math.round(limit * 10_000) / 10_000 });
  }
  return { budgets };
}

/** How much of a budget its current period used, 0 to 100 (a document budget has no period total). */
export function budgetUse(budget: Pick<Budget, "spentUsd" | "limitUsd">): number | null {
  if (budget.spentUsd === null || budget.spentUsd === undefined) return null;
  const limit = Number(budget.limitUsd);
  if (limit <= 0) return 100;
  return Math.min(100, Math.round((Number(budget.spentUsd) / limit) * 100));
}

/**
 * The tenant's LLM budgets (docs/11 §16, §19, P5): per UTC day, UTC month or document, for every stage or one, with what
 * the current period spent. A call that would pass a budget is not made; the stage goes on without the LLM and the
 * document is held with `LLM_BUDGET_EXCEEDED`. `GET`/`PUT /llm-budgets` (tenant admins).
 */
export function LlmBudgetsCard() {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const budgets = useQuery({ queryKey: queryKeys.llm.budgets(), queryFn: async () => unwrap(await client.GET("/api/v1/llm-budgets")) });
  const [drafts, setDrafts] = useState<BudgetDraft[]>([]);
  const [problem, setProblem] = useState<string | null>(null);
  useEffect(() => {
    if (budgets.data) setDrafts(budgetDrafts(budgets.data.budgets));
  }, [budgets.data]);
  const set = (index: number, patch: Partial<BudgetDraft>) => setDrafts((current) => current.map((d, i) => (i === index ? { ...d, ...patch } : d)));
  const save = useMutation({
    mutationFn: async () => {
      const body = budgetsBody(drafts);
      if ("invalid" in body) throw new Error(t(`settings.budgets.problems.${body.reason}`, { row: body.invalid + 1 }));
      return unwrap(await client.PUT("/api/v1/llm-budgets", { body: body as never }));
    },
    meta: { silent: true },
    onSuccess: async () => {
      setProblem(null);
      toast.success(t("settings.budgets.saved"));
      await queryClient.invalidateQueries({ queryKey: queryKeys.llm.budgets() });
    },
    onError: (error) => setProblem(error instanceof ApiError ? (error.isValidation ? Object.values(error.fieldErrors).flat().join(" ") : (error.problem?.detail ?? error.title)) : error.message),
  });
  const stored = budgets.data?.budgets ?? [];
  return (
    <Card data-testid="llm-budgets">
      <CardHeader>
        <CardTitle className="text-base">{t("settings.budgets.title")}</CardTitle>
        <CardDescription>{t("settings.budgets.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {drafts.length === 0 && <p className="text-sm text-muted-foreground">{t("settings.budgets.none")}</p>}
        {drafts.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("settings.budgets.period")}</TableHead>
                <TableHead>{t("settings.budgets.stage")}</TableHead>
                <TableHead>{t("settings.budgets.limit")}</TableHead>
                <TableHead>{t("settings.budgets.spent")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {drafts.map((draft, index) => {
                const match = stored.find((b) => b.period === draft.period && (b.stage ?? null) === draft.stage);
                const use = match ? budgetUse(match) : null;
                return (
                  <TableRow key={index} data-budget={`${draft.period}:${draft.stage ?? "all"}`}>
                    <TableCell>
                      <Select value={draft.period} onValueChange={(value) => set(index, { period: value as BudgetDraft["period"] })}>
                        <SelectTrigger className="w-32" aria-label={t("settings.budgets.period")}>
                          <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                          {budgetPeriods.map((p) => (
                            <SelectItem key={p} value={p}>
                              {t(`settings.budgets.periods.${p}`)}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    </TableCell>
                    <TableCell>
                      <Select value={draft.stage ?? ALL} onValueChange={(value) => set(index, { stage: value === ALL ? null : value })}>
                        <SelectTrigger className="w-36" aria-label={t("settings.budgets.stage")}>
                          <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                          <SelectItem value={ALL}>{t("settings.budgets.allStages")}</SelectItem>
                          {budgetStages.map((s) => (
                            <SelectItem key={s} value={s}>
                              {t(`settings.budgets.stages.${s}`)}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    </TableCell>
                    <TableCell>
                      <Input className="w-28" inputMode="decimal" value={draft.limit} onChange={(e) => set(index, { limit: e.target.value })} aria-label={t("settings.budgets.limit")} data-testid={`budget-limit-${index}`} />
                    </TableCell>
                    <TableCell className="min-w-40">
                      {match && match.spentUsd !== null ? (
                        <div className="grid gap-1">
                          <span className="text-xs tabular-nums">{t("settings.budgets.spentOf", { spent: formatMoney(Number(match.spentUsd), "USD", i18n.language), limit: formatMoney(Number(match.limitUsd), "USD", i18n.language) })}</span>
                          <Progress value={use ?? 0} aria-label={t("settings.budgets.spent")} />
                        </div>
                      ) : (
                        <span className="text-xs text-muted-foreground">{draft.period === "document" ? t("settings.budgets.perDocument") : "—"}</span>
                      )}
                    </TableCell>
                    <TableCell>
                      <Button type="button" size="icon-xs" variant="ghost" aria-label={t("settings.budgets.remove")} onClick={() => setDrafts((current) => current.filter((_, i) => i !== index))}>
                        <Trash2 aria-hidden />
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
        {problem && (
          <Alert variant="destructive" data-testid="budgets-problem">
            <AlertDescription>{problem}</AlertDescription>
          </Alert>
        )}
        <div className="flex gap-2">
          <Button type="button" size="sm" variant="outline" onClick={() => setDrafts((current) => [...current, { period: "month", stage: null, limit: "" }])} data-testid="budget-add">
            <Plus aria-hidden />
            {t("settings.budgets.add")}
          </Button>
          <Button type="button" size="sm" onClick={() => save.mutate()} disabled={save.isPending || JSON.stringify(drafts) === JSON.stringify(budgetDrafts(stored))} data-testid="budgets-save">
            <Save aria-hidden />
            {t("settings.budgets.save")}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
