import { useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import { Plus, Trash2 } from "lucide-react";
import type { components } from "@docproc/api-client";
import {
  Button,
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
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@docproc/ui";
import { ConfirmDialog, DataTable, createDataTableColumnHelper, formatDateTime, isApiError, queryKeys, unwrap, useApi, type DataTableColumns } from "@docproc/app-shell";
import { RowCount } from "@/components/LoadMore";
import { translated } from "@/lib/validation";

type Member = components["schemas"]["MemberResponse"];

/** `TenantRole` as the API spells it. */
export const tenantRoles = ["TenantAdmin", "Reviewer", "Viewer"] as const;

const schema = z.object({
  userId: z.string().trim().min(1, translated("tenants.validation.required")).max(200),
  email: z.string().trim().max(320),
  displayName: z.string().trim().max(200),
  role: z.enum(tenantRoles),
});
type FormValues = z.infer<typeof schema>;

const HIDDEN_COLUMNS = { userId: false };
const helper = createDataTableColumnHelper<Member>();
const EMPTY: Member[] = [];

function AddMemberDialog() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const form = useForm<FormValues>({ resolver: zodResolver(schema), defaultValues: { userId: "", email: "", displayName: "", role: "Reviewer" } });
  const mutation = useMutation({
    mutationFn: async (values: FormValues) =>
      unwrap(await client.POST("/api/v1/members", { body: { userId: values.userId, email: values.email || null, displayName: values.displayName || null, role: values.role } })),
    meta: { silent: true },
    onSuccess: async (created) => {
      toast.success(t("members.added", { name: created.displayName ?? created.userId }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.members.all });
      form.reset();
      setOpen(false);
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) form.setError(field as keyof FormValues, { message: messages.join(" ") });
        return;
      }
      if (isApiError(error) && error.isConflict) {
        form.setError("userId", { message: error.problem?.detail ?? error.title });
        return;
      }
      toast.error(isApiError(error) ? error.title : t("members.addFailed"));
    },
  });

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button>
          <Plus aria-hidden />
          {t("members.add")}
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t("members.addTitle")}</DialogTitle>
          <DialogDescription>{t("members.addDescription")}</DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={form.handleSubmit((values) => mutation.mutate(values))} className="grid gap-4" noValidate>
            <FormField
              control={form.control}
              name="userId"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("members.userId")}</FormLabel>
                  <FormControl>
                    <Input {...field} className="font-mono" />
                  </FormControl>
                  <FormDescription>{t("members.userIdHint")}</FormDescription>
                  <FormMessage />
                </FormItem>
              )}
            />
            <div className="grid gap-4 sm:grid-cols-2">
              <FormField
                control={form.control}
                name="displayName"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("members.displayName")}</FormLabel>
                    <FormControl>
                      <Input {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="email"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("members.email")}</FormLabel>
                    <FormControl>
                      <Input type="email" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </div>
            <FormField
              control={form.control}
              name="role"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("members.role")}</FormLabel>
                  <Select value={field.value} onValueChange={field.onChange}>
                    <FormControl>
                      <SelectTrigger className="w-full">
                        <SelectValue />
                      </SelectTrigger>
                    </FormControl>
                    <SelectContent>
                      {tenantRoles.map((role) => (
                        <SelectItem key={role} value={role}>
                          {t(`common:roles.${role}`)}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                  <FormMessage />
                </FormItem>
              )}
            />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setOpen(false)}>
                {t("common:dialog.cancel")}
              </Button>
              <Button type="submit" disabled={mutation.isPending} aria-busy={mutation.isPending}>
                {t("members.add")}
              </Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  );
}

/** Members of the tenant (`/members`, tenant scope): list, add, change role, remove. The API keeps the last admin. */
export function MembersTab() {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [removing, setRemoving] = useState<Member | null>(null);

  const members = useQuery({ queryKey: queryKeys.members.list(), queryFn: async () => unwrap(await client.GET("/api/v1/members")) });

  const changeRole = useMutation({
    mutationFn: async ({ member, role }: { member: Member; role: string }) =>
      unwrap(await client.PATCH("/api/v1/members/{userId}", { params: { path: { userId: member.userId } }, body: { role } })),
    onSuccess: async (updated) => {
      toast.success(t("members.roleChanged", { name: updated.displayName ?? updated.userId, role: t(`common:roles.${updated.role}`, { defaultValue: updated.role }) }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.members.all });
    },
  });

  const remove = useMutation({
    mutationFn: async (member: Member) => unwrap(await client.DELETE("/api/v1/members/{userId}", { params: { path: { userId: member.userId } } })),
    onSuccess: async (_data, member) => {
      toast.success(t("members.removed", { name: member.displayName ?? member.userId }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.members.all });
    },
  });

  const columns = useMemo<DataTableColumns<Member>>(
    () =>
      helper.columns([
        helper.accessor("displayName", { header: t("members.displayName"), cell: (info) => <span className="font-medium">{info.getValue() ?? "—"}</span> }),
        helper.accessor("email", { header: t("members.email"), cell: (info) => info.getValue() ?? "—" }),
        helper.accessor("userId", { header: t("members.userId"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        helper.accessor("role", {
          header: t("members.role"),
          cell: (info) => {
            const member = info.row.original;
            return (
              <Select value={member.role} onValueChange={(role) => changeRole.mutate({ member, role })} disabled={changeRole.isPending}>
                <SelectTrigger size="sm" className="w-40" aria-label={t("members.roleOf", { name: member.displayName ?? member.userId })}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {tenantRoles.map((role) => (
                    <SelectItem key={role} value={role}>
                      {t(`common:roles.${role}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            );
          },
        }),
        helper.accessor("createdAt", { header: t("members.since"), sortFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDateTime(info.getValue(), i18n.language)}</span> }),
        helper.display({
          id: "actions",
          header: () => <span className="sr-only">{t("members.actions")}</span>,
          cell: (info) => (
            <div className="flex justify-end">
              <Button variant="ghost" size="icon-sm" onClick={() => setRemoving(info.row.original)} aria-label={t("members.removeAria", { name: info.row.original.displayName ?? info.row.original.userId })}>
                <Trash2 aria-hidden />
              </Button>
            </div>
          ),
        }),
      ]),
    [t, i18n.language, changeRole],
  );

  return (
    <>
      <DataTable
        columns={columns}
        data={members.data ?? EMPTY}
        getRowId={(row) => row.userId}
        isLoading={members.isPending} query={members}
        actions={<AddMemberDialog />}
        // The identity provider's subject is for support, not for reading: one click away in Columns.
        initialColumnVisibility={HIDDEN_COLUMNS}
        enableColumnVisibility
        footer={<RowCount count={members.data?.length} />}
        caption={t("members.caption")}
        emptyTitle={t("members.empty")}
        emptyDescription={t("members.emptyDescription")}
      />
      {removing && (
        <ConfirmDialog
          open
          onOpenChange={(open) => !open && setRemoving(null)}
          destructive
          title={t("members.removeTitle", { name: removing.displayName ?? removing.userId })}
          description={t("members.removeDescription")}
          confirmLabel={t("members.remove")}
          isPending={remove.isPending}
          onConfirm={() => remove.mutateAsync(removing)}
        />
      )}
    </>
  );
}
