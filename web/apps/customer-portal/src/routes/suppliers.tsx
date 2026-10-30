import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Pencil, Plus, Search } from "lucide-react";
import {
  Badge,
  Button,
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
  Skeleton,
  Switch,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
} from "@docproc/ui";
import { ApiError, DataTable, EmptyState, PageHeader, Policies, StatusBadge, createDataTableColumnHelper, formatDate, formatMoney, queryKeys, relativeTime, unwrap, useApi, useAuth, type DocumentStatus } from "@docproc/app-shell";
import { EMPTY_ARRAY, useSuppliers, type Supplier } from "@/lib/queries";

export const Route = createFileRoute("/suppliers")({ component: SuppliersPage });

const helper = createDataTableColumnHelper<Supplier>();

const supplierSchema = z.object({
  name: z.string().trim().min(1),
  vatId: z.string().trim(),
  taxId: z.string().trim(),
  defaultCurrency: z
    .string()
    .trim()
    .toUpperCase()
    .refine((value) => value === "" || /^[A-Z]{3}$/.test(value), { message: "currency" }),
  ibans: z.string(),
  emailDomains: z.string(),
  aliases: z.string(),
  alwaysReview: z.boolean(),
});
type SupplierForm = z.infer<typeof supplierSchema>;

/** Lists are typed one per line or comma-separated. */
export function splitList(text: string): string[] {
  return Array.from(new Set(text.split(/[\n,;]+/).map((s) => s.trim()).filter(Boolean)));
}

function toForm(supplier: Supplier | null): SupplierForm {
  return {
    name: supplier?.name ?? "",
    vatId: supplier?.vatId ?? "",
    taxId: supplier?.taxId ?? "",
    defaultCurrency: supplier?.defaultCurrency ?? "",
    ibans: supplier?.ibans.join("\n") ?? "",
    emailDomains: supplier?.emailDomains.join("\n") ?? "",
    aliases: supplier?.aliases.join("\n") ?? "",
    alwaysReview: supplier?.alwaysReview ?? false,
  };
}

function SuppliersPage() {
  const { t, i18n } = useTranslation();
  const { hasPolicy } = useAuth();
  const canManage = hasPolicy(Policies.TenantAdmin);
  const [draft, setDraft] = useState("");
  const [q, setQ] = useState<string | undefined>(undefined);
  const [editing, setEditing] = useState<Supplier | null | "new">(null);

  const list = useSuppliers({ q, limit: 50 });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("suppliers.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("vatId", { header: t("suppliers.columns.vatId"), cell: (info) => info.getValue() ?? "—" }),
        helper.accessor("defaultCurrency", { header: t("suppliers.columns.currency"), cell: (info) => info.getValue() ?? "—" }),
        helper.accessor((s) => s.ibans.length, { id: "ibans", header: t("suppliers.columns.ibans"), cell: (info) => info.getValue() }),
        helper.accessor((s) => s.emailDomains.join(", "), { id: "emailDomains", header: t("suppliers.columns.emailDomains"), cell: (info) => info.getValue() || "—" }),
        helper.accessor("alwaysReview", {
          header: t("suppliers.columns.alwaysReview"),
          cell: (info) => (info.getValue() ? <Badge variant="destructive">{t("suppliers.alwaysReviewBadge")}</Badge> : <span className="text-muted-foreground">—</span>),
        }),
        helper.accessor("updatedAt", { header: t("suppliers.columns.updated"), cell: (info) => relativeTime(info.getValue(), i18n.language), sortFn: "datetime" }),
        helper.display({
          id: "actions",
          header: "",
          cell: ({ row }) => (
            <Button size="xs" variant="ghost" onClick={() => setEditing(row.original)} aria-label={t("suppliers.openLabel", { name: row.original.name })}>
              <Pencil aria-hidden />
              {canManage ? t("suppliers.edit") : t("suppliers.view")}
            </Button>
          ),
        }),
      ]),
    [t, i18n.language, canManage],
  );

  return (
    <>
      <PageHeader
        title={t("suppliers.title")}
        description={t("suppliers.description")}
        actions={
          canManage ? (
            <Button onClick={() => setEditing("new")}>
              <Plus aria-hidden />
              {t("suppliers.create")}
            </Button>
          ) : undefined
        }
      />
      <DataTable
        columns={columns}
        data={list.items.length ? list.items : EMPTY_ARRAY}
        getRowId={(row) => row.id}
        isLoading={list.isLoading}
        caption={t("suppliers.caption")}
        onRowClick={(row) => setEditing(row)}
        emptyTitle={t("suppliers.emptyTitle")}
        emptyDescription={q ? t("suppliers.emptySearch", { q }) : t("suppliers.empty")}
        toolbar={
          <form
            role="search"
            className="flex items-center gap-2"
            onSubmit={(event) => {
              event.preventDefault();
              setQ(draft.trim() || undefined);
            }}
          >
            <div className="relative">
              <Search className="pointer-events-none absolute top-1/2 left-2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
              <Input value={draft} onChange={(event) => setDraft(event.target.value)} placeholder={t("suppliers.searchPlaceholder")} aria-label={t("suppliers.search")} className="w-64 pl-8" />
            </div>
            <Button type="submit" variant="secondary" size="sm">
              {t("suppliers.search")}
            </Button>
          </form>
        }
        footer={
          <>
            <span aria-live="polite">{t("common:table.rowCount", { count: list.items.length })}</span>
            {list.hasNextPage && (
              <Button variant="outline" size="sm" onClick={() => void list.fetchNextPage()} disabled={list.isFetchingNextPage}>
                {t("common:table.loadMore")}
              </Button>
            )}
          </>
        }
      />
      <SupplierSheet supplier={editing === "new" ? null : editing} open={editing !== null} onOpenChange={(open) => !open && setEditing(null)} canManage={canManage} />
    </>
  );
}

