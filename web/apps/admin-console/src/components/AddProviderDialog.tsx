import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient, type QueryKey } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import { Plus } from "lucide-react";
import type { ApiClient, components } from "@docproc/api-client";
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
import { isApiError, unwrap, useApi } from "@docproc/app-shell";
import { translated } from "@/lib/validation";
import type { ProviderScope } from "./ProviderTable";

type CreateLlmProviderRequest = components["schemas"]["CreateLlmProviderRequest"];

/** `LlmProviderKind` without Replay (the API refuses it) and `DataResidency`, as LlmAdminEndpoints validates them. */
export const providerKinds = ["Anthropic", "AnthropicFoundry", "OpenAi", "AzureOpenAi", "OpenAiCompatible"] as const;
export const dataResidencies = ["Eu", "Us", "TenantOwned"] as const;
/** `LlmStage`: the stages a provider needs a model for. */
export const llmStages = ["Classify", "Split", "Extract", "Synthesize"] as const;

const schema = z
  .object({
    kind: z.enum(providerKinds),
    endpoint: z.string().trim().max(500).optional(),
    apiVersion: z.string().trim().max(50).optional(),
    apiKey: z.string().min(8).max(4096),
    dataResidency: z.enum(dataResidencies),
    monthlySpendCapUsd: z.string().trim().optional(),
    models: z.object({ Classify: z.string().trim().max(100), Split: z.string().trim().max(100), Extract: z.string().trim().max(100), Synthesize: z.string().trim().max(100) }),
  })
  .refine((v) => Object.values(v.models).some((m) => m !== ""), { path: ["models", "Extract"], ...translated("llm.atLeastOneModel") })
  .refine((v) => v.monthlySpendCapUsd === undefined || v.monthlySpendCapUsd === "" || (Number.isFinite(Number(v.monthlySpendCapUsd)) && Number(v.monthlySpendCapUsd) >= 0), {
    path: ["monthlySpendCapUsd"],
    ...translated("llm.invalidCap"),
  });

type FormValues = z.infer<typeof schema>;

/** The request body the form produces: blanks become null, the cap a number, only filled stages in `models`. */
export function toCreateProviderRequest(values: FormValues): CreateLlmProviderRequest {
  const models = Object.fromEntries(Object.entries(values.models).filter(([, model]) => model.trim() !== "")) as Record<string, string>;
  return {
    kind: values.kind,
    endpoint: values.endpoint ? values.endpoint : null,
    apiVersion: values.apiVersion ? values.apiVersion : null,
    apiKey: values.apiKey,
    dataResidency: values.dataResidency,
    monthlySpendCapUsd: values.monthlySpendCapUsd ? Number(values.monthlySpendCapUsd) : null,
    models,
  };
}

async function createProvider(client: ApiClient, scope: ProviderScope, body: CreateLlmProviderRequest) {
  return scope === "platform" ? unwrap(await client.POST("/api/v1/llm/providers", { body })) : unwrap(await client.POST("/api/v1/llm-providers", { body }));
}

/** "Add provider": kind, endpoint, API key (write-only), residency, spend cap and the model per stage. */
export function AddProviderDialog({ scope, queryKey }: { scope: ProviderScope; queryKey: QueryKey }) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { kind: "Anthropic", endpoint: "", apiVersion: "", apiKey: "", dataResidency: "Eu", monthlySpendCapUsd: "", models: { Classify: "", Split: "", Extract: "", Synthesize: "" } },
  });

  const mutation = useMutation({
    mutationFn: (values: FormValues) => createProvider(client, scope, toCreateProviderRequest(values)),
    meta: { silent: true },
    onSuccess: async (created) => {
      toast.success(t("llm.providerCreated", { kind: created.kind }));
      await queryClient.invalidateQueries({ queryKey });
      form.reset();
      setOpen(false);
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) {
          form.setError(field as keyof FormValues, { message: messages.join(" ") });
        }
        return;
      }
      toast.error(isApiError(error) ? error.title : t("llm.providerCreateFailed"), { description: isApiError(error) ? error.problem?.detail ?? undefined : undefined });
    },
  });


  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button size="sm">
          <Plus aria-hidden />
          {t("llm.addProvider")}
        </Button>
      </DialogTrigger>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t("llm.addProviderTitle")}</DialogTitle>
          <DialogDescription>{t("llm.addProviderDescription")}</DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={form.handleSubmit((values) => mutation.mutate(values))} className="grid gap-4" noValidate>
            <div className="grid gap-4 sm:grid-cols-2">
              <FormField
                control={form.control}
                name="kind"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("llm.kind")}</FormLabel>
                    <Select value={field.value} onValueChange={field.onChange}>
                      <FormControl>
                        <SelectTrigger className="w-full">
                          <SelectValue />
                        </SelectTrigger>
                      </FormControl>
                      <SelectContent>
                        {providerKinds.map((kind) => (
                          <SelectItem key={kind} value={kind}>
                            {kind}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="dataResidency"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("llm.dataResidency")}</FormLabel>
                    <Select value={field.value} onValueChange={field.onChange}>
                      <FormControl>
                        <SelectTrigger className="w-full">
                          <SelectValue />
                        </SelectTrigger>
                      </FormControl>
                      <SelectContent>
                        {dataResidencies.map((r) => (
                          <SelectItem key={r} value={r}>
                            {t(`llm.residency.${r}`, { defaultValue: r })}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </div>
            <FormField
              control={form.control}
              name="apiKey"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("llm.apiKey")}</FormLabel>
                  <FormControl>
                    <Input type="password" autoComplete="off" {...field} />
                  </FormControl>
                  <FormDescription>{t("llm.apiKeyHint")}</FormDescription>
                  <FormMessage />
                </FormItem>
              )}
            />
            <div className="grid gap-4 sm:grid-cols-2">
              <FormField
                control={form.control}
                name="endpoint"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("llm.endpoint")}</FormLabel>
                    <FormControl>
                      <Input placeholder="https://" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="apiVersion"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("llm.apiVersion")}</FormLabel>
                    <FormControl>
                      <Input {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </div>
            <FormField
              control={form.control}
              name="monthlySpendCapUsd"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("llm.spendCap")}</FormLabel>
                  <FormControl>
                    <Input inputMode="decimal" placeholder="100" {...field} />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />
            <fieldset className="grid gap-3 rounded-md border p-3">
              <legend className="px-1 text-sm font-medium">{t("llm.modelsPerStage")}</legend>
              <div className="grid gap-3 sm:grid-cols-2">
                {llmStages.map((stage) => (
                  <FormField
                    key={stage}
                    control={form.control}
                    name={`models.${stage}`}
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t(`stages.${stage}`)}</FormLabel>
                        <FormControl>
                          <Input placeholder={t("llm.modelPlaceholder")} {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                ))}
              </div>
            </fieldset>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setOpen(false)}>
                {t("common:dialog.cancel")}
              </Button>
              <Button type="submit" disabled={mutation.isPending} aria-busy={mutation.isPending}>
                {t("llm.addProvider")}
              </Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  );
}
