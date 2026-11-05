import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { FormProvider, useForm } from "react-hook-form";
import { toast } from "sonner";
import { ArrowDown, ArrowLeft, ArrowUp, CheckCircle2, CircleAlert, FilePlus2, GitFork, ListPlus, Plus, Save, Send, Trash2 } from "lucide-react";
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
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
  cn,
} from "@docproc/ui";
import { PageHeader, formatDateTime, isApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { DefinitionForm } from "../components/DefinitionForm";
import { isFormSpec, specsFromDefinition } from "../lib/formSpec";
import { REVIEW_NS, localized } from "../lib/labels";
import { toFormValues } from "../lib/values";
import { useDocumentType, useDocumentTypeVersion, type DocumentTypeVersionDetail, type PublishReport } from "./api";
import { useTenantFeatures } from "./DocumentTypesPanel";
import { IdentificationTab } from "./IdentificationTab";
import { ProcessingTab } from "./ProcessingTab";
import { RulesTab } from "./RulesTab";
import {
  addField,
  childrenOf,
  editorKinds,
  editorLocales,
  fieldAt,
  fieldIssues,
  isDefinition,
  isRowField,
  moveField,
  newFieldId,
  newFieldKey,
  removeField,
  updateField,
  type Definition,
  type DefinitionField,
  type FieldIssue,
  type FieldLocation,
} from "./model";

export interface DocumentTypeEditorProps {
  typeKey: string;
  /** ConfigAdmin with `configurableTypes` on: drafts can be started, edited, validated and published. */
  canEdit: boolean;
  /** `customTypes` on (not needed for a copy of a template). */
  customTypes: boolean;
  onBack: () => void;
}

/**
 * The type editor (docs/11 §19, Settings > Document types): one version of a type with the tabs Fields (the field tree
 * with kind, labels per language, required, indexed, options), Preview (the generated review form of the saved
 * version), Versions (every version, validate and publish with the publish-time report, dependents included) and
 * JSON. A draft is saved with `If-Match`; an adopted type is forked first, a published copy gets a new draft.
 */
export function DocumentTypeEditor({ typeKey, canEdit, onBack }: DocumentTypeEditorProps) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  const { client } = useApi();
  const queryClient = useQueryClient();
  const type = useDocumentType(typeKey);
  const [chosen, setChosen] = useState<number | null>(null);
  const versions = useMemo(() => [...(type.data?.versions ?? [])].sort((a, b) => Number(b.version) - Number(a.version)), [type.data]);
  const fallback = type.data ? Number(type.data.draftVersion ?? type.data.publishedVersion ?? versions[0]?.version ?? 1) : null;
  const current = chosen ?? fallback;
  const version = useDocumentTypeVersion(typeKey, current);

  const invalidate = async () => {
    await queryClient.invalidateQueries({ queryKey: queryKeys.documentTypes.all });
  };

  const startDraft = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/document-types/{key}/versions", { params: { path: { key: typeKey } } })),
    onSuccess: async (created) => {
      await invalidate();
      setChosen(Number(created.version));
      toast.success(t("types.draftStarted", { version: created.version }));
    },
  });
  const fork = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/document-types", { body: { from: { template: type.data!.baseTemplate!, mode: "copy" }, key: typeKey, definition: null } })),
    onSuccess: async (forked) => {
      await invalidate();
      setChosen(Number(forked.draftVersion ?? forked.publishedVersion));
      toast.success(t("types.forked", { key: typeKey }));
    },
  });

  // An adopted type follows its template; a ConfigAdmin switches its auto-approval (docs/11 §5.8: adopting an opt-in
  // template keeps it off; `POST /document-types/{key}:upgrade-base {autoApprove}` publishes the switch).
  const autoApproval = useMutation({
    mutationFn: async (autoApprove: boolean) =>
      unwrap(await client.POST("/api/v1/document-types/{key}:upgrade-base", { params: { path: { key: typeKey } }, body: { autoApprove } })),
    onSuccess: async (updated, autoApprove) => {
      await invalidate();
      setChosen(updated.publishedVersion ? Number(updated.publishedVersion) : null);
      toast.success(autoApprove ? t("types.autoApproval.switchedOn", { key: typeKey }) : t("types.autoApproval.switchedOff", { key: typeKey }));
    },
  });

  const title = type.data ? (localized(type.data.name, i18n.language) ?? type.data.key) : typeKey;
  const detail = version.data?.detail;
  const isDraft = detail?.version.state === "Draft";

  return (
    <div className="grid gap-4" data-testid="type-editor" data-type={typeKey}>
      <PageHeader
        title={
          <span className="flex flex-wrap items-center gap-2">
            <Button variant="ghost" size="icon-sm" aria-label={t("types.back")} onClick={onBack}>
              <ArrowLeft aria-hidden />
            </Button>
            {title}
            {type.data && <Badge variant="outline">{t(`types.origin.${type.data.origin}`, { defaultValue: type.data.origin })}</Badge>}
            {detail && (
              <Badge variant={isDraft ? "warning" : "secondary"} data-testid="version-badge">
                {t("types.versionState", { version: detail.version.version, state: t(`types.states.${detail.version.state}`, { defaultValue: detail.version.state }) })}
              </Badge>
            )}
          </span>
        }
        description={<span className="font-mono text-xs">{typeKey}</span>}
        actions={
          canEdit && type.data ? (
            type.data.origin === "Adopted" ? (
              type.data.baseTemplate && (
                <Button type="button" onClick={() => fork.mutate()} disabled={fork.isPending} aria-busy={fork.isPending}>
                  <GitFork aria-hidden />
                  {t("types.fork")}
                </Button>
              )
            ) : (
              !type.data.draftVersion && (
                <Button type="button" onClick={() => startDraft.mutate()} disabled={startDraft.isPending} aria-busy={startDraft.isPending}>
                  <FilePlus2 aria-hidden />
                  {t("types.newVersion")}
                </Button>
              )
            )
          ) : undefined
        }
      />
      {type.data?.origin === "Adopted" && (
        <Alert>
          <AlertTitle>{t("types.adoptedTitle")}</AlertTitle>
          <AlertDescription className="flex flex-col gap-2">
            <span>{t("types.adoptedHint")}</span>
            {detail && detail.version.state === "Published" && (
              <span className="flex flex-wrap items-center gap-2" data-testid="type-auto-approval" data-state={autoApproveOf(detail.definition) ? "on" : "off"}>
                <span>{autoApproveOf(detail.definition) ? t("types.autoApproval.on") : t("types.autoApproval.off")}</span>
                {canEdit && (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => autoApproval.mutate(!autoApproveOf(detail.definition))}
                    disabled={autoApproval.isPending}
                    aria-busy={autoApproval.isPending}
                    data-testid="type-auto-approval-toggle"
                  >
                    {autoApproveOf(detail.definition) ? t("types.autoApproval.switchOff") : t("types.autoApproval.switchOn")}
                  </Button>
                )}
              </span>
            )}
          </AlertDescription>
        </Alert>
      )}
      {!detail ? (
        <Skeleton className="h-64 w-full" aria-busy />
      ) : (
        <VersionWorkspace
          key={`${detail.version.version}:${detail.version.revision}`}
          typeKey={typeKey}
          detail={detail}
          etag={version.data?.etag ?? null}
          editable={canEdit && isDraft}
          canConfigAdmin={canEdit}
          versions={versions}
          onChooseVersion={setChosen}
          onChanged={invalidate}
        />
      )}
    </div>
  );
}

