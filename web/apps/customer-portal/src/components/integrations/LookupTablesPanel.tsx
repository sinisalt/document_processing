import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft, Loader2, Pencil, Plus, Save, Trash2 } from "lucide-react";
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Checkbox,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Skeleton,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@docproc/ui";
import { DataTable, createDataTableColumnHelper, isApiError, queryKeys, relativeTime, unwrap, useApi } from "@docproc/app-shell";
import { EMPTY_ARRAY } from "@/lib/queries";
import { SectionIntro } from "@/components/settings/SectionIntro";
import { useLookupTable, useLookupTables, type LookupEntry, type LookupTable } from "./api";

const helper = createDataTableColumnHelper<LookupTable>();
const KEY_PATTERN = /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface EntryRow {
  from: string;
  to: string;
  validFrom: string;
  validTo: string;
}

/** The rows as `PUT …/entries` takes them: blank rows dropped, dates as given (`yyyy-MM-dd`), or the first row that is wrong. */
export function entriesOf(rows: EntryRow[]): { entries: LookupEntry[] } | { invalidRow: number } {
  const entries: LookupEntry[] = [];
  for (const [i, row] of rows.entries()) {
    if (!row.from.trim() && !row.to.trim()) continue;
    if (!row.from.trim() || (row.validFrom && !DATE.test(row.validFrom)) || (row.validTo && !DATE.test(row.validTo))) return { invalidRow: i };
    entries.push({ from: row.from.trim(), to: row.to.trim(), validFrom: row.validFrom || null, validTo: row.validTo || null, labels: null });
  }
  return { entries };
}

/**
 * Settings > Integrations > Lookup tables (docs/11 §6.1, §19): the tenant's code tables that `$table("erp-uom", unit,
 * "ST")` reads, next to the platform's code lists (read-only); an entry maps a value to the receiver's code, optionally
 * from or until a day. Writing them needs ConfigAdmin. CSV import comes later.
 */
export function LookupTablesPanel({ canEdit }: { canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const tables = useLookupTables();
  const [open, setOpen] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("key", { header: t("integrations.lookups.columns.key"), cell: (info) => <span className="font-mono text-xs font-medium">{info.getValue()}</span> }),
        helper.accessor("description", { header: t("integrations.lookups.columns.description"), cell: (info) => info.getValue() ?? "" }),
        helper.accessor("platform", { header: t("integrations.lookups.columns.owner"), cell: (info) => (info.getValue() ? <Badge variant="secondary">{t("integrations.lookups.platform")}</Badge> : t("integrations.lookups.tenant")) }),
        helper.accessor("entryCount", { header: t("integrations.lookups.columns.entries"), cell: (info) => Number(info.getValue()) }),
        helper.accessor("updatedAt", { header: t("integrations.lookups.columns.updated"), cell: (info) => relativeTime(info.getValue(), i18n.language), sortFn: "datetime" }),
        helper.display({
          id: "actions",
          header: "",
          cell: ({ row }) => (
            <div className="flex justify-end">
              <Button size="xs" variant="ghost" onClick={() => setOpen(row.original.key)} data-testid={`open-lookup-${row.original.key}`}>
                <Pencil aria-hidden />
                {row.original.platform || !canEdit ? t("integrations.view") : t("integrations.edit")}
              </Button>
            </div>
          ),
        }),
      ]),
    [t, i18n.language, canEdit],
  );

  if (open) return <LookupTableEditor tableKey={open} canEdit={canEdit} onBack={() => setOpen(null)} />;

  return (
    <div className="flex flex-col gap-4" data-testid="lookups-panel">
      <SectionIntro
        title={t("integrations.lookups.title")}
        description={t("integrations.lookups.description")}
        action={
          canEdit && (
            <Button onClick={() => setCreating(true)} data-testid="new-lookup">
              <Plus aria-hidden />
              {t("integrations.lookups.create")}
            </Button>
          )
        }
      />
      <DataTable columns={columns} data={tables.data ?? EMPTY_ARRAY} getRowId={(row) => `${row.platform ? "p" : "t"}:${row.key}`} isLoading={tables.isPending} query={tables} caption={t("integrations.lookups.title")} emptyTitle={t("integrations.lookups.empty")} />
      <CreateLookupDialog open={creating} onClose={() => setCreating(false)} onCreated={setOpen} />
    </div>
  );
}

