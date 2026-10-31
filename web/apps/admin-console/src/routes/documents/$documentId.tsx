import { useMemo, useState } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ArrowLeft, Check, RotateCcw, X } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@docproc/ui";
import {
  ApiError,
  ConfidenceBadge,
  DataTable,
  EmptyState,
  FieldStatusBadge,
  PageHeader,
  Policies,
  SourceBadge,
  StatusBadge,
  createDataTableColumnHelper,
  formatDate,
  formatDateTime,
  formatMoney,
  formatNumber,
  flagMessage,
  formatPercent,
  queryKeys,
  unwrap,
  useApi,
  useAuth,
  type DataTableColumns,
} from "@docproc/app-shell";
import { canDecide, canReprocess, canonicalRows, flagSeverityVariant, type CanonicalRow } from "@/lib/documents";
import { formatUsd } from "@/lib/usage";
import { KeyValue } from "@/components/KeyValue";
import { TimelineTable } from "@/components/TimelineTable";
import { PageThumbnails } from "@/components/PageThumbnails";
import { ApproveDialog, RejectDialog, ReprocessDialog } from "@/components/DocumentActions";

type DocumentFlag = components["schemas"]["DocumentFlagDto"];
type DocumentEvent = components["schemas"]["DocumentEventDto"];

export const Route = createFileRoute("/documents/$documentId")({ component: DocumentDetailPage });

const flagHelper = createDataTableColumnHelper<DocumentFlag>();
const canonicalHelper = createDataTableColumnHelper<CanonicalRow>();
const NO_FLAGS: DocumentFlag[] = [];
const NO_EVENTS: DocumentEvent[] = [];