interface WorkspaceProps {
  typeKey: string;
  detail: DocumentTypeVersionDetail;
  etag: string | null;
  editable: boolean;
  /** ConfigAdmin with configurableTypes: may change the tenant fallback rules of the type, whatever version is open. */
  canConfigAdmin: boolean;
  versions: ReadonlyArray<DocumentTypeVersionDetail["version"]>;
  onChooseVersion: (version: number) => void;
  onChanged: () => Promise<void>;
}

function VersionWorkspace({ typeKey, detail, etag, editable, canConfigAdmin, versions, onChooseVersion, onChanged }: WorkspaceProps) {
  const { t } = useTranslation(REVIEW_NS);
  const { client } = useApi();
  const saved = useMemo(() => (isDefinition(detail.definition) ? detail.definition : null), [detail.definition]);
  const [definition, setDefinition] = useState<Definition | null>(saved);
  const [selected, setSelected] = useState<FieldLocation | null>(saved && saved.fields.length > 0 ? [0] : null);
  const [tab, setTab] = useState("fields");
  const [report, setReport] = useState<PublishReport | null>(null);
  const [changeNote, setChangeNote] = useState("");
  const [saveErrors, setSaveErrors] = useState<string[]>([]);
  const versionNumber = Number(detail.version.version);
  const dirty = JSON.stringify(definition) !== JSON.stringify(saved);
  const issues = useMemo(() => (definition ? fieldIssues(definition) : []), [definition]);
  // The tenant fallback identification rules (P5) need supplierLayouts; their writes ConfigAdmin, whatever the version.
  const features = useTenantFeatures();
  const identificationOn = features.data?.supplierLayouts === true;
  const canConfigure = canConfigAdmin;

  const save = useMutation({
    mutationFn: async () => {
      const result = await client.PUT("/api/v1/document-types/{key}/versions/{version}", {
        params: { path: { key: typeKey, version: versionNumber } },
        body: definition as never,
        headers: etag ? { "If-Match": etag } : {},
      });
      return unwrap(result);
    },
    meta: { silent: true },
    onSuccess: async () => {
      setSaveErrors([]);
      toast.success(t("types.saved"));
      await onChanged();
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        setSaveErrors(Object.entries(error.fieldErrors).map(([where, messages]) => `${where}: ${messages.join(" ")}`));
        return;
      }
      toast.error(isApiError(error) ? error.title : t("types.failed"), { description: isApiError(error) ? (error.problem?.detail ?? undefined) : undefined });
    },
  });

  const validate = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/document-types/{key}/versions/{version}:validate", { params: { path: { key: typeKey, version: versionNumber } } })),
    onSuccess: (result) => {
      setReport(result);
      setTab("versions");
    },
  });

  const publish = useMutation({
    mutationFn: async () => {
      const result = await client.POST("/api/v1/document-types/{key}/versions/{version}:publish", {
        params: { path: { key: typeKey, version: versionNumber } },
        body: { changeNote: changeNote.trim() || null } as never,
        headers: etag ? { "If-Match": etag } : {},
      });
      // 422 carries the publish report: what blocks, the dependents and the changes (docs/11 §5.7).
      if (result.response.status === 422) return { published: false as const, report: result.error as unknown as PublishReport };
      unwrap(result);
      return { published: true as const, report: null };
    },
    onSuccess: async (outcome) => {
      if (!outcome.published) {
        setReport(outcome.report);
        setTab("versions");
        toast.error(t("types.publishBlocked"));
        return;
      }
      toast.success(t("types.published", { version: versionNumber }));
      setReport(null);
      await onChanged();
    },
  });

  if (!definition) {
    return (
      <Alert variant="destructive">
        <AlertTitle>{t("types.unreadable")}</AlertTitle>
      </Alert>
    );
  }

  const field = selected ? fieldAt(definition, selected) : null;
  const addTopLevel = () => {
    const section = definition.ui?.sections?.[0]?.id;
    const created: DefinitionField = { id: newFieldId(definition), key: newFieldKey(definition.fields), kind: "string", label: { en: t("types.newFieldLabel") }, ...(section ? { section } : {}) };
    const next = addField(definition, null, created);
    setDefinition(next.definition);
    setSelected(next.location);
  };
  const addChild = (parent: FieldLocation) => {
    const container = fieldAt(definition, parent)!;
    const created: DefinitionField = { id: newFieldId(definition), key: newFieldKey(childrenOf(container) ?? []), kind: "string", label: { en: t("types.newFieldLabel") } };
    const next = addField(definition, parent, created);
    setDefinition(next.definition);
    setSelected(next.location);
  };

  return (
    <Tabs value={tab} onValueChange={setTab} className="grid gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <TabsList>
          <TabsTrigger value="fields">{t("types.tabs.fields")}</TabsTrigger>
          <TabsTrigger value="rules" data-testid="type-tab-rules">
            {t("types.tabs.rules")}
          </TabsTrigger>
          <TabsTrigger value="processing" data-testid="type-tab-processing">
            {t("types.tabs.processing")}
          </TabsTrigger>
          {identificationOn && (
            <TabsTrigger value="identification" data-testid="type-tab-identification">
              {t("types.tabs.identification")}
            </TabsTrigger>
          )}
          <TabsTrigger value="preview">{t("types.tabs.preview")}</TabsTrigger>
          <TabsTrigger value="versions">{t("types.tabs.versions")}</TabsTrigger>
          <TabsTrigger value="json">{t("types.tabs.json")}</TabsTrigger>
        </TabsList>
        {editable && (
          <span className="ml-auto flex flex-wrap gap-2">
            <Button type="button" variant="secondary" onClick={() => save.mutate()} disabled={!dirty || issues.length > 0 || save.isPending} aria-busy={save.isPending} data-testid="save-draft">
              <Save aria-hidden />
              {t("types.save")}
            </Button>
            <Button type="button" variant="outline" onClick={() => validate.mutate()} disabled={dirty || validate.isPending} aria-busy={validate.isPending} title={dirty ? t("types.saveFirst") : undefined}>
              <CheckCircle2 aria-hidden />
              {t("types.validate")}
            </Button>
          </span>
        )}
      </div>
      {saveErrors.length > 0 && (
        <Alert variant="destructive" data-testid="save-errors">
          <CircleAlert aria-hidden />
          <AlertTitle>{t("types.saveRefused")}</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-4">
              {saveErrors.map((error) => (
                <li key={error}>{error}</li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      )}

      <TabsContent value="fields" className="grid gap-4 lg:grid-cols-[minmax(16rem,1fr)_2fr]">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between gap-2">
            <CardTitle className="text-base">{t("types.fieldsTitle", { count: definition.fields.length })}</CardTitle>
            {editable && (
              <Button type="button" size="sm" variant="outline" onClick={addTopLevel} data-testid="add-field">
                <Plus aria-hidden />
                {t("types.addField")}
              </Button>
            )}
          </CardHeader>
          <CardContent>
            <FieldTree fields={definition.fields} parent={[]} selected={selected} issues={issues} onSelect={setSelected} />
          </CardContent>
        </Card>
        <Card>
          <CardContent className="pt-6">
            {field && selected ? (
              <FieldForm
                key={field.id}
                definition={definition}
                location={selected}
                field={field}
                editable={editable}
                issues={issues.filter((issue) => sameLocation(issue.location, selected))}
                onChange={(patch) => setDefinition(updateField(definition, selected, patch))}
                onMove={(delta) => {
                  const moved = moveField(definition, selected, delta);
                  setDefinition(moved.definition);
                  setSelected(moved.location);
                }}
                onRemove={() => {
                  setDefinition(removeField(definition, selected));
                  setSelected(null);
                }}
                onAddChild={() => addChild(selected)}
              />
            ) : (
              <p className="text-sm text-muted-foreground">{t("types.pickField")}</p>
            )}
          </CardContent>
        </Card>
      </TabsContent>

      <TabsContent value="rules">
        {tab === "rules" && <RulesTab typeKey={typeKey} version={versionNumber} definition={definition} editable={editable} dirty={dirty} onChange={setDefinition} />}
      </TabsContent>

      <TabsContent value="processing">{tab === "processing" && <ProcessingTab typeKey={typeKey} />}</TabsContent>

      {identificationOn && <TabsContent value="identification">{tab === "identification" && <IdentificationTab typeKey={typeKey} canEdit={canConfigure} />}</TabsContent>}

      <TabsContent value="preview">
        <Preview detail={detail} dirty={dirty} />
      </TabsContent>

      <TabsContent value="versions" className="grid gap-4">
        {report && <PublishReportView report={report} />}
        {editable && (
          <Card>
            <CardHeader>
              <CardTitle className="text-base">{t("types.publishTitle", { version: versionNumber })}</CardTitle>
            </CardHeader>
            <CardContent className="grid gap-3">
              <div className="grid gap-1.5">
                <Label htmlFor="change-note">{t("types.changeNote")}</Label>
                <Input id="change-note" value={changeNote} onChange={(event) => setChangeNote(event.target.value)} maxLength={500} />
              </div>
              <div>
                <Button type="button" onClick={() => publish.mutate()} disabled={dirty || publish.isPending} aria-busy={publish.isPending} title={dirty ? t("types.saveFirst") : undefined} data-testid="publish">
                  <Send aria-hidden />
                  {t("types.publish")}
                </Button>
              </div>
            </CardContent>
          </Card>
        )}
        <VersionsTable versions={versions} current={versionNumber} onChoose={onChooseVersion} />
      </TabsContent>

      <TabsContent value="json">
        <JsonTab definition={definition} editable={editable} onApply={setDefinition} />
      </TabsContent>
    </Tabs>
  );
}

function sameLocation(a: FieldLocation, b: FieldLocation): boolean {
  return a.length === b.length && a.every((index, i) => index === b[i]);
}

function FieldTree({ fields, parent, selected, issues, onSelect }: { fields: readonly DefinitionField[]; parent: FieldLocation; selected: FieldLocation | null; issues: readonly FieldIssue[]; onSelect: (location: FieldLocation) => void }) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  if (fields.length === 0) return <p className="text-sm text-muted-foreground">{t("types.noFields")}</p>;
  return (
    <ul className={cn("grid gap-0.5", parent.length > 0 && "ml-4 border-l pl-2")} role={parent.length === 0 ? "tree" : "group"} aria-label={parent.length === 0 ? t("types.fieldsTree") : undefined}>
      {fields.map((field, index) => {
        const location = [...parent, index];
        const active = selected !== null && sameLocation(selected, location);
        const children = childrenOf(field);
        const broken = issues.some((issue) => sameLocation(issue.location, location));
        return (
          <li key={field.id} role="treeitem" aria-selected={active} aria-expanded={children ? true : undefined}>
            <button
              type="button"
              onClick={() => onSelect(location)}
              className={cn("flex w-full flex-wrap items-center gap-1.5 rounded-md px-2 py-1 text-left text-sm hover:bg-muted", active && "bg-muted font-medium")}
              data-field-key={field.key}
            >
              <span>{localized(field.label, i18n.language) ?? field.key}</span>
              <span className="font-mono text-xs text-muted-foreground">{field.key}</span>
              <Badge variant="outline" className="text-[10px]">
                {t(`types.kinds.${field.kind}`, { defaultValue: field.kind })}
              </Badge>
              {field.required && <Badge variant="secondary">{t("types.required")}</Badge>}
              {field.indexed && <Badge variant="secondary">{t("types.indexed")}</Badge>}
              {broken && <CircleAlert className="size-4 text-destructive" aria-label={t("types.hasIssues")} />}
            </button>
            {children && children.length > 0 && <FieldTree fields={children} parent={location} selected={selected} issues={issues} onSelect={onSelect} />}
          </li>
        );
      })}
    </ul>
  );
}

const SCALAR_INDEXABLE = new Set(["string", "text", "identifier", "date", "decimal", "integer", "percent", "money", "currency", "boolean", "enum", "unit"]);

interface FieldFormProps {
  definition: Definition;
  location: FieldLocation;
  field: DefinitionField;
  editable: boolean;
  issues: readonly FieldIssue[];
  onChange: (patch: Partial<DefinitionField>) => void;
  onMove: (delta: -1 | 1) => void;
  onRemove: () => void;
  onAddChild: () => void;
}

function FieldForm({ definition, location, field, editable, issues, onChange, onMove, onRemove, onAddChild }: FieldFormProps) {
  const { t } = useTranslation(REVIEW_NS);
  // A bare member (a line number, the VAT summary) belongs to the coded records: shown, not changed.
  const fixed = field.provenance === false;
  const canChange = editable && !fixed;
  const id = (name: string) => `field-${field.id}-${name}`;
  const locales = [...new Set([...editorLocales, ...Object.keys(field.label ?? {})])];
  const sections = definition.ui?.sections ?? [];
  const container = childrenOf(field) !== null;
  const issueText = (code: FieldIssue["code"]) => t(`types.issues.${code}`);

  return (
    <div className="grid gap-4" data-testid="field-form" data-field-id={field.id}>
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold">{t("types.fieldTitle", { key: field.key })}</h3>
        <span className="font-mono text-xs text-muted-foreground">{field.id}</span>
        {editable && (
          <span className="ml-auto flex gap-1">
            <Button type="button" variant="ghost" size="icon-sm" aria-label={t("types.moveUp")} onClick={() => onMove(-1)} disabled={location[location.length - 1] === 0}>
              <ArrowUp aria-hidden />
            </Button>
            <Button type="button" variant="ghost" size="icon-sm" aria-label={t("types.moveDown")} onClick={() => onMove(1)}>
              <ArrowDown aria-hidden />
            </Button>
            {container && (
              <Button type="button" variant="ghost" size="sm" onClick={onAddChild}>
                <ListPlus aria-hidden />
                {t("types.addChild")}
              </Button>
            )}
            {!fixed && (
              <Button type="button" variant="ghost" size="icon-sm" aria-label={t("types.removeField", { key: field.key })} onClick={onRemove}>
                <Trash2 aria-hidden />
              </Button>
            )}
          </span>
        )}
      </div>
      {fixed && <p className="text-sm text-muted-foreground">{t("types.fixedField")}</p>}
      {issues.length > 0 && (
        <ul className="text-sm text-destructive" role="alert">
          {issues.map((issue) => (
            <li key={issue.code}>{issueText(issue.code)}</li>
          ))}
        </ul>
      )}
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="grid gap-1.5">
          <Label htmlFor={id("key")}>{t("types.fieldKey")}</Label>
          <Input id={id("key")} value={field.key} disabled={!canChange} onChange={(event) => onChange({ key: event.target.value.trim() })} aria-invalid={issues.some((i) => i.code.startsWith("key")) || undefined} />
          <p className="text-xs text-muted-foreground">{t("types.fieldKeyHint")}</p>
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor={id("kind")}>{t("types.fieldKind")}</Label>
          <Select value={field.kind} onValueChange={(kind) => onChange({ kind })} disabled={!canChange}>
            <SelectTrigger id={id("kind")} className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {[...new Set([...editorKinds, field.kind])].map((kind) => (
                <SelectItem key={kind} value={kind}>
                  {t(`types.kinds.${kind}`, { defaultValue: kind })}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        {locales.map((locale) => (
          <div key={locale} className="grid gap-1.5">
            <Label htmlFor={id(`label-${locale}`)}>{t("types.labelIn", { locale: locale.toUpperCase() })}</Label>
            <Input
              id={id(`label-${locale}`)}
              value={field.label?.[locale] ?? ""}
              disabled={!editable}
              onChange={(event) => {
                const label = { ...(field.label ?? {}) };
                if (event.target.value === "") delete label[locale];
                else label[locale] = event.target.value;
                onChange({ label: Object.keys(label).length > 0 ? label : undefined });
              }}
            />
          </div>
        ))}
        {location.length === 1 && sections.length > 0 && (
          <div className="grid gap-1.5">
            <Label htmlFor={id("section")}>{t("types.section")}</Label>
            <Select value={field.section ?? "__none__"} onValueChange={(section) => onChange({ section: section === "__none__" ? undefined : section })} disabled={!editable}>
              <SelectTrigger id={id("section")} className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="__none__">{t("types.noSection")}</SelectItem>
                {sections.map((section) => (
                  <SelectItem key={section.id} value={section.id}>
                    {localized(section.label, "en") ?? section.id}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}
        <div className="grid gap-1.5 sm:col-span-2">
          <Label htmlFor={id("description")}>{t("types.description")}</Label>
          <Textarea id={id("description")} value={field.description ?? ""} disabled={!editable} rows={2} onChange={(event) => onChange({ description: event.target.value || undefined })} />
          <p className="text-xs text-muted-foreground">{t("types.descriptionHint")}</p>
        </div>
        {SCALAR_INDEXABLE.has(field.kind) && <ExtractionHintsInputs key={field.id} field={field} locales={locales} editable={editable} onChange={onChange} />}
        <div className="flex items-center justify-between gap-4 rounded-md border p-3">
          <div>
            <Label htmlFor={id("required")}>{t("types.required")}</Label>
            <p className="text-xs text-muted-foreground">{t("types.requiredHint")}</p>
          </div>
          <Switch id={id("required")} checked={!!field.required} disabled={!canChange} onCheckedChange={(required) => onChange({ required: required || undefined })} />
        </div>
        <div className="flex items-center justify-between gap-4 rounded-md border p-3">
          <div>
            <Label htmlFor={id("indexed")}>{t("types.indexed")}</Label>
            <p className="text-xs text-muted-foreground">{t(isRowField(definition, location) ? "types.indexedRowHint" : "types.indexedHint")}</p>
          </div>
          <Switch id={id("indexed")} checked={!!field.indexed} disabled={!canChange || !SCALAR_INDEXABLE.has(field.kind) || field.list === true} onCheckedChange={(indexed) => onChange({ indexed: indexed || undefined })} />
        </div>
      </div>
      {field.kind === "enum" && <OptionsEditor field={field} editable={canChange} onChange={onChange} />}
    </div>
  );
}

/** A field's extraction hints (docs/11 §5.2 `extraction`): the labels it is printed with per language, and a pattern. */
function ExtractionHintsInputs({ field, locales, editable, onChange }: { field: DefinitionField; locales: readonly string[]; editable: boolean; onChange: (patch: Partial<DefinitionField>) => void }) {
  const { t } = useTranslation(REVIEW_NS);
  const extraction = (field.extraction ?? {}) as { labels?: Record<string, string[]> | null; pattern?: string | null; [other: string]: unknown };
  // The typed text per language, so a trailing comma survives until the next label is typed.
  const [texts, setTexts] = useState<Record<string, string>>(() => Object.fromEntries(locales.map((locale) => [locale, (extraction.labels?.[locale] ?? []).join(", ")])));
  const update = (patch: Partial<typeof extraction>) => {
    const next: Record<string, unknown> = { ...extraction, ...patch };
    for (const key of Object.keys(next)) {
      if (next[key] === undefined || next[key] === null) delete next[key];
    }
    onChange({ extraction: Object.keys(next).length > 0 ? next : undefined });
  };
  const setLabels = (locale: string, text: string) => {
    setTexts((current) => ({ ...current, [locale]: text }));
    const labels = { ...(extraction.labels ?? {}) };
    const list = text
      .split(",")
      .map((label) => label.trim())
      .filter((label) => label.length > 0);
    if (list.length > 0) labels[locale] = list;
    else delete labels[locale];
    update({ labels: Object.keys(labels).length > 0 ? labels : undefined });
  };
  const pattern = extraction.pattern ?? "";
  const patternValid = (() => {
    if (pattern === "") return true;
    try {
      new RegExp(pattern);
      return true;
    } catch {
      return false;
    }
  })();
  const id = (part: string) => `field-${field.id}-${part}`;
  return (
    <>
      {locales.map((locale) => (
        <div key={locale} className="grid gap-1.5">
          <Label htmlFor={id(`printed-${locale}`)}>{t("types.printedAs", { locale: locale.toUpperCase() })}</Label>
          <Input id={id(`printed-${locale}`)} value={texts[locale] ?? ""} disabled={!editable} onChange={(event) => setLabels(locale, event.target.value)} />
        </div>
      ))}
      <p className="text-xs text-muted-foreground sm:col-span-2">{t("types.printedAsHint")}</p>
      {(field.kind === "string" || field.kind === "text" || field.kind === "identifier") && (
        <div className="grid gap-1.5 sm:col-span-2">
          <Label htmlFor={id("pattern")}>{t("types.pattern")}</Label>
          <Input id={id("pattern")} className="font-mono" value={pattern} disabled={!editable} aria-invalid={!patternValid || undefined} onChange={(event) => update({ pattern: event.target.value === "" ? undefined : event.target.value })} />
          <p className={patternValid ? "text-xs text-muted-foreground" : "text-xs text-destructive"} role={patternValid ? undefined : "alert"}>
            {patternValid ? t("types.patternHint") : t("types.patternInvalid")}
          </p>
        </div>
      )}
    </>
  );
}

function OptionsEditor({ field, editable, onChange }: { field: DefinitionField; editable: boolean; onChange: (patch: Partial<DefinitionField>) => void }) {
  const { t } = useTranslation(REVIEW_NS);
  const options = field.options ?? [];
  const set = (index: number, patch: Partial<{ code: string; en: string; de: string }>) => {
    const next = options.map((option, i) => {
      if (i !== index) return option;
      const label = { ...(option.label ?? {}) };
      for (const locale of ["en", "de"] as const) {
        const value = patch[locale];
        if (value === undefined) continue;
        if (value === "") delete label[locale];
        else label[locale] = value;
      }
      return { ...option, ...(patch.code !== undefined ? { code: patch.code } : {}), label: Object.keys(label).length > 0 ? label : null };
    });
    onChange({ options: next });
  };
  return (
    <fieldset className="grid gap-2" data-testid="options-editor">
      <legend className="text-sm font-medium">{t("types.options")}</legend>
      {options.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("types.optionCode")}</TableHead>
              <TableHead>{t("types.labelIn", { locale: "EN" })}</TableHead>
              <TableHead>{t("types.labelIn", { locale: "DE" })}</TableHead>
              <TableHead className="w-10" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {options.map((option, index) => (
              <TableRow key={index}>
                <TableCell>
                  <Input aria-label={t("types.optionCodeOf", { n: index + 1 })} value={option.code} disabled={!editable} onChange={(event) => set(index, { code: event.target.value.trim() })} />
                </TableCell>
                <TableCell>
                  <Input aria-label={t("types.optionLabelOf", { n: index + 1, locale: "EN" })} value={option.label?.en ?? ""} disabled={!editable} onChange={(event) => set(index, { en: event.target.value })} />
                </TableCell>
                <TableCell>
                  <Input aria-label={t("types.optionLabelOf", { n: index + 1, locale: "DE" })} value={option.label?.de ?? ""} disabled={!editable} onChange={(event) => set(index, { de: event.target.value })} />
                </TableCell>
                <TableCell>
                  {editable && (
                    <Button type="button" variant="ghost" size="icon-sm" aria-label={t("types.removeOption", { code: option.code || index + 1 })} onClick={() => onChange({ options: options.filter((_, i) => i !== index) })}>
                      <Trash2 aria-hidden />
                    </Button>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {editable && (
        <div>
          <Button type="button" variant="outline" size="sm" onClick={() => onChange({ options: [...options, { code: `OPTION${options.length + 1}` }] })}>
            <Plus aria-hidden />
            {t("types.addOption")}
          </Button>
        </div>
      )}
    </fieldset>
  );
}

/** The generated review form of the saved version on an empty document (docs/11 §19 Preview). */
function Preview({ detail, dirty }: { detail: DocumentTypeVersionDetail; dirty: boolean }) {
  const { t } = useTranslation(REVIEW_NS);
  const spec = isFormSpec(detail.formSpec) ? detail.formSpec : null;
  const layout = useMemo(() => (spec ? specsFromDefinition(spec, {}, { schemaVersion: 2 }) : null), [spec]);
  const form = useForm<Record<string, unknown>>({ defaultValues: layout ? toFormValues({}, layout.specs) : {} });
  if (!layout) return <p className="text-sm text-muted-foreground">{t("types.noPreview")}</p>;
  return (
    <div className="grid gap-3" data-testid="type-preview">
      {dirty && <p className="text-sm text-muted-foreground">{t("types.previewSaved")}</p>}
      <FormProvider {...form}>
        <div className="@container rounded-lg border p-4">
          <DefinitionForm layout={layout} canonical={{}} disabled onHighlight={() => undefined} />
        </div>
      </FormProvider>
    </div>
  );
}

function VersionsTable({ versions, current, onChoose }: { versions: WorkspaceProps["versions"]; current: number; onChoose: (version: number) => void }) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">{t("types.versionsTitle")}</CardTitle>
      </CardHeader>
      <CardContent>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("types.columns.version")}</TableHead>
              <TableHead>{t("types.columns.state")}</TableHead>
              <TableHead>{t("types.columns.llmSchema")}</TableHead>
              <TableHead>{t("types.columns.publishedAt")}</TableHead>
              <TableHead>{t("types.columns.changeNote")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {versions.map((version) => (
              <TableRow key={version.version} data-version={version.version} data-current={Number(version.version) === current || undefined}>
                <TableCell className="tabular-nums">v{version.version}</TableCell>
                <TableCell>
                  <Badge variant={version.state === "Published" ? "success" : version.state === "Draft" ? "warning" : "outline"}>{t(`types.states.${version.state}`, { defaultValue: version.state })}</Badge>
                </TableCell>
                <TableCell className="font-mono text-xs">{version.llmSchema ?? "—"}</TableCell>
                <TableCell>{version.publishedAt ? `${formatDateTime(version.publishedAt, i18n.language)} · ${version.publishedBy ?? ""}` : "—"}</TableCell>
                <TableCell className="max-w-64 truncate">{version.changeNote ?? "—"}</TableCell>
                <TableCell className="text-right">
                  {Number(version.version) !== current && (
                    <Button type="button" variant="ghost" size="sm" onClick={() => onChoose(Number(version.version))}>
                      {t("types.openVersion")}
                    </Button>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </CardContent>
    </Card>
  );
}

/** The publish-time report (docs/11 §5.7): whether it can publish, what blocks, the dependents, warnings and changes. */
export function PublishReportView({ report }: { report: PublishReport }) {
  const { t } = useTranslation(REVIEW_NS);
  return (
    <Card data-testid="publish-report" data-can-publish={report.canPublish}>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          {report.canPublish ? <CheckCircle2 className="size-4 text-emerald-600" aria-hidden /> : <CircleAlert className="size-4 text-destructive" aria-hidden />}
          {report.canPublish ? t("types.report.ok") : t("types.report.blocked")}
        </CardTitle>
      </CardHeader>
      <CardContent className="grid gap-4 text-sm">
        {report.llmSchema && (
          <p>
            {t("types.report.llmSchema")} <span className="font-mono">{report.llmSchema}</span>
          </p>
        )}
        <ReportList title={t("types.report.problems")} items={report.problems.map((p) => `${p.where}: ${p.message}`)} tone="destructive" />
        <ReportList
          title={t("types.report.dependents")}
          items={report.blockingDependents.map((d) => t("types.report.dependent", { kind: d.kind, name: d.name, path: d.path }))}
          tone="destructive"
        />
        <ReportList title={t("types.report.warnings")} items={report.warnings.map((w) => `${w.where}: ${w.message}`)} tone="warning" />
        {report.changes.length > 0 && (
          <div>
            <h4 className="mb-1 font-medium">{t("types.report.changes")}</h4>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("types.report.change")}</TableHead>
                  <TableHead>{t("types.report.field")}</TableHead>
                  <TableHead>{t("types.report.path")}</TableHead>
                  <TableHead>{t("types.report.kind")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {report.changes.map((change) => (
                  <TableRow key={`${change.change}-${change.fieldId}`}>
                    <TableCell>{t(`types.report.changeKinds.${change.change}`, { defaultValue: change.change })}</TableCell>
                    <TableCell className="font-mono text-xs">{change.fieldId}</TableCell>
                    <TableCell className="font-mono text-xs">{[change.oldPath, change.newPath].filter(Boolean).join(" → ")}</TableCell>
                    <TableCell className="font-mono text-xs">{[change.oldKind, change.newKind].filter(Boolean).join(" → ")}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function ReportList({ title, items, tone }: { title: string; items: string[]; tone: "destructive" | "warning" }) {
  if (items.length === 0) return null;
  return (
    <div>
      <h4 className="mb-1 font-medium">{title}</h4>
      <ul className={cn("list-disc pl-5", tone === "destructive" ? "text-destructive" : "text-amber-700 dark:text-amber-400")}>
        {items.map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ul>
    </div>
  );
}

function JsonTab({ definition, editable, onApply }: { definition: Definition; editable: boolean; onApply: (definition: Definition) => void }) {
  const { t } = useTranslation(REVIEW_NS);
  const pretty = JSON.stringify(definition, null, 2);
  const [text, setText] = useState(pretty);
  const [error, setError] = useState<string | null>(null);
  return (
    <div className="grid gap-2">
      <Label htmlFor="definition-json">{t("types.json")}</Label>
      <Textarea id="definition-json" value={editable ? text : pretty} readOnly={!editable} onChange={(event) => setText(event.target.value)} rows={24} className="font-mono text-xs" spellCheck={false} aria-invalid={error ? true : undefined} />
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {editable && (
        <div>
          <Button
            type="button"
            variant="outline"
            onClick={() => {
              try {
                const parsed: unknown = JSON.parse(text);
                if (!isDefinition(parsed)) throw new Error(t("types.jsonNotDefinition"));
                setError(null);
                onApply(parsed);
              } catch (e) {
                setError(e instanceof Error ? e.message : String(e));
              }
            }}
          >
            {t("types.applyJson")}
          </Button>
        </div>
      )}
    </div>
  );
}

/** Whether a version's definition auto-approves (`processing.review.autoApprove`). */
function autoApproveOf(definition: unknown): boolean {
  const processing = (definition as { processing?: { review?: { autoApprove?: unknown } } } | null)?.processing;
  return processing?.review?.autoApprove === true;
}
