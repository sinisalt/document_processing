import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Plus, Trash2 } from "lucide-react";
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Form,
  FormControl,
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
import { ApiError, ConfirmDialog, DataTable, createDataTableColumnHelper, formatDateTime, queryKeys, unwrap, useApi, useAuth } from "@docproc/app-shell";
import { EMPTY_ARRAY, type Member } from "@/lib/queries";

/** The roles a tenant may grant (`TenantRole`); platform roles are not assignable here. */
export const tenantRoles = ["TenantAdmin", "Reviewer", "Viewer"] as const;

const schema = z.object({
  userId: z.string().trim().min(1),
  email: z.string().trim().email().or(z.literal("")),
  displayName: z.string().trim(),
  role: z.enum(tenantRoles),
});
type MemberForm = z.infer<typeof schema>;

const helper = createDataTableColumnHelper<Member>();

/** Members: list for everyone with Read; add / change role / remove for tenant admins. */
export function MembersTab({ canEdit }: { canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const [addOpen, setAddOpen] = useState(false);
  const [removing, setRemoving] = useState<Member | null>(null);

  const members = useQuery({
    queryKey: queryKeys.members.list(),
    queryFn: async () => unwrap(await client.GET("/api/v1/members")),
  });
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: queryKeys.members.all });

  const form = useForm<MemberForm>({ resolver: zodResolver(schema), defaultValues: { userId: "", email: "", displayName: "", role: "Reviewer" } });
  const add = useMutation({
    mutationFn: async (values: MemberForm) =>
      unwrap(await client.POST("/api/v1/members", { body: { userId: values.userId, email: values.email || null, displayName: values.displayName || null, role: values.role } })),
    onSuccess: (member) => {
      toast.success(t("settings.members.added", { name: member.displayName ?? member.userId }));
      invalidate();
      form.reset();
      setAddOpen(false);
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) {
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          const name = (key.charAt(0).toLowerCase() + key.slice(1)) as keyof MemberForm;
          if (name in form.getValues()) form.setError(name, { type: "server", message: messages.join(" ") });
        }
      }
    },
  });
  const changeRole = useMutation({
    mutationFn: async ({ userId, role }: { userId: string; role: string }) => unwrap(await client.PATCH("/api/v1/members/{userId}", { params: { path: { userId } }, body: { role } })),
    onSuccess: (member) => {
      toast.success(t("settings.members.roleChanged", { name: member.displayName ?? member.userId, role: t(`common:roles.${member.role}`, { defaultValue: member.role }) }));
      invalidate();
    },
    onError: invalidate,
  });
  const remove = useMutation({
    mutationFn: async (userId: string) => unwrap(await client.DELETE("/api/v1/members/{userId}", { params: { path: { userId } } })),
    onSuccess: () => {
      toast.success(t("settings.members.removed"));
      invalidate();
    },
  });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("displayName", { header: t("settings.members.columns.name"), cell: (info) => info.getValue() ?? <span className="text-muted-foreground">—</span> }),
        helper.accessor("email", { header: t("settings.members.columns.email"), cell: (info) => info.getValue() ?? "—" }),
        helper.accessor("userId", { header: t("settings.members.columns.userId"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        helper.accessor("role", {
          header: t("settings.members.columns.role"),
          cell: ({ row, getValue }) =>
            canEdit && row.original.userId !== user?.subject ? (
              <Select value={getValue()} onValueChange={(role) => changeRole.mutate({ userId: row.original.userId, role })} disabled={changeRole.isPending}>
                <SelectTrigger size="sm" className="w-40" aria-label={t("settings.members.roleFor", { name: row.original.displayName ?? row.original.userId })}>
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
            ) : (
              t(`common:roles.${getValue()}`, { defaultValue: getValue() })
            ),
        }),
        helper.accessor("createdAt", { header: t("settings.members.columns.since"), cell: (info) => formatDateTime(info.getValue(), i18n.language), sortFn: "datetime" }),
        ...(canEdit
          ? [
              helper.display({
                id: "actions",
                header: "",
                cell: ({ row }) =>
                  row.original.userId !== user?.subject ? (
                    <Button size="xs" variant="ghost" onClick={() => setRemoving(row.original)} aria-label={t("settings.members.removeLabel", { name: row.original.displayName ?? row.original.userId })}>
                      <Trash2 aria-hidden />
                      {t("settings.members.remove")}
                    </Button>
                  ) : null,
              }),
            ]
          : []),
      ]),
    [t, i18n.language, canEdit, user?.subject, changeRole],
  );

  return (
    <div className="flex flex-col gap-4">
      {canEdit && (
        <div className="flex justify-end">
          <Dialog open={addOpen} onOpenChange={setAddOpen}>
            <Button onClick={() => setAddOpen(true)}>
              <Plus aria-hidden />
              {t("settings.members.add")}
            </Button>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>{t("settings.members.addTitle")}</DialogTitle>
                <DialogDescription>{t("settings.members.addDescription")}</DialogDescription>
              </DialogHeader>
              <Form {...form}>
                <form id="add-member-form" onSubmit={form.handleSubmit((values) => add.mutate(values))} className="flex flex-col gap-4">
                  <FormField
                    control={form.control}
                    name="userId"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("settings.members.fields.userId")}</FormLabel>
                        <FormControl>
                          <Input {...field} autoFocus placeholder={t("settings.members.fields.userIdPlaceholder")} />
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
                        <FormLabel>{t("settings.members.fields.email")}</FormLabel>
                        <FormControl>
                          <Input type="email" {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="displayName"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("settings.members.fields.displayName")}</FormLabel>
                        <FormControl>
                          <Input {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="role"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("settings.members.fields.role")}</FormLabel>
                        <Select value={field.value} onValueChange={field.onChange}>
                          <FormControl>
                            <SelectTrigger>
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
                </form>
              </Form>
              <DialogFooter>
                <Button type="button" variant="outline" onClick={() => setAddOpen(false)}>
                  {t("common:dialog.cancel")}
                </Button>
                <Button type="submit" form="add-member-form" disabled={add.isPending} aria-busy={add.isPending}>
                  {add.isPending && <Loader2 className="animate-spin" aria-hidden />}
                  {t("settings.members.add")}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      )}
      <DataTable columns={columns} data={members.data ?? EMPTY_ARRAY} getRowId={(row) => row.userId} isLoading={members.isPending} caption={t("settings.members.caption")} emptyTitle={t("settings.members.empty")} />
      <ConfirmDialog
        open={removing !== null}
        onOpenChange={(open) => !open && setRemoving(null)}
        title={t("settings.members.removeTitle")}
        description={t("settings.members.removeDescription", { name: removing?.displayName ?? removing?.userId })}
        confirmLabel={t("settings.members.remove")}
        destructive
        onConfirm={() => (removing ? remove.mutateAsync(removing.userId).then(() => undefined) : undefined)}
        isPending={remove.isPending}
      />
    </div>
  );
}
