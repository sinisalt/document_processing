import { useMemo, useState } from "react";
import { useMutation, useQueryClient, type QueryKey } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import type { components } from "@docproc/api-client";
import { Badge, Button } from "@docproc/ui";
import { ConfirmDialog, DataTable, createDataTableColumnHelper, formatDateTime, unwrap, useApi, type DataTableColumns, type DataTableQueryState } from "@docproc/app-shell";
import { formatUsd } from "@/lib/usage";

export type LlmProvider = components["schemas"]["LlmProviderResponse"];

/** Which `:enable` / `:disable` routes the table calls: the platform's (`/llm/providers`) or the tenant's (`/llm-providers`). */
export type ProviderScope = "platform" | "tenant";

const helper = createDataTableColumnHelper<LlmProvider>();
const EMPTY: LlmProvider[] = [];

export interface ProviderTableProps {
  providers: LlmProvider[] | undefined;
  isLoading: boolean;
  /** The providers query: when it failed the table says so (with Retry) instead of "No providers". */
  query?: DataTableQueryState;
  scope: ProviderScope;
  /** Invalidated after a switch. */
  queryKey: QueryKey;
  /** Hides the kill switch for readers who may not flip it. */
  canManage: boolean;
  toolbar?: React.ReactNode;
}

/** Provider configurations without credentials (only `keyLast4`), with the per-provider kill switch of docs/08. */
export function ProviderTable({ providers, isLoading, query, scope, queryKey, canManage, toolbar }: ProviderTableProps) {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [pending, setPending] = useState<LlmProvider | null>(null);

  const toggle = useMutation({
    mutationFn: async (provider: LlmProvider) => {
      const enable = provider.status !== "Active";
      const path = { params: { path: { id: provider.id } } };
      if (scope === "platform") {
        return enable ? unwrap(await client.POST("/api/v1/llm/providers/{id}:enable", path)) : unwrap(await client.POST("/api/v1/llm/providers/{id}:disable", path));
      }
      return enable ? unwrap(await client.POST("/api/v1/llm-providers/{id}:enable", path)) : unwrap(await client.POST("/api/v1/llm-providers/{id}:disable", path));
    },
    onSuccess: async (updated) => {
      toast.success(updated.status === "Active" ? t("llm.providerEnabled", { kind: updated.kind }) : t("llm.providerDisabled", { kind: updated.kind }));
      await queryClient.invalidateQueries({ queryKey });
    },
  });

  const columns = useMemo<DataTableColumns<LlmProvider>>(
    () =>
      helper.columns([
        helper.accessor("kind", { header: t("llm.kind"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("status", {
          header: t("llm.status"),
          cell: (info) => (
            <Badge variant={info.getValue() === "Active" ? "default" : "destructive"} data-status={info.getValue()}>
              {t(`llm.providerStatus.${info.getValue()}`, { defaultValue: info.getValue() })}
            </Badge>
          ),
        }),
        helper.accessor("dataResidency", { header: t("llm.dataResidency"), cell: (info) => t(`llm.residency.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("models", {
          header: t("llm.models"),
          enableSorting: false,
          cell: (info) => (
            <ul className="flex flex-wrap gap-1">
              {Object.entries(info.getValue()).map(([stage, model]) => (
                <li key={stage}>
                  <Badge variant="outline" className="font-mono text-[11px]">
                    {t(`stages.${stage}`, { defaultValue: stage })}: {model}
                  </Badge>
                </li>
              ))}
            </ul>
          ),
        }),
        helper.accessor("endpoint", { header: t("llm.endpoint"), cell: (info) => (info.getValue() ? <span className="font-mono text-xs">{info.getValue()}</span> : "—") }),
        helper.accessor("keyLast4", { header: t("llm.key"), cell: (info) => (info.getValue() ? <span className="font-mono text-xs">{`••••${info.getValue()}`}</span> : "—") }),
        helper.accessor("monthlySpendCapUsd", { header: t("llm.spendCap"), cell: (info) => <span className="tabular-nums">{info.getValue() == null ? "—" : formatUsd(info.getValue(), i18n.language)}</span> }),
        helper.accessor("lastError", { header: t("llm.lastError"), cell: (info) => {
            // Only an actual error is red; "no error" is the muted dash every other empty cell shows.
            const error = info.getValue();
            return error ? (
              <span className="block max-w-xs truncate text-destructive" title={error}>
                {error}
              </span>
            ) : (
              <span className="text-muted-foreground">—</span>
            );
          },
        }),
        helper.accessor("updatedAt", { header: t("llm.updatedAt"), sortFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDateTime(info.getValue(), i18n.language)}</span> }),
        helper.display({
          id: "actions",
          header: () => <span className="sr-only">{t("llm.actions")}</span>,
          cell: (info) =>
            canManage ? (
              <Button
                variant={info.row.original.status === "Active" ? "outline" : "default"}
                size="sm"
                onClick={() => setPending(info.row.original)}
                aria-label={info.row.original.status === "Active" ? t("llm.disableProviderAria", { kind: info.row.original.kind }) : t("llm.enableProviderAria", { kind: info.row.original.kind })}
              >
                {info.row.original.status === "Active" ? t("llm.disable") : t("llm.enable")}
              </Button>
            ) : null,
        }),
      ]),
    [t, i18n.language, canManage],
  );

  return (
    <>
      <DataTable
        columns={columns}
        data={providers ?? EMPTY}
        getRowId={(row) => row.id}
        isLoading={isLoading}
        query={query}
        toolbar={toolbar}
        caption={t("llm.providersCaption")}
        emptyTitle={t("llm.noProviders")}
        emptyDescription={t("llm.noProvidersDescription")}
        initialColumnVisibility={{ endpoint: false, updatedAt: false }}
        enableColumnVisibility
      />
      {pending && (
        <ConfirmDialog
          open
          onOpenChange={(open) => !open && setPending(null)}
          destructive={pending.status === "Active"}
          title={pending.status === "Active" ? t("llm.disableTitle", { kind: pending.kind }) : t("llm.enableTitle", { kind: pending.kind })}
          description={pending.status === "Active" ? t("llm.disableDescription") : t("llm.enableDescription")}
          confirmLabel={pending.status === "Active" ? t("llm.disable") : t("llm.enable")}
          isPending={toggle.isPending}
          onConfirm={async () => { await toggle.mutateAsync(pending); }}
        />
      )}
    </>
  );
}
