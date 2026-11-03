import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft, Code2, Eye, GitBranchPlus, Loader2, Save, Send, Wand2 } from "lucide-react";
import {
  Alert,
  AlertDescription,
  AlertTitle,
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Skeleton,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
} from "@docproc/ui";
import { ConfirmDialog, isApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { useDocumentTypes } from "@docproc/review";
import { useLookupTables, useOutputMapping, useOutputMappingVersion, type OutputMapping, type PreviewResponse, type RenderError, type VersionWithRevision } from "./api";
import { compileFieldMap, fieldMapOf, fieldMapProblems, listChainOf, relativeSource, rowAt, updateRowAt, type FieldMap } from "./fieldMap";
import { FieldMapEditor } from "./FieldMapEditor";
import { PreviewPane } from "./PreviewPane";
import { SourceTree, type SourcePick } from "./SourceTree";

const ANY_TYPE = "__any";

/** A value that follows another one after it has stopped changing for `ms`. */
export function useDebounced<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), ms);
    return () => clearTimeout(timer);
  }, [value, ms]);
  return debounced;
}

/** The target schema typed in the Schema tab: empty is none, otherwise it must be a JSON object. */
export function parseSchema(text: string): { schema: unknown | null; invalid: boolean } {
  if (text.trim() === "") return { schema: null, invalid: false };
  try {
    const parsed: unknown = JSON.parse(text);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? { schema: parsed, invalid: false } : { schema: null, invalid: true };
  } catch {
    return { schema: null, invalid: true };
  }
}

/**
 * An output mapping opened in the Integrations tab (docs/11 §19): its versions, and the version shown in three panes,
 * the values view of a chosen document on the left, the visual field map or the JSONata code in the middle, and the
 * output the server renders over the document on the right, with its errors. A draft is saved with `If-Match` and
 * published (`:publish`, 422 with the renderer's errors); a published version gets a new draft.
 */
export function MappingEditor({ mappingKey, canEdit, onBack }: { mappingKey: string; canEdit: boolean; onBack: () => void }) {
  const { t } = useTranslation();
  const mapping = useOutputMapping(mappingKey);
  const [chosen, setChosen] = useState<number | null>(null);
  const versions = mapping.data?.versions ?? [];
  const current = chosen ?? (mapping.data ? Number(mapping.data.draftVersion ?? mapping.data.publishedVersion ?? versions[versions.length - 1]?.version ?? 1) : null);
  const version = useOutputMappingVersion(mappingKey, current);

  if (mapping.isPending || (current !== null && version.isPending)) {
    return (
      <div className="grid gap-3" aria-busy>
        <Skeleton className="h-10 w-1/3" />
        <Skeleton className="h-96" />
      </div>
    );
  }
  if (!mapping.data || !version.data) {
    return (
      <Alert variant="destructive">
        <AlertTitle>{t("integrations.mappings.notFound", { key: mappingKey })}</AlertTitle>
        <AlertDescription>
          <Button variant="link" className="px-0" onClick={onBack}>
            {t("integrations.back")}
          </Button>
        </AlertDescription>
      </Alert>
    );
  }
  return <VersionEditor key={`${version.data.detail.version.id}:${version.data.etag ?? ""}`} mapping={mapping.data} version={version.data} canEdit={canEdit} onBack={onBack} onVersionChange={setChosen} />;
}

interface VersionEditorProps {
  mapping: OutputMapping;
  version: VersionWithRevision;
  canEdit: boolean;
  onBack: () => void;
  onVersionChange: (version: number) => void;
}

