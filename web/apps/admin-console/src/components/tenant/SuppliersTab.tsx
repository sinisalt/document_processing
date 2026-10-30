import { useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import { Plus, Search, Trash2 } from "lucide-react";
import type { components } from "@docproc/api-client";
import {
  Badge,
  Button,
  Checkbox,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  Label,
} from "@docproc/ui";
import { ConfirmDialog, DataTable, createDataTableColumnHelper, isApiError, queryKeys, unwrap, useApi, useCursorList, type DataTableColumns } from "@docproc/app-shell";
import { LoadMore } from "../LoadMore";

type Supplier = components["schemas"]["SupplierResponse"];

const schema = z.object({
  name: z.string().trim().min(1, "required").max(300),
  vatId: z.string().trim().max(32),
  taxId: z.string().trim().max(32),
  defaultCurrency: z.string().trim().toUpperCase().refine((v) => v === "" || /^[A-Z]{3}$/.test(v), "currency"),
  emailDomains: z.string().trim(),
  aliases: z.string().trim(),
  alwaysReview: z.boolean(),
});
type FormValues = z.infer<typeof schema>;

function splitList(value: string): string[] {
  return value
    .split(/[,\n]/)
    .map((v) => v.trim())
    .filter(Boolean);
}

const helper = createDataTableColumnHelper<Supplier>();
const PAGE_SIZE = 50;

function CreateSupplierDialog() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { name: "", vatId: "", taxId: "", defaultCurrency: "", emailDomains: "", aliases: "", alwaysReview: false },
  });
  const mutation = useMutation({
    mutationFn: async (values: FormValues) =>
      unwrap(
        await client.POST("/api/v1/suppliers", {
          body: {
            name: values.name,
            vatId: values.vatId || null,
            taxId: values.taxId || null,
            defaultCurrency: values.defaultCurrency || null,
            emailDomains: splitList(values.emailDomains),
            aliases: splitList(values.aliases),
            ibans: null,
            alwaysReview: values.alwaysReview,
          },
        }),
      ),
    meta: { silent: true },
    onSuccess: async (created) => {
      toast.success(t("suppliers.created", { name: created.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.suppliers.all });
      form.reset();
      setOpen(false);
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) form.setError(field as keyof FormValues, { message: messages.join(" ") });
        return;
      }
      toast.error(isApiError(error) ? error.title : t("suppliers.createFailed"), { description: isApiError(error) ? error.problem?.detail ?? undefined : undefined });
    },
  });
  const message = (code: string | undefined) => (code === "required" ? t("tenants.validation.required") : code === "currency" ? t("tenants.validation.currency") : code);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button size="sm">
          <Plus aria-hidden />
          {t("suppliers.create")}
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t("suppliers.createTitle")}</DialogTitle>
          <DialogDescription>{t("suppliers.createDescription")}</DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={form.handleSubmit((values) => mutation.mutate(values))} className="grid gap-4" noValidate>
            <FormField
              control={form.control}
              name="name"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("suppliers.name")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormMessage>{message(form.formState.errors.name?.message)}</FormMessage>
                </FormItem>
              )}
            />
            <div className="grid gap-4 sm:grid-cols-3">
              <FormField
                control={form.control}
                name="vatId"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("suppliers.vatId")}</FormLabel>
                    <FormControl>
                      <Input {...field} />
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
                    <FormLabel>{t("suppliers.taxId")}</FormLabel>
                    <FormControl>
                      <Input {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="defaultCurrency"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("suppliers.currency")}</FormLabel>
                    <FormControl>
                      <Input {...field} maxLength={3} className="uppercase" />
                    </FormControl>
                    <FormMessage>{message(form.formState.errors.defaultCurrency?.message)}</FormMessage>
                  </FormItem>
                )}
              />
            </div>
            <FormField
              control={form.control}
              name="emailDomains"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("suppliers.emailDomains")}</FormLabel>
                  <FormControl>
                    <Input {...field} placeholder="supplier.example" />
                  </FormControl>
                  <FormDescription>{t("suppliers.listHint")}</FormDescription>
                  <FormMessage />
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="aliases"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("suppliers.aliases")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormDescription>{t("suppliers.listHint")}</FormDescription>
                  <FormMessage />
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="alwaysReview"
              render={({ field }) => (
                <FormItem className="flex items-center gap-2">
                  <FormControl>
                    <Checkbox checked={field.value} onCheckedChange={(checked) => field.onChange(checked === true)} />
                  </FormControl>
                  <FormLabel className="font-normal">{t("suppliers.alwaysReview")}</FormLabel>
                </FormItem>
              )}
            />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setOpen(false)}>
                {t("common:dialog.cancel")}
              </Button>
              <Button type="submit" disabled={mutation.isPending} aria-busy={mutation.isPending}>
                {t("suppliers.create")}
              </Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  );
}

