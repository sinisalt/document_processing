import { useTranslation } from "react-i18next";
import { AlertTriangle, CheckCircle2, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@docproc/ui";
import { formatBytes } from "@docproc/app-shell";
import { jsonPreview, type PreviewResponse } from "./api";
import { outputLists } from "./fieldMap";

/**
 * The preview pane of the mapping editor: the output the server rendered over the chosen document (docs/11 §13.1:
 * "Preview is always rendered by the server, so preview and delivery cannot drift"), the size of its lists (412 prices,
 * the tier prices as scales), the render error with its position, and the target-schema errors by path.
 */
export function PreviewPane({ result, isFetching, failure, documentChosen }: { result: PreviewResponse | undefined; isFetching: boolean; failure: string | null; documentChosen: boolean }) {
  const { t, i18n } = useTranslation();
  if (!documentChosen) return <p className="text-sm text-muted-foreground">{t("integrations.preview.chooseDocument")}</p>;
  if (failure) {
    return (
      <Alert variant="destructive">
        <AlertTitle>{t("integrations.preview.failed")}</AlertTitle>
        <AlertDescription>{failure}</AlertDescription>
      </Alert>
    );
  }
  if (!result) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin" aria-hidden />
        {t("integrations.preview.rendering")}
      </p>
    );
  }
  const lists = outputLists(result.output);
  const ok = result.errors.length === 0 && result.schemaErrors.length === 0;
  const preview = result.output !== null && result.output !== undefined ? jsonPreview(result.output) : null;
  return (
    <div className="flex flex-col gap-3" data-testid="preview-pane" aria-busy={isFetching}>
      <div className="flex flex-wrap items-center gap-2 text-xs" data-testid="preview-summary">
        {ok ? (
          <Badge variant="success" data-testid="preview-ok">
            <CheckCircle2 aria-hidden />
            {result.undefined ? t("integrations.preview.undefined") : t("integrations.preview.ok")}
          </Badge>
        ) : (
          <Badge variant="destructive" data-testid="preview-error">
            <AlertTriangle aria-hidden />
            {result.errors.length > 0 ? t("integrations.preview.renderError") : t("integrations.preview.schemaErrors", { count: result.schemaErrors.length })}
          </Badge>
        )}
        <span className="text-muted-foreground">{formatBytes(Number(result.outputBytes), i18n.language)}</span>
        <span className="text-muted-foreground">{t("integrations.preview.elapsed", { ms: Number(result.elapsedMs) })}</span>
        {isFetching && <Loader2 className="size-3 animate-spin" aria-label={t("integrations.preview.rendering")} />}
      </div>
      {lists.length > 0 && (
        <ul className="flex flex-wrap gap-1.5" data-testid="preview-lists">
          {lists.map((list) => (
            <li key={list.path}>
              <Badge variant="outline" className="font-mono text-[11px] font-normal" data-list={list.path} data-items={list.items}>
                {list.inRows !== undefined ? t("integrations.preview.nestedList", { path: list.path, count: list.items, rows: list.inRows }) : t("integrations.preview.list", { path: list.path, count: list.items })}
              </Badge>
            </li>
          ))}
        </ul>
      )}
      {result.errors.map((error, i) => (
        <Alert key={i} variant="destructive" data-testid="preview-render-error">
          <AlertTitle>{t(`integrations.preview.errorKinds.${error.kind}`, { defaultValue: error.kind })}</AlertTitle>
          <AlertDescription>
            {error.message}
            {error.position !== null && error.position !== undefined ? ` (${t("integrations.preview.position", { position: Number(error.position) })})` : ""}
          </AlertDescription>
        </Alert>
      ))}
      {result.schemaErrors.length > 0 && (
        <Table data-testid="preview-schema-errors">
          <TableHeader>
            <TableRow>
              <TableHead>{t("integrations.preview.columns.path")}</TableHead>
              <TableHead>{t("integrations.preview.columns.keyword")}</TableHead>
              <TableHead>{t("integrations.preview.columns.message")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {result.schemaErrors.map((error, i) => (
              <TableRow key={i}>
                <TableCell className="font-mono text-xs">{error.path || "/"}</TableCell>
                <TableCell className="font-mono text-xs">{error.keyword}</TableCell>
                <TableCell className="text-xs">{error.message}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {preview && (
        <>
          <pre className="max-h-[60vh] overflow-auto rounded-md bg-muted p-3 font-mono text-[11px] leading-snug" data-testid="preview-output">
            {preview.text}
          </pre>
          {preview.cut && <p className="text-xs text-muted-foreground">{t("integrations.preview.cut")}</p>}
        </>
      )}
    </div>
  );
}