function VersionEditor({ mapping, version, canEdit, onBack, onVersionChange }: VersionEditorProps) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const detail = version.detail;
  const versionNumber = Number(detail.version.version);
  const editable = canEdit && detail.version.state === "Draft" && mapping.origin !== "Platform";
  const savedSchema = detail.targetSchema ? JSON.stringify(detail.targetSchema, null, 2) : "";

  const [expression, setExpression] = useState(detail.expression);
  const [schemaText, setSchemaText] = useState(savedSchema);
  const [tab, setTab] = useState<"visual" | "code" | "schema">(() => (fieldMapOf(detail.expression).inSync ? "visual" : "code"));
  const [activeRow, setActiveRow] = useState<string | null>(null);
  const [documentId, setDocumentId] = useState<string | null>(detail.sampleDocumentIds[0] ?? null);
  const [publishErrors, setPublishErrors] = useState<RenderError[] | null>(null);
  const [changeNote, setChangeNote] = useState("");
  const [startVisual, setStartVisual] = useState(false);

  const parsed = useMemo(() => fieldMapOf(expression), [expression]);
  const problems = useMemo(() => (parsed.map ? fieldMapProblems(parsed.map) : []), [parsed.map]);
  const schema = parseSchema(schemaText);
  const dirty = expression !== detail.expression || schemaText !== savedSchema;

  const types = useDocumentTypes();
  const lookupTables = useLookupTables();
  const documents = useQuery({
    queryKey: queryKeys.documents.list({ type: mapping.sourceTypeKey ?? undefined, limit: 50 }),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents", { params: { query: { type: mapping.sourceTypeKey ?? undefined, limit: 50 } } })),
  });
  useEffect(() => {
    if (!documentId && documents.data?.items[0]) setDocumentId(documents.data.items[0].id);
  }, [documentId, documents.data]);

  // The values view of the document, for the source tree (an empty object rendered with includeValues).
  const values = useQuery({
    queryKey: ["outputs", "values", documentId],
    enabled: documentId !== null,
    queryFn: async () => unwrap(await client.POST("/api/v1/output-mappings:preview", { body: { expression: "{}", engine: "jsonata", documentId, includeValues: true, mappingVersionId: null, sampleValues: null, targetSchema: null, event: null } })).values,
    meta: { silent: true },
    staleTime: 60_000,
  });

  const debouncedExpression = useDebounced(expression, 600);
  const debouncedSchema = useDebounced(schema.invalid ? null : schema.schema, 600);
  const preview = useQuery({
    queryKey: ["outputs", "preview", mapping.key, documentId, debouncedExpression, JSON.stringify(debouncedSchema)],
    enabled: documentId !== null && debouncedExpression.trim() !== "",
    queryFn: async (): Promise<PreviewResponse> =>
      unwrap(
        await client.POST("/api/v1/output-mappings:preview", {
          body: { expression: debouncedExpression, engine: "jsonata", targetSchema: (debouncedSchema ?? null) as never, documentId, includeValues: false, mappingVersionId: null, sampleValues: null, event: null },
        }),
      ),
    placeholderData: keepPreviousData,
    meta: { silent: true },
    retry: false,
    staleTime: Infinity,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: queryKeys.outputs.mapping(mapping.key) });
    await queryClient.invalidateQueries({ queryKey: queryKeys.outputs.mappings() });
  };

  const save = useMutation({
    mutationFn: async () =>
      unwrap(
        await client.PUT("/api/v1/output-mappings/{key}/versions/{version}", {
          params: { path: { key: mapping.key, version: versionNumber } },
          headers: version.etag ? { "If-Match": version.etag } : {},
          body: { engine: "jsonata", expression, contentType: detail.version.contentType, fileNameTemplate: detail.fileNameTemplate, targetSchema: (schema.schema ?? null) as never, sampleDocumentIds: documentId ? [documentId] : [] },
        }),
      ),
    meta: { silent: true },
    onSuccess: async () => {
      toast.success(t("integrations.editor.saved", { version: versionNumber }));
      await refresh();
    },
    onError: (error) => toast.error(isApiError(error) ? error.title : t("integrations.failed"), { description: isApiError(error) ? (Object.values(error.fieldErrors)[0]?.join(" ") ?? error.problem?.detail ?? undefined) : undefined }),
  });

  const publish = useMutation({
    mutationFn: async () => {
      const result = await client.POST("/api/v1/output-mappings/{key}/versions/{version}:publish", { params: { path: { key: mapping.key, version: versionNumber } }, body: { changeNote: changeNote.trim() || null } as never });
      // 422 carries the renderer's report: the expression does not parse, or the target schema is not one.
      if (result.response.status === 422) return { published: false as const, errors: (result.error as unknown as { errors: RenderError[] }).errors ?? [] };
      unwrap(result);
      return { published: true as const, errors: [] };
    },
    meta: { silent: true },
    onSuccess: async (outcome) => {
      if (!outcome.published) {
        setPublishErrors(outcome.errors);
        toast.error(t("integrations.editor.publishBlocked"));
        return;
      }
      setPublishErrors(null);
      toast.success(t("integrations.editor.published", { version: versionNumber }));
      await refresh();
    },
    onError: (error) => toast.error(isApiError(error) ? error.title : t("integrations.failed")),
  });

  const newVersion = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/output-mappings/{key}/versions", { params: { path: { key: mapping.key } } })),
    onSuccess: async (created) => {
      await refresh();
      onVersionChange(Number(created.version));
    },
  });

  const setSourceType = useMutation({
    mutationFn: async (key: string) => unwrap(await client.PATCH("/api/v1/output-mappings/{key}", { params: { path: { key: mapping.key } }, body: { sourceTypeKey: key, name: null, description: null, status: null } })),
    onSuccess: refresh,
  });

  const changeMap = (map: FieldMap) => setExpression(compileFieldMap(map));

  /** A field picked in the source tree goes into the row being edited, relative to the lists the row lies in. */
  const pick = (picked: SourcePick) => {
    if (!editable || !parsed.inSync || !parsed.map || activeRow === null) return;
    const row = rowAt(parsed.map.rows, activeRow);
    if (!row || (row.kind !== "value" && row.kind !== "list")) return;
    const source = relativeSource(picked.path, listChainOf(parsed.map.rows, activeRow));
    changeMap({ ...parsed.map, rows: updateRowAt(parsed.map.rows, activeRow, (r) => (r.kind === "value" || r.kind === "list" ? { ...r, source, target: r.target || source.split(".").pop()!.replace(/^\$root$/, "") } : r)) });
  };

  const previewFailure = preview.error ? (isApiError(preview.error) ? (preview.error.problem?.detail ?? preview.error.title) : String(preview.error)) : null;
  const lookupKeys = (lookupTables.data ?? []).map((table) => table.key);

  return (
    <div className="flex flex-col gap-4" data-testid="mapping-editor" data-mapping={mapping.key}>
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="ghost" size="sm" onClick={onBack}>
          <ArrowLeft aria-hidden />
          {t("integrations.back")}
        </Button>
        <h2 className="text-lg font-semibold">{mapping.name}</h2>
        <Badge variant="outline" className="font-mono">
          {mapping.key}
        </Badge>
        <Badge variant="secondary">{t(`integrations.mappings.origins.${mapping.origin}`, { defaultValue: mapping.origin })}</Badge>
        <Select value={String(versionNumber)} onValueChange={(v) => onVersionChange(Number(v))}>
          <SelectTrigger className="h-8 w-48" aria-label={t("integrations.editor.version")} data-testid="mapping-version">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {mapping.versions.map((v) => (
              <SelectItem key={String(v.version)} value={String(v.version)}>
                {t("integrations.editor.versionOption", { version: Number(v.version), state: t(`integrations.versionStates.${v.state}`, { defaultValue: v.state }) })}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          {canEdit && mapping.origin !== "Platform" && detail.version.state !== "Draft" && mapping.draftVersion === null && (
            <Button variant="outline" size="sm" onClick={() => newVersion.mutate()} disabled={newVersion.isPending} data-testid="mapping-new-version">
              <GitBranchPlus aria-hidden />
              {t("integrations.editor.newVersion")}
            </Button>
          )}
          {editable && (
            <>
              <Button size="sm" variant="secondary" onClick={() => save.mutate()} disabled={!dirty || save.isPending || schema.invalid} data-testid="mapping-save">
                {save.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Save aria-hidden />}
                {t("integrations.editor.save")}
              </Button>
              <Input value={changeNote} onChange={(event) => setChangeNote(event.target.value)} placeholder={t("integrations.editor.changeNote")} className="h-8 w-48" aria-label={t("integrations.editor.changeNote")} />
              <Button size="sm" onClick={() => publish.mutate()} disabled={dirty || publish.isPending} title={dirty ? t("integrations.editor.saveFirst") : undefined} data-testid="mapping-publish">
                {publish.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Send aria-hidden />}
                {t("integrations.editor.publish")}
              </Button>
            </>
          )}
        </div>
      </div>

      {!editable && (
        <p className="text-sm text-muted-foreground" data-testid="mapping-read-only">
          {mapping.origin === "Platform" ? t("integrations.editor.platformReadOnly") : detail.version.state === "Draft" ? t("integrations.editor.noRights") : t("integrations.editor.publishedReadOnly")}
        </p>
      )}
      {publishErrors && publishErrors.length > 0 && (
        <Alert variant="destructive" data-testid="publish-errors">
          <AlertTitle>{t("integrations.editor.publishBlocked")}</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-4">
              {publishErrors.map((error, i) => (
                <li key={i}>
                  {error.message}
                  {error.position !== null && error.position !== undefined ? ` (${t("integrations.preview.position", { position: Number(error.position) })})` : ""}
                </li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      )}

      <div className="grid gap-4 xl:grid-cols-[17rem_minmax(0,1.3fr)_minmax(0,1fr)]">
        <Card className="min-w-0">
          <CardHeader className="gap-2">
            <CardTitle className="text-sm">{t("integrations.editor.sourceTitle")}</CardTitle>
            <div className="grid gap-1">
              <Label className="text-xs">{t("integrations.editor.sourceType")}</Label>
              <Select value={mapping.sourceTypeKey ?? ANY_TYPE} onValueChange={(key) => setSourceType.mutate(key === ANY_TYPE ? "" : key)} disabled={!canEdit || mapping.origin === "Platform"}>
                <SelectTrigger className="h-8" data-testid="mapping-source-type">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ANY_TYPE}>{t("integrations.anyType")}</SelectItem>
                  {(types.data ?? []).map((type) => (
                    <SelectItem key={type.key} value={type.key}>
                      {type.key}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="grid gap-1">
              <Label className="text-xs">{t("integrations.editor.sampleDocument")}</Label>
              <Select value={documentId ?? undefined} onValueChange={setDocumentId}>
                <SelectTrigger className="h-8" data-testid="mapping-document">
                  <SelectValue placeholder={t("integrations.editor.chooseDocument")} />
                </SelectTrigger>
                <SelectContent>
                  {(documents.data?.items ?? []).map((document) => (
                    <SelectItem key={document.id} value={document.id}>
                      {[document.documentNumber ?? t("integrations.noNumber"), document.supplierName, t(`documentType.${document.type}`, { defaultValue: document.type }), document.lineCount !== null ? t("integrations.editor.rows", { count: Number(document.lineCount) }) : null].filter(Boolean).join(" · ")}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </CardHeader>
          <CardContent className="max-h-[70vh] overflow-auto">
            {values.isPending && documentId ? <Skeleton className="h-48" /> : <SourceTree values={values.data} onPick={editable && parsed.inSync ? pick : undefined} />}
            {editable && parsed.inSync && <p className="mt-2 text-xs text-muted-foreground">{t("integrations.editor.pickHint")}</p>}
          </CardContent>
        </Card>

        <Card className="min-w-0">
          <CardContent className="pt-4">
            <Tabs value={tab} onValueChange={(value) => setTab(value as typeof tab)}>
              <TabsList>
                <TabsTrigger value="visual" data-testid="mapping-tab-visual">
                  <Wand2 aria-hidden />
                  {t("integrations.editor.visual")}
                </TabsTrigger>
                <TabsTrigger value="code" data-testid="mapping-tab-code">
                  <Code2 aria-hidden />
                  {t("integrations.editor.code")}
                </TabsTrigger>
                <TabsTrigger value="schema" data-testid="mapping-tab-schema">
                  {t("integrations.editor.schema")}
                </TabsTrigger>
              </TabsList>
              <TabsContent value="visual" className="pt-3">
                {parsed.map && parsed.inSync ? (
                  <FieldMapEditor map={parsed.map} onChange={changeMap} activeRow={activeRow} onActiveRowChange={setActiveRow} problems={problems} lookupTables={lookupKeys} readOnly={!editable} />
                ) : parsed.map ? (
                  <Alert data-testid="map-out-of-sync">
                    <AlertTitle>{t("integrations.editor.outOfSyncTitle")}</AlertTitle>
                    <AlertDescription className="grid gap-2">
                      {t("integrations.editor.outOfSync")}
                      {editable && (
                        <Button size="sm" variant="outline" className="w-fit" onClick={() => setExpression(compileFieldMap(parsed.map!))} data-testid="map-rebuild">
                          {t("integrations.editor.rebuild")}
                        </Button>
                      )}
                    </AlertDescription>
                  </Alert>
                ) : (
                  <Alert data-testid="map-none">
                    <AlertTitle>{t("integrations.editor.noMapTitle")}</AlertTitle>
                    <AlertDescription className="grid gap-2">
                      {t("integrations.editor.noMap")}
                      {editable && (
                        <Button size="sm" variant="outline" className="w-fit" onClick={() => (expression.trim() ? setStartVisual(true) : changeMap({ version: 1, rows: [] }))} data-testid="map-start">
                          {t("integrations.editor.startMap")}
                        </Button>
                      )}
                    </AlertDescription>
                  </Alert>
                )}
              </TabsContent>
              <TabsContent value="code" className="pt-3">
                <Textarea
                  value={expression}
                  onChange={(event) => setExpression(event.target.value)}
                  readOnly={!editable}
                  rows={28}
                  spellCheck={false}
                  className="font-mono text-xs leading-snug"
                  aria-label={t("integrations.editor.code")}
                  data-testid="mapping-code"
                />
                <p className="mt-2 text-xs text-muted-foreground">{parsed.map && parsed.inSync ? t("integrations.editor.codeFromMap") : t("integrations.editor.codeHint")}</p>
              </TabsContent>
              <TabsContent value="schema" className="pt-3">
                <Textarea value={schemaText} onChange={(event) => setSchemaText(event.target.value)} readOnly={!editable} rows={20} spellCheck={false} className="font-mono text-xs" aria-label={t("integrations.editor.schema")} data-testid="mapping-schema" placeholder={'{ "type": "object", "required": ["header", "prices"] }'} />
                <p className={schema.invalid ? "mt-2 text-xs text-destructive" : "mt-2 text-xs text-muted-foreground"}>{schema.invalid ? t("integrations.editor.schemaInvalid") : t("integrations.editor.schemaHint")}</p>
              </TabsContent>
            </Tabs>
          </CardContent>
        </Card>

        <Card className="min-w-0">
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle className="flex items-center gap-2 text-sm">
              <Eye className="size-4" aria-hidden />
              {t("integrations.preview.title")}
            </CardTitle>
            <Button size="xs" variant="ghost" onClick={() => void preview.refetch()} disabled={!documentId} data-testid="preview-refresh">
              {t("integrations.preview.refresh")}
            </Button>
          </CardHeader>
          <CardContent>
            <PreviewPane result={preview.data} isFetching={preview.isFetching} failure={previewFailure} documentChosen={documentId !== null} />
          </CardContent>
        </Card>
      </div>
      <ConfirmDialog
        open={startVisual}
        onOpenChange={setStartVisual}
        title={t("integrations.editor.startMapTitle")}
        description={t("integrations.editor.startMapDescription")}
        confirmLabel={t("integrations.editor.startMap")}
        destructive
        onConfirm={() => {
          changeMap({ version: 1, rows: [] });
          setTab("visual");
          setStartVisual(false);
        }}
      />
    </div>
  );
}