interface SupplierSheetProps {
  supplier: Supplier | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  canManage: boolean;
}

/** Create/edit sheet with the supplier's details and, for an existing supplier, its recent documents. */
function SupplierSheet({ supplier, open, onOpenChange, canManage }: SupplierSheetProps) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<SupplierForm>({ resolver: zodResolver(supplierSchema), defaultValues: toForm(supplier) });
  const [tab, setTab] = useState("details");

  useEffect(() => {
    form.reset(toForm(supplier));
    setTab("details");
  }, [supplier, open, form]);

  const activity = useQuery({
    queryKey: queryKeys.suppliers.activity(supplier?.id ?? ""),
    queryFn: async () => unwrap(await client.GET("/api/v1/suppliers/{id}/activity", { params: { path: { id: supplier!.id } } })),
    enabled: open && !!supplier && tab === "activity",
  });

  const save = useMutation({
    mutationFn: async (values: SupplierForm) => {
      const body = {
        name: values.name,
        vatId: values.vatId || null,
        taxId: values.taxId || null,
        defaultCurrency: values.defaultCurrency || null,
        ibans: splitList(values.ibans),
        emailDomains: splitList(values.emailDomains),
        aliases: splitList(values.aliases),
        alwaysReview: values.alwaysReview,
      };
      if (supplier) {
        // PATCH: an empty string clears a text field, a list replaces the stored list.
        return unwrap(await client.PATCH("/api/v1/suppliers/{id}", { params: { path: { id: supplier.id } }, body: { ...body, vatId: values.vatId, taxId: values.taxId, defaultCurrency: values.defaultCurrency } }));
      }
      return unwrap(await client.POST("/api/v1/suppliers", { body }));
    },
    onSuccess: (saved) => {
      toast.success(supplier ? t("suppliers.updated", { name: saved.name }) : t("suppliers.created", { name: saved.name }));
      void queryClient.invalidateQueries({ queryKey: queryKeys.suppliers.all });
      onOpenChange(false);
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) {
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          const name = (key.charAt(0).toLowerCase() + key.slice(1)) as keyof SupplierForm;
          if (name in form.getValues()) form.setError(name, { type: "server", message: messages.join(" ") });
        }
      }
    },
  });

  const readOnly = !canManage;

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="flex w-full flex-col gap-0 overflow-hidden sm:max-w-xl">
        <SheetHeader>
          <SheetTitle>{supplier ? supplier.name : t("suppliers.createTitle")}</SheetTitle>
          <SheetDescription>{supplier ? t("suppliers.editDescription") : t("suppliers.createDescription")}</SheetDescription>
        </SheetHeader>
        <Tabs value={tab} onValueChange={setTab} className="flex min-h-0 flex-1 flex-col px-4">
          <TabsList>
            <TabsTrigger value="details">{t("suppliers.tabs.details")}</TabsTrigger>
            <TabsTrigger value="activity" disabled={!supplier}>
              {t("suppliers.tabs.activity")}
            </TabsTrigger>
          </TabsList>
          <TabsContent value="details" className="min-h-0 flex-1 overflow-y-auto py-4">
            <Form {...form}>
              <form id="supplier-form" onSubmit={form.handleSubmit((values) => save.mutate(values))} className="flex flex-col gap-4">
                <FormField
                  control={form.control}
                  name="name"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("suppliers.fields.name")}</FormLabel>
                      <FormControl>
                        <Input {...field} readOnly={readOnly} autoFocus={!supplier} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <FormField
                    control={form.control}
                    name="vatId"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("suppliers.fields.vatId")}</FormLabel>
                        <FormControl>
                          <Input {...field} readOnly={readOnly} placeholder="DE123456789" />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="taxId"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("suppliers.fields.taxId")}</FormLabel>
                        <FormControl>
                          <Input {...field} readOnly={readOnly} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                </div>
                <FormField
                  control={form.control}
                  name="defaultCurrency"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("suppliers.fields.defaultCurrency")}</FormLabel>
                      <FormControl>
                        <Input {...field} readOnly={readOnly} placeholder="EUR" maxLength={3} className="w-24 uppercase" />
                      </FormControl>
                      <FormDescription>{t("suppliers.fields.defaultCurrencyHint")}</FormDescription>
                      <FormMessage>{form.formState.errors.defaultCurrency ? t("suppliers.fields.currencyInvalid") : undefined}</FormMessage>
                    </FormItem>
                  )}
                />
                {(["ibans", "emailDomains", "aliases"] as const).map((name) => (
                  <FormField
                    key={name}
                    control={form.control}
                    name={name}
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t(`suppliers.fields.${name}`)}</FormLabel>
                        <FormControl>
                          <Textarea {...field} readOnly={readOnly} rows={3} />
                        </FormControl>
                        <FormDescription>{t("suppliers.fields.listHint")}</FormDescription>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                ))}
                <FormField
                  control={form.control}
                  name="alwaysReview"
                  render={({ field }) => (
                    <FormItem className="flex flex-row items-center justify-between rounded-lg border p-3">
                      <div className="space-y-0.5">
                        <FormLabel>{t("suppliers.fields.alwaysReview")}</FormLabel>
                        <FormDescription>{t("suppliers.fields.alwaysReviewHint")}</FormDescription>
                      </div>
                      <FormControl>
                        <Switch checked={field.value} onCheckedChange={field.onChange} disabled={readOnly} aria-label={t("suppliers.fields.alwaysReview")} />
                      </FormControl>
                    </FormItem>
                  )}
                />
              </form>
            </Form>
          </TabsContent>
          <TabsContent value="activity" className="min-h-0 flex-1 overflow-y-auto py-4">
            {activity.isPending ? (
              <div className="flex flex-col gap-2" aria-busy>
                {Array.from({ length: 3 }, (_, i) => (
                  <Skeleton key={i} className="h-10" />
                ))}
              </div>
            ) : !activity.data || activity.data.length === 0 ? (
              <EmptyState title={t("suppliers.activityEmpty")} />
            ) : (
              <ul className="divide-y rounded-md border text-sm" aria-label={t("suppliers.tabs.activity")}>
                {activity.data.map((item) => (
                  <li key={item.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2">
                    <Link to="/documents/$documentId" params={{ documentId: item.id }} className="font-medium underline-offset-2 hover:underline">
                      {item.documentNumber ?? t("inbox.noNumber")}
                    </Link>
                    <span className="text-muted-foreground">{t(`documentType.${item.type}`, { defaultValue: item.type })}</span>
                    <span>{formatDate(item.issueDate, i18n.language)}</span>
                    <span className="tabular-nums">{formatMoney(item.totalGross, item.currency, i18n.language)}</span>
                    <StatusBadge status={item.status as DocumentStatus} />
                  </li>
                ))}
              </ul>
            )}
          </TabsContent>
        </Tabs>
        <SheetFooter className="border-t">
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            {canManage ? t("common:dialog.cancel") : t("common:actions.close")}
          </Button>
          {canManage && (
            <Button type="submit" form="supplier-form" disabled={save.isPending} aria-busy={save.isPending}>
              {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
              {supplier ? t("suppliers.save") : t("suppliers.create")}
            </Button>
          )}
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