/** The tenant's supplier registry (`/suppliers`, tenant scope): search, cursor pages, create, delete. */
export function SuppliersTab() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [search, setSearch] = useState("");
  const [deleting, setDeleting] = useState<Supplier | null>(null);
  const q = search.trim() === "" ? undefined : search.trim();

  const suppliers = useCursorList<Supplier>({
    queryKey: [...queryKeys.suppliers.list({ q }), "pages"],
    queryFn: async (cursor) => unwrap(await client.GET("/api/v1/suppliers", { params: { query: { q, limit: PAGE_SIZE, cursor: cursor ?? undefined } } })),
  });

  const remove = useMutation({
    mutationFn: async (supplier: Supplier) => unwrap(await client.DELETE("/api/v1/suppliers/{id}", { params: { path: { id: supplier.id } } })),
    onSuccess: async (_data, supplier) => {
      toast.success(t("suppliers.deleted", { name: supplier.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.suppliers.all });
    },
  });

  const columns = useMemo<DataTableColumns<Supplier>>(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("suppliers.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("vatId", { header: t("suppliers.vatId"), cell: (info) => <span className="font-mono text-xs">{info.getValue() ?? "—"}</span> }),
        helper.accessor("defaultCurrency", { header: t("suppliers.currency"), cell: (info) => info.getValue() ?? "—" }),
        helper.accessor("emailDomains", { header: t("suppliers.emailDomains"), enableSorting: false, cell: (info) => (info.getValue().length ? info.getValue().join(", ") : "—") }),
        helper.accessor("aliases", { header: t("suppliers.aliases"), enableSorting: false, cell: (info) => (info.getValue().length ? info.getValue().join(", ") : "—") }),
        helper.accessor("alwaysReview", { header: t("suppliers.alwaysReview"), cell: (info) => (info.getValue() ? <Badge variant="destructive">{t("yes")}</Badge> : <span className="text-muted-foreground">{t("no")}</span>) }),
        helper.display({
          id: "actions",
          header: () => <span className="sr-only">{t("suppliers.actions")}</span>,
          cell: (info) => (
            <div className="flex justify-end">
              <Button variant="ghost" size="icon-sm" onClick={() => setDeleting(info.row.original)} aria-label={t("suppliers.deleteAria", { name: info.row.original.name })}>
                <Trash2 aria-hidden />
              </Button>
            </div>
          ),
        }),
      ]),
    [t],
  );

  return (
    <>
      <DataTable
        columns={columns}
        data={suppliers.items}
        getRowId={(row) => row.id}
        isLoading={suppliers.isLoading}
        toolbar={
          <>
            <div className="relative">
              <Label htmlFor="supplier-search" className="sr-only">
                {t("suppliers.search")}
              </Label>
              <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
              <Input id="supplier-search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder={t("suppliers.search")} className="w-64 pl-8" />
            </div>
            <CreateSupplierDialog />
          </>
        }
        footer={<LoadMore list={suppliers} />}
        caption={t("suppliers.caption")}
        emptyTitle={t("suppliers.empty")}
        emptyDescription={t("suppliers.emptyDescription")}
      />
      {deleting && (
        <ConfirmDialog
          open
          onOpenChange={(open) => !open && setDeleting(null)}
          destructive
          title={t("suppliers.deleteTitle", { name: deleting.name })}
          description={t("suppliers.deleteDescription")}
          confirmLabel={t("suppliers.delete")}
          isPending={remove.isPending}
          onConfirm={() => remove.mutateAsync(deleting)}
        />
      )}
    </>
  );
}
