import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useNavigate } from "@tanstack/react-router";
import { toast } from "sonner";
import { Plus } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger, Form, FormControl, FormDescription, FormField, FormItem, FormLabel, FormMessage, Input } from "@docproc/ui";
import { isApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";

type CreateTenantRequest = components["schemas"]["CreateTenantRequest"];

/** `name` → `slug` the way the API wants it: lowercase letters, digits and single dashes, at most 64 characters. */
export function slugify(name: string): string {
  return name
    .normalize("NFKD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .replace(/-{2,}/g, "-")
    .slice(0, 64)
    .replace(/-+$/g, "");
}

function isTimeZone(value: string): boolean {
  try {
    new Intl.DateTimeFormat("en", { timeZone: value });
    return true;
  } catch {
    return false;
  }
}

const schema = z.object({
  name: z.string().trim().min(1, "required").max(200),
  slug: z.string().trim().regex(/^[a-z0-9]+(-[a-z0-9]+)*$/, "slug").max(64),
  legalName: z.string().trim().min(1, "required").max(300),
  vatId: z.string().trim().max(32).optional(),
  defaultCurrency: z.string().trim().toUpperCase().regex(/^[A-Z]{3}$/, "currency"),
  countryCode: z.string().trim().toUpperCase().regex(/^[A-Z]{2}$/, "country"),
  timeZone: z.string().trim().min(1, "required").refine(isTimeZone, "timeZone"),
});

type FormValues = z.infer<typeof schema>;

export function toCreateTenantRequest(values: FormValues): CreateTenantRequest {
  return {
    name: values.name,
    slug: values.slug,
    legalName: values.legalName,
    vatId: values.vatId ? values.vatId : null,
    defaultCurrency: values.defaultCurrency,
    countryCode: values.countryCode,
    timeZone: values.timeZone,
  };
}

/** "New tenant": the identity fields of `POST /tenants`; the slug follows the name until it is edited by hand. */
export function TenantFormDialog() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [slugTouched, setSlugTouched] = useState(false);
  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { name: "", slug: "", legalName: "", vatId: "", defaultCurrency: "EUR", countryCode: "DE", timeZone: "Europe/Berlin" },
  });

  const mutation = useMutation({
    mutationFn: async (values: FormValues) => unwrap(await client.POST("/api/v1/tenants", { body: toCreateTenantRequest(values) })),
    meta: { silent: true },
    onSuccess: async (created) => {
      toast.success(t("tenants.created", { name: created.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.tenants.all });
      form.reset();
      setSlugTouched(false);
      setOpen(false);
      void navigate({ to: "/tenants/$tenantId", params: { tenantId: created.id } });
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) {
          form.setError(field as keyof FormValues, { message: messages.join(" ") });
        }
        return;
      }
      if (isApiError(error) && error.isConflict) {
        form.setError("slug", { message: error.problem?.detail ?? error.title });
        return;
      }
      toast.error(isApiError(error) ? error.title : t("tenants.createFailed"));
    },
  });

  const message = (code: string | undefined) => (code && ["required", "slug", "currency", "country", "timeZone"].includes(code) ? t(`tenants.validation.${code}`) : code);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button size="sm">
          <Plus aria-hidden />
          {t("tenants.create")}
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t("tenants.createTitle")}</DialogTitle>
          <DialogDescription>{t("tenants.createDescription")}</DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={form.handleSubmit((values) => mutation.mutate(values))} className="grid gap-4" noValidate>
            <FormField
              control={form.control}
              name="name"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("tenants.name")}</FormLabel>
                  <FormControl>
                    <Input
                      {...field}
                      onChange={(event) => {
                        field.onChange(event);
                        if (!slugTouched) form.setValue("slug", slugify(event.target.value), { shouldValidate: form.formState.isSubmitted });
                      }}
                    />
                  </FormControl>
                  <FormMessage>{message(form.formState.errors.name?.message)}</FormMessage>
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="slug"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("tenants.slug")}</FormLabel>
                  <FormControl>
                    <Input
                      {...field}
                      className="font-mono"
                      onChange={(event) => {
                        setSlugTouched(true);
                        field.onChange(event);
                      }}
                    />
                  </FormControl>
                  <FormDescription>{t("tenants.slugHint")}</FormDescription>
                  <FormMessage>{message(form.formState.errors.slug?.message)}</FormMessage>
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="legalName"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("tenants.legalName")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormMessage>{message(form.formState.errors.legalName?.message)}</FormMessage>
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="vatId"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("tenants.vatId")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />
            <div className="grid gap-4 sm:grid-cols-3">
              <FormField
                control={form.control}
                name="defaultCurrency"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("tenants.currency")}</FormLabel>
                    <FormControl>
                      <Input {...field} maxLength={3} className="uppercase" />
                    </FormControl>
                    <FormMessage>{message(form.formState.errors.defaultCurrency?.message)}</FormMessage>
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="countryCode"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("tenants.country")}</FormLabel>
                    <FormControl>
                      <Input {...field} maxLength={2} className="uppercase" />
                    </FormControl>
                    <FormMessage>{message(form.formState.errors.countryCode?.message)}</FormMessage>
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="timeZone"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("tenants.timeZone")}</FormLabel>
                    <FormControl>
                      <Input {...field} list="tenant-time-zones" />
                    </FormControl>
                    <FormMessage>{message(form.formState.errors.timeZone?.message)}</FormMessage>
                  </FormItem>
                )}
              />
            </div>
            <datalist id="tenant-time-zones">
              {timeZoneSuggestions.map((zone) => (
                <option key={zone} value={zone} />
              ))}
            </datalist>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setOpen(false)}>
                {t("common:dialog.cancel")}
              </Button>
              <Button type="submit" disabled={mutation.isPending} aria-busy={mutation.isPending}>
                {t("tenants.submit")}
              </Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  );
}

const timeZoneSuggestions: string[] = (() => {
  try {
    return Intl.supportedValuesOf("timeZone");
  } catch {
    return ["Europe/Berlin", "Europe/Vienna", "Europe/Zagreb", "Europe/London", "UTC"];
  }
})();