function CreateLookupDialog({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (key: string) => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [key, setKey] = useState("");
  const [description, setDescription] = useState("");
  const [caseSensitive, setCaseSensitive] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (open) {
      setKey("");
      setDescription("");
      setCaseSensitive(false);
      setError(null);
    }
  }, [open]);
  const create = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/lookup-tables", { body: { key: key.trim(), description: description.trim() || null, caseSensitive } })),
    meta: { silent: true },
    onSuccess: async (table) => {
      toast.success(t("integrations.lookups.created", { key: table.key }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.outputs.lookupTables() });
      onClose();
      onCreated(table.key);
    },
    onError: (failure) => setError(isApiError(failure) ? (Object.values(failure.fieldErrors)[0]?.join(" ") ?? failure.problem?.detail ?? failure.title) : String(failure)),
  });
  const valid = KEY_PATTERN.test(key.trim());
  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{t("integrations.lookups.createTitle")}</DialogTitle>
          <DialogDescription>{t("integrations.lookups.createDescription")}</DialogDescription>
        </DialogHeader>
        <form
          id="create-lookup-form"
          className="grid gap-4"
          onSubmit={(event) => {
            event.preventDefault();
            if (valid) create.mutate();
          }}
        >
          <div className="grid gap-1.5">
            <Label htmlFor="lookup-key">{t("integrations.lookups.fields.key")}</Label>
            <Input id="lookup-key" value={key} onChange={(event) => setKey(event.target.value)} placeholder="erp-uom" className="font-mono" autoFocus data-testid="lookup-key" />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="lookup-description">{t("integrations.lookups.fields.description")}</Label>
            <Input id="lookup-description" value={description} onChange={(event) => setDescription(event.target.value)} />
          </div>
          <Label className="flex items-center gap-2 font-normal">
            <Checkbox checked={caseSensitive} onCheckedChange={(on) => setCaseSensitive(on === true)} />
            {t("integrations.lookups.fields.caseSensitive")}
          </Label>
          {error && (
            <p className="text-sm text-destructive" role="alert">
              {error}
            </p>
          )}
        </form>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            {t("common:dialog.cancel")}
          </Button>
          <Button type="submit" form="create-lookup-form" disabled={!valid || create.isPending} data-testid="lookup-create-submit">
            {create.isPending && <Loader2 className="animate-spin" aria-hidden />}
            {t("integrations.lookups.create")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function LookupTableEditor({ tableKey, canEdit, onBack }: { tableKey: string; canEdit: boolean; onBack: () => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const table = useLookupTable(tableKey);
  const [rows, setRows] = useState<EntryRow[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (table.data) setRows(table.data.entries.map((e) => ({ from: e.from, to: e.to, validFrom: e.validFrom ?? "", validTo: e.validTo ?? "" })));
  }, [table.data]);
  const editable = canEdit && table.data?.table.platform === false;

  const save = useMutation({
    mutationFn: async () => {
      const built = entriesOf(rows);
      if ("invalidRow" in built) throw new Error(t("integrations.lookups.invalidRow", { row: built.invalidRow + 1 }));
      return unwrap(await client.PUT("/api/v1/lookup-tables/{key}/entries", { params: { path: { key: tableKey } }, headers: table.data?.etag ? { "If-Match": table.data.etag } : {}, body: { entries: built.entries } }));
    },
    meta: { silent: true },
    onSuccess: async (summary) => {
      setError(null);
      toast.success(t("integrations.lookups.saved", { count: Number(summary.entryCount) }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.outputs.lookupTables() });
      await queryClient.invalidateQueries({ queryKey: queryKeys.outputs.lookupTable(tableKey) });
    },
    onError: (failure) => setError(isApiError(failure) ? (Object.entries(failure.fieldErrors).map(([f, m]) => `${f}: ${m.join(" ")}`).join(" ") || failure.problem?.detail || failure.title) : failure instanceof Error ? failure.message : String(failure)),
  });

  const set = (i: number, patch: Partial<EntryRow>) => setRows((current) => current.map((row, j) => (j === i ? { ...row, ...patch } : row)));

  return (
    <Card data-testid="lookup-editor">
      <CardHeader className="flex flex-row items-start justify-between gap-4">
        <div className="grid gap-1.5">
          <CardTitle className="flex items-center gap-2">
            <Button variant="ghost" size="sm" onClick={onBack}>
              <ArrowLeft aria-hidden />
              {t("integrations.back")}
            </Button>
            <span className="font-mono">{tableKey}</span>
          </CardTitle>
          <CardDescription>{table.data?.table.description ?? t("integrations.lookups.entriesDescription")}</CardDescription>
        </div>
        {editable && (
          <Button onClick={() => save.mutate()} disabled={save.isPending} data-testid="lookup-save">
            {save.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Save aria-hidden />}
            {t("integrations.save")}
          </Button>
        )}
      </CardHeader>
      <CardContent className="grid gap-3">
        {table.isPending ? (
          <Skeleton className="h-32" />
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("integrations.lookups.columns.from")}</TableHead>
                <TableHead>{t("integrations.lookups.columns.to")}</TableHead>
                <TableHead>{t("integrations.lookups.columns.validFrom")}</TableHead>
                <TableHead>{t("integrations.lookups.columns.validTo")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((row, i) => (
                <TableRow key={i} data-testid={`lookup-row-${i}`}>
                  {(["from", "to", "validFrom", "validTo"] as const).map((field) => (
                    <TableCell key={field} className="p-1">
                      <Input value={row[field]} onChange={(event) => set(i, { [field]: event.target.value })} readOnly={!editable} className="h-8 font-mono text-xs" placeholder={field.startsWith("valid") ? "yyyy-MM-dd" : undefined} aria-label={t(`integrations.lookups.columns.${field}`)} data-testid={`lookup-${field}-${i}`} />
                    </TableCell>
                  ))}
                  <TableCell className="p-1">
                    {editable && (
                      <Button type="button" size="icon" variant="ghost" className="size-7" onClick={() => setRows((current) => current.filter((_, j) => j !== i))} aria-label={t("integrations.lookups.removeRow")}>
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
          <Button type="button" variant="outline" size="sm" className="w-fit" onClick={() => setRows((current) => [...current, { from: "", to: "", validFrom: "", validTo: "" }])} data-testid="lookup-add-row">
            <Plus aria-hidden />
            {t("integrations.lookups.addRow")}
          </Button>
        )}
        {error && (
          <p className="text-sm text-destructive" role="alert">
            {error}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