/** The job inspector: read-only summary, flags, costs, the canonical fields, page thumbnails and the event timeline. */
function DocumentDetailPage() {
  const { t, i18n } = useTranslation("admin");
  const { documentId } = Route.useParams();
  const { client } = useApi();
  const { hasPolicy } = useAuth();
  const [action, setAction] = useState<"reprocess" | "reject" | "approve" | null>(null);

  const document = useQuery({
    queryKey: queryKeys.documents.detail(documentId),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}", { params: { path: { id: documentId } } })),
    retry: false,
    refetchInterval: (query) => (query.state.data && ["Received", "Normalized", "Split", "Classified", "Matched", "Extracted"].includes(query.state.data.status) ? 3_000 : false),
  });
  const events = useQuery({
    queryKey: queryKeys.documents.events(documentId),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/events", { params: { path: { id: documentId } } })),
    refetchInterval: document.data && ["Received", "Normalized", "Split", "Classified", "Matched", "Extracted"].includes(document.data.status) ? 3_000 : false,
  });

  // Review is SuperAdmin, Operator, TenantAdmin and Reviewer (Roles.cs), so an Operator sees Approve/Reject too, by design.
  const canReview = hasPolicy(Policies.Review);
  const rows = useMemo(() => canonicalRows(document.data?.canonical, i18n.language), [document.data?.canonical, i18n.language]);

  const flagColumns = useMemo<DataTableColumns<DocumentFlag>>(
    () =>
      flagHelper.columns([
        flagHelper.accessor("severity", { header: t("document.flagSeverity"), cell: (info) => <Badge variant={flagSeverityVariant[info.getValue()]}>{t(`flagSeverity.${info.getValue()}`, { defaultValue: info.getValue() })}</Badge> }),
        flagHelper.accessor("code", { header: t("document.flagCode"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        flagHelper.accessor("field", { header: t("document.flagField"), cell: (info) => <span className="font-mono text-xs">{info.getValue() ?? "—"}</span> }),
        // The message in the UI language from the flag's code and parameters; the API's English text for an unknown
        // code or a flag stored without parameters. Paths stay paths here: they are the field column's vocabulary.
        // Accessed as the displayed sentence, so sorting follows what the user reads, not the hidden English text.
        flagHelper.accessor((flag) => flagMessage(flag, { language: i18n.language, typeLabel: (type) => t(`documentType.${type}`, { defaultValue: type }) }), {
          id: "message",
          header: t("document.flagMessage"),
          cell: (info) => info.getValue(),
        }),
        flagHelper.accessor("createdBy", { header: t("document.flagOrigin"), cell: (info) => t(`flagOrigin.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        flagHelper.accessor("resolvedAt", { header: t("document.flagResolved"), sortFn: "datetime", cell: (info) => (info.getValue() ? `${formatDateTime(info.getValue(), i18n.language)}${info.row.original.resolvedBy ? ` · ${info.row.original.resolvedBy}` : ""}` : t("document.flagOpen")) }),
      ]),
    [t, i18n.language],
  );

  const canonicalColumns = useMemo<DataTableColumns<CanonicalRow>>(
    () =>
      canonicalHelper.columns([
        canonicalHelper.accessor("path", { header: t("document.field"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        canonicalHelper.accessor("value", { header: t("document.value"), cell: (info) => (info.row.original.count !== null ? t("document.items", { count: info.row.original.count }) : info.getValue()) }),
        canonicalHelper.display({ id: "status", header: t("document.fieldStatus"), cell: (info) => (info.row.original.field ? <FieldStatusBadge status={info.row.original.field.status} /> : "—") }),
        canonicalHelper.display({ id: "confidence", header: t("document.confidence"), cell: (info) => (info.row.original.field ? <ConfidenceBadge confidence={info.row.original.field.confidence} /> : "—") }),
        canonicalHelper.display({ id: "source", header: t("document.source"), cell: (info) => (info.row.original.field?.source ? <SourceBadge source={info.row.original.field.source} /> : "—") }),
      ]),
    [t],
  );

  if (document.isError && document.error instanceof ApiError && document.error.status === 404) {
    return (
      <EmptyState
        title={t("document.notFound")}
        description={documentId}
        action={
          <Button variant="outline" asChild>
            <Link to="/documents">{t("document.backToList")}</Link>
          </Button>
        }
      />
    );
  }

  const doc = document.data;
  const label = doc?.documentNumber ?? documentId.slice(0, 8);

  return (
    <>
      <PageHeader
        title={
          doc ? (
            <span className="flex flex-wrap items-center gap-2">
              {doc.documentNumber ?? t("document.untitled")}
              <StatusBadge status={doc.status} />
            </span>
          ) : (
            <Skeleton className="h-8 w-56" />
          )
        }
        description={doc ? `${t(`documentType.${doc.type}`, { defaultValue: doc.type })}${doc.supplierName ? ` · ${doc.supplierName}` : ""} · ${doc.id}` : undefined}
        actions={
          <>
            <Button variant="ghost" size="sm" asChild>
              <Link to="/documents">
                <ArrowLeft aria-hidden />
                {t("document.backToList")}
              </Link>
            </Button>
            {doc && canReview && (
              <>
                <Button variant="outline" size="sm" disabled={!canReprocess(doc.status)} onClick={() => setAction("reprocess")}>
                  <RotateCcw aria-hidden />
                  {t("document.reprocess")}
                </Button>
                <Button variant="destructive" size="sm" disabled={!canDecide(doc.status)} onClick={() => setAction("reject")}>
                  <X aria-hidden />
                  {t("document.reject")}
                </Button>
                <Button size="sm" disabled={!canDecide(doc.status)} onClick={() => setAction("approve")}>
                  <Check aria-hidden />
                  {t("document.approve")}
                </Button>
              </>
            )}
          </>
        }
      />

      {doc?.failureReason && (
        <div role="alert" className="rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm">
          <span className="font-medium">{t("document.failureReason")}:</span> {doc.failureReason}
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader>
            <CardTitle>{t("document.summary")}</CardTitle>
          </CardHeader>
          <CardContent>
            {doc ? (
              <KeyValue
                columns={3}
                items={[
                  { label: t("documents.supplier"), value: doc.supplierName ?? "—" },
                  { label: t("documents.issueDate"), value: formatDate(doc.issueDate, i18n.language) },
                  { label: t("documents.total"), value: formatMoney(doc.totalGross, doc.currency, i18n.language) },
                  { label: t("documents.type"), value: `${t(`documentType.${doc.type}`, { defaultValue: doc.type })}${doc.typeConfidence != null ? ` (${formatPercent(doc.typeConfidence, i18n.language)})` : ""}` },
                  { label: t("documents.method"), value: doc.extractionMethod ? t(`common:sourceMethod.${doc.extractionMethod}`, { defaultValue: doc.extractionMethod }) : "—" },
                  { label: t("documents.pages"), value: String(doc.pageCount) },
                  { label: t("document.flagsCount"), value: t("document.flagsSummary", { blocking: Number(doc.blockingFlagCount), warning: Number(doc.warningFlagCount) }) },
                  { label: t("documents.receivedAt"), value: formatDateTime(doc.createdAt, i18n.language) },
                  { label: t("documents.updatedAt"), value: formatDateTime(doc.updatedAt, i18n.language) },
                  { label: t("document.approvedBy"), value: doc.approvedBy ? `${doc.approvedBy} · ${formatDateTime(doc.approvedAt, i18n.language)}` : "—" },
                  { label: t("document.ingestion"), value: doc.ingestion ? `${doc.ingestion.fileName}${doc.ingestion.sourceFrom ? ` · ${doc.ingestion.sourceFrom}` : ""}` : "—" },
                  { label: t("document.schemaVersion"), value: doc.canonicalSchemaVersion != null ? String(doc.canonicalSchemaVersion) : "—", mono: true },
                ]}
              />
            ) : (
              <Skeleton className="h-32 w-full" />
            )}
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>{t("document.costs")}</CardTitle>
            <CardDescription>{t("document.costsDescription")}</CardDescription>
          </CardHeader>
          <CardContent>
            {doc ? (
              <KeyValue
                columns={1}
                items={[
                  { label: t("document.estimatedCost"), value: doc.costs.estimatedUsd == null ? "—" : formatUsd(doc.costs.estimatedUsd, i18n.language) },
                  { label: t("document.llmModel"), value: doc.costs.llmModel ? `${doc.costs.llmProvider ?? ""} ${doc.costs.llmModel}`.trim() : "—" },
                  { label: t("document.keyOwner"), value: doc.costs.keyOwner ? t(`keyOwner.${doc.costs.keyOwner}`, { defaultValue: doc.costs.keyOwner }) : "—" },
                  { label: t("document.tokens"), value: t("document.tokensSummary", { input: formatNumber(doc.costs.llmInputTokens, i18n.language), output: formatNumber(doc.costs.llmOutputTokens, i18n.language), cache: formatNumber(doc.costs.llmCacheReadTokens, i18n.language) }) },
                  { label: t("document.ocrPages"), value: String(doc.costs.ocrPages) },
                ]}
              />
            ) : (
              <Skeleton className="h-32 w-full" />
            )}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t("document.pages")}</CardTitle>
        </CardHeader>
        <CardContent>{doc ? <PageThumbnails documentId={doc.id} pageCount={Number(doc.pageCount)} /> : <Skeleton className="h-40 w-full" />}</CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("document.flags")}</CardTitle>
        </CardHeader>
        <CardContent>
          <DataTable columns={flagColumns} data={doc?.flags ?? NO_FLAGS} getRowId={(row) => row.id} isLoading={document.isPending} query={document} caption={t("document.flags")} emptyTitle={t("document.noFlags")} emptyDescription={t("document.noFlagsDescription")} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("document.canonical")}</CardTitle>
          <CardDescription>{t("document.canonicalDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          <DataTable columns={canonicalColumns} data={rows} getRowId={(row) => row.path} isLoading={document.isPending} query={document} caption={t("document.canonical")} emptyTitle={t("document.noCanonical")} emptyDescription={t("document.noCanonicalDescription")} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("timeline.title")}</CardTitle>
          <CardDescription>{t("timeline.description")}</CardDescription>
        </CardHeader>
        <CardContent>
          <TimelineTable events={events.data ?? NO_EVENTS} isLoading={events.isPending} query={events} />
        </CardContent>
      </Card>

      {doc && (
        <>
          <ReprocessDialog documentId={doc.id} documentLabel={label} status={doc.status} events={events.data} open={action === "reprocess"} onOpenChange={(open) => !open && setAction(null)} />
          <RejectDialog documentId={doc.id} documentLabel={label} open={action === "reject"} onOpenChange={(open) => !open && setAction(null)} />
          <ApproveDialog documentId={doc.id} documentLabel={label} open={action === "approve"} onOpenChange={(open) => !open && setAction(null)} />
        </>
      )}
    </>
  );
}
