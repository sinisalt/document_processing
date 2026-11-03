import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Building2, Check, ChevronsUpDown, Loader2, Plus, Scissors, Wand2 } from "lucide-react";
import {
  Button,
  Checkbox,
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Popover,
  PopoverContent,
  PopoverTrigger,
  RadioGroup,
  RadioGroupItem,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  cn,
} from "@docproc/ui";
import { getByPath, isFieldValue, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { localized, useDocumentTypes, type DocumentTypeDetail } from "@docproc/review";
import { useDocumentParts, useSuppliers, type DocumentDetail } from "@/lib/queries";

/** What the review screen does after an action answered with the document as stored. */
function useSettle(documentId: string) {
  const queryClient = useQueryClient();
  return (updated?: DocumentDetail) => {
    if (updated) queryClient.setQueryData(queryKeys.documents.detail(documentId), updated);
    void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
    void queryClient.invalidateQueries({ queryKey: queryKeys.reviewTasks.all });
  };
}

/** The types a reviewer can give a document: every type of the tenant with a published version (docs/11 §12.1). */
export function usableTypes(types: readonly DocumentTypeDetail[] | undefined): DocumentTypeDetail[] {
  return (types ?? []).filter((type) => type.publishedVersion !== null && type.publishedVersion !== undefined && type.status !== "Retired");
}

function typeLabel(type: DocumentTypeDetail, locale: string): string {
  return localized(type.name, locale) ?? type.key;
}

export type Prefill = "none" | "auto";

export interface SetTypeFormProps {
  document: DocumentDetail;
  locale: string;
  onDone?: () => void;
  /** Inside a dialog the buttons sit in its footer. */
  inDialog?: boolean;
}

/**
 * `:set-type` (docs/11 §12.1 step 3): a type with a published version, and how to start: Empty (every field Missing,
 * filled from the page in map mode) or Let AI pre-fill (the Extract stage reads the document for the type, 202).
 */
export function SetTypeForm({ document, locale, onDone, inDialog = false }: SetTypeFormProps) {
  const { t } = useTranslation();
  const { client } = useApi();
  const settle = useSettle(document.id);
  const types = useDocumentTypes();
  const usable = useMemo(() => usableTypes(types.data), [types.data]);
  const current = document.definition?.key ?? (usable.some((type) => type.key === document.type) ? document.type : "");
  const [typeKey, setTypeKey] = useState(current);
  const [prefill, setPrefill] = useState<Prefill>("none");
  const setType = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/documents/{id}:set-type", { params: { path: { id: document.id } }, body: { typeKey, prefill } })),
    onSuccess: (updated) => {
      const type = usable.find((u) => u.key === typeKey);
      toast.success(prefill === "auto" ? t("review.setType.queued", { type: type ? typeLabel(type, locale) : typeKey }) : t("review.setType.done", { type: type ? typeLabel(type, locale) : typeKey }));
      settle(updated);
      onDone?.();
    },
  });

  const footer = (
    <Button type="button" onClick={() => setType.mutate()} disabled={!typeKey || setType.isPending} data-testid="set-type-submit">
      {setType.isPending ? <Loader2 className="animate-spin" aria-hidden /> : prefill === "auto" ? <Wand2 aria-hidden /> : <Check aria-hidden />}
      {t("review.setType.submit")}
    </Button>
  );
  return (
    <div className="flex flex-col gap-4" data-testid="set-type-form">
      <div className="flex flex-col gap-1">
        <Label htmlFor="set-type-key">{t("review.setType.type")}</Label>
        <Select value={typeKey} onValueChange={setTypeKey} disabled={types.isPending}>
          <SelectTrigger id="set-type-key" className="w-full sm:w-80" data-testid="set-type-key">
            <SelectValue placeholder={types.isPending ? t("review.setType.loading") : t("review.setType.pick")} />
          </SelectTrigger>
          <SelectContent>
            {usable.map((type) => (
              <SelectItem key={type.key} value={type.key}>
                {typeLabel(type, locale)}
                <span className="ml-2 text-xs text-muted-foreground">v{String(type.publishedVersion)}</span>
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {!types.isPending && usable.length === 0 && <p className="text-sm text-muted-foreground">{t("review.setType.none")}</p>}
      </div>
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1 text-sm font-medium">{t("review.setType.startWith")}</legend>
        <RadioGroup value={prefill} onValueChange={(value) => setPrefill(value as Prefill)}>
          <div className="flex items-start gap-2">
            <RadioGroupItem value="none" id="prefill-none" className="mt-0.5" />
            <div className="grid gap-0.5">
              <Label htmlFor="prefill-none">{t("review.setType.empty")}</Label>
              <p className="text-xs text-muted-foreground">{t("review.setType.emptyHint")}</p>
            </div>
          </div>
          <div className="flex items-start gap-2">
            <RadioGroupItem value="auto" id="prefill-auto" className="mt-0.5" />
            <div className="grid gap-0.5">
              <Label htmlFor="prefill-auto">{t("review.setType.auto")}</Label>
              <p className="text-xs text-muted-foreground">{t("review.setType.autoHint")}</p>
            </div>
          </div>
        </RadioGroup>
      </fieldset>
      {inDialog ? <DialogFooter>{footer}</DialogFooter> : <div>{footer}</div>}
    </div>
  );
}

export function SetTypeDialog({ document, locale, open, onOpenChange }: { document: DocumentDetail; locale: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t("review.setType.title")}</DialogTitle>
          <DialogDescription>{t("review.setType.description")}</DialogDescription>
        </DialogHeader>
        {open && <SetTypeForm document={document} locale={locale} inDialog onDone={() => onOpenChange(false)} />}
      </DialogContent>
    </Dialog>
  );
}

/** A supplier picker over `GET /suppliers?q=` (a Command in a Popover); picking one assigns it (`:assign-supplier`). */
export function SupplierPicker({ document, onCreate }: { document: DocumentDetail; onCreate?: () => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const settle = useSettle(document.id);
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState("");
  const suppliers = useSuppliers(q.trim() ? { q: q.trim(), limit: 20 } : { limit: 20 }, open);
  const assign = useMutation({
    mutationFn: async (supplierId: string) => unwrap(await client.POST("/api/v1/documents/{id}:assign-supplier", { params: { path: { id: document.id } }, body: { supplierId } })),
    onSuccess: (updated) => {
      toast.success(t("review.supplier.assigned", { name: updated.supplierName ?? "" }));
      settle(updated);
      setOpen(false);
    },
  });
  return (
    <div className="flex flex-wrap items-center gap-2" data-testid="supplier-picker">
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button type="button" variant="outline" role="combobox" aria-expanded={open} className="w-full justify-between sm:w-80" data-testid="supplier-picker-trigger">
            <span className="flex items-center gap-2 truncate">
              <Building2 className="size-4" aria-hidden />
              {document.supplierName ?? t("review.supplier.pick")}
            </span>
            <ChevronsUpDown className="size-4 opacity-50" aria-hidden />
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-80 p-0" align="start">
          <Command shouldFilter={false}>
            <CommandInput placeholder={t("review.supplier.search")} value={q} onValueChange={setQ} />
            <CommandList>
              <CommandEmpty>{suppliers.isLoading ? t("review.supplier.loading") : t("review.supplier.noMatch")}</CommandEmpty>
              <CommandGroup>
                {suppliers.items.map((supplier) => (
                  <CommandItem key={supplier.id} value={supplier.id} onSelect={() => assign.mutate(supplier.id)} disabled={assign.isPending}>
                    <Check className={cn("size-4", supplier.id === document.supplierId ? "opacity-100" : "opacity-0")} aria-hidden />
                    <span className="truncate">{supplier.name}</span>
                    {supplier.vatId && <span className="ml-auto text-xs text-muted-foreground">{supplier.vatId}</span>}
                  </CommandItem>
                ))}
              </CommandGroup>
            </CommandList>
          </Command>
        </PopoverContent>
      </Popover>
      {onCreate && (
        <Button type="button" variant="ghost" size="sm" onClick={onCreate} data-testid="create-supplier-open">
          <Plus aria-hidden />
          {t("review.supplier.create")}
        </Button>
      )}
    </div>
  );
}

/** `:assign-supplier` from the review actions: the picker in a dialog, with "create supplier from this document" beside it. */
export function AssignSupplierDialog({ document, open, onOpenChange, onCreate }: { document: DocumentDetail; open: boolean; onOpenChange: (open: boolean) => void; onCreate: () => void }) {
  const { t } = useTranslation();
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t("review.supplier.assignTitle")}</DialogTitle>
          <DialogDescription>{t("review.supplier.assignDescription")}</DialogDescription>
        </DialogHeader>
        {open && <SupplierPicker document={document} onCreate={onCreate} />}
      </DialogContent>
    </Dialog>
  );
}

/** The supplier party of the document's data, as far as it was captured: what "create supplier from this document" starts from. */
export function supplierDraftOf(document: DocumentDetail): { name: string; vatId: string; iban: string; emailDomain: string } {
  const party = getByPath(document.canonical, "supplier");
  const value = isFieldValue(party) && party.status !== "Missing" && party.value && typeof party.value === "object" ? (party.value as Record<string, unknown>) : {};
  const text = (key: string) => (typeof value[key] === "string" ? (value[key] as string) : "");
  const email = text("email");
  return { name: text("name") || (document.supplierName ?? ""), vatId: text("vatId"), iban: text("iban"), emailDomain: email.includes("@") ? email.split("@")[1]! : "" };
}

/** `:create-supplier` (docs/11 §14): a new supplier from the document's supplier party, created and assigned in one save. */
export function CreateSupplierDialog({ document, open, onOpenChange }: { document: DocumentDetail; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const settle = useSettle(document.id);
  const draft = useMemo(() => supplierDraftOf(document), [document]);
  const [name, setName] = useState(draft.name);
  const [vatId, setVatId] = useState(draft.vatId);
  const [iban, setIban] = useState(draft.iban);
  const [domain, setDomain] = useState(draft.emailDomain);
  const create = useMutation({
    mutationFn: async () =>
      unwrap(
        await client.POST("/api/v1/documents/{id}:create-supplier", {
          params: { path: { id: document.id } },
          body: {
            name: name.trim(),
            vatId: vatId.trim() || null,
            taxId: null,
            ibans: iban.trim() ? [iban.trim()] : null,
            emailDomains: domain.trim() ? [domain.trim()] : null,
            aliases: null,
            alwaysReview: null,
            defaultCurrency: null,
          },
        }),
      ),
    onSuccess: (answer) => {
      toast.success(t("review.supplier.created", { name: answer.supplier.name }));
      settle(answer.document);
      void queryClient.invalidateQueries({ queryKey: queryKeys.suppliers.all });
      onOpenChange(false);
    },
  });
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t("review.supplier.createTitle")}</DialogTitle>
          <DialogDescription>{t("review.supplier.createDescription")}</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-3"
          onSubmit={(event) => {
            event.preventDefault();
            create.mutate();
          }}
          data-testid="create-supplier-form"
        >
          <div className="grid gap-1">
            <Label htmlFor="new-supplier-name">{t("review.supplier.name")}</Label>
            <Input id="new-supplier-name" value={name} onChange={(event) => setName(event.target.value)} required autoFocus />
          </div>
          <div className="grid gap-1">
            <Label htmlFor="new-supplier-vat">{t("review.supplier.vatId")}</Label>
            <Input id="new-supplier-vat" value={vatId} onChange={(event) => setVatId(event.target.value)} />
          </div>
          <div className="grid gap-1">
            <Label htmlFor="new-supplier-iban">{t("review.supplier.iban")}</Label>
            <Input id="new-supplier-iban" value={iban} onChange={(event) => setIban(event.target.value)} />
          </div>
          <div className="grid gap-1">
            <Label htmlFor="new-supplier-domain">{t("review.supplier.emailDomain")}</Label>
            <Input id="new-supplier-domain" value={domain} onChange={(event) => setDomain(event.target.value)} placeholder="nordwind.example" />
          </div>
          <DialogFooter>
            <Button type="submit" disabled={!name.trim() || create.isPending} data-testid="create-supplier-submit">
              {create.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Plus aria-hidden />}
              {t("review.supplier.createSubmit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * The parts of a split from where new parts start: `starts` holds the pages (2..n) that begin a part.
 * `[2, 4]` over 5 pages gives [[1], [2, 3], [4, 5]].
 */
export function partsFromStarts(pageCount: number, starts: ReadonlySet<number>): number[][] {
  const parts: number[][] = [];
  let current: number[] = [];
  for (let page = 1; page <= pageCount; page++) {
    if (page > 1 && starts.has(page)) {
      parts.push(current);
      current = [];
    }
    current.push(page);
  }
  parts.push(current);
  return parts;
}

/** `:split` (docs/11 §12.7): the pages where a new document starts, and optionally each part's type. */
export function SplitDialog({ document, locale, open, onOpenChange }: { document: DocumentDetail; locale: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const settle = useSettle(document.id);
  const pageCount = Number(document.pageCount) || 0;
  const [starts, setStarts] = useState<Set<number>>(new Set());
  const [types, setTypes] = useState<Record<number, string>>({});
  const typeList = useDocumentTypes(open);
  const usable = useMemo(() => usableTypes(typeList.data), [typeList.data]);
  const parts = partsFromStarts(pageCount, starts);
  const split = useMutation({
    mutationFn: async () =>
      unwrap(
        await client.POST("/api/v1/documents/{id}:split", {
          params: { path: { id: document.id } },
          body: { parts: parts.map((pages, index) => ({ pages, typeKey: types[index] && types[index] !== "__none__" ? types[index] : null })) },
        }),
      ),
    onSuccess: (answer) => {
      toast.success(t("review.split.done", { count: answer.parts.length }));
      settle();
      onOpenChange(false);
    },
  });
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{t("review.split.title")}</DialogTitle>
          <DialogDescription>{t("review.split.description")}</DialogDescription>
        </DialogHeader>
        <ol className="flex flex-col gap-1" aria-label={t("review.split.pages")}>
          {Array.from({ length: pageCount }, (_, i) => i + 1).map((page) => (
            <li key={page} className="flex items-center gap-2">
              {page > 1 ? (
                <Checkbox
                  id={`split-start-${page}`}
                  checked={starts.has(page)}
                  onCheckedChange={(checked) => {
                    const next = new Set(starts);
                    if (checked === true) next.add(page);
                    else next.delete(page);
                    setStarts(next);
                  }}
                />
              ) : (
                <span className="size-4" aria-hidden />
              )}
              <Label htmlFor={`split-start-${page}`} className="font-normal">
                {page > 1 ? t("review.split.startsAt", { page }) : t("review.split.firstPage")}
              </Label>
            </li>
          ))}
        </ol>
        {parts.length > 1 && (
          <ul className="flex flex-col gap-2" aria-label={t("review.split.parts")}>
            {parts.map((pages, index) => (
              <li key={index} className="flex flex-wrap items-center gap-2" data-testid={`split-part-${index}`}>
                <span className="w-40 text-sm">{t("review.split.part", { n: index + 1, from: pages[0], to: pages[pages.length - 1] })}</span>
                <Select value={types[index] ?? "__none__"} onValueChange={(value) => setTypes({ ...types, [index]: value })}>
                  <SelectTrigger size="sm" className="min-w-48" aria-label={t("review.split.partType", { n: index + 1 })}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__none__">{t("review.split.classify")}</SelectItem>
                    {usable.map((type) => (
                      <SelectItem key={type.key} value={type.key}>
                        {typeLabel(type, locale)}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </li>
            ))}
          </ul>
        )}
        <DialogFooter>
          <Button type="button" onClick={() => split.mutate()} disabled={parts.length < 2 || split.isPending} data-testid="split-submit">
            {split.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Scissors aria-hidden />}
            {t("review.split.submit", { count: parts.length })}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** `:join` (docs/11 §12.7): the other parts of the attachment, which with this one make one run of pages. */
export function JoinDialog({ document, open, onOpenChange }: { document: DocumentDetail; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const settle = useSettle(document.id);
  const parts = useDocumentParts(document.id, open);
  const others = (parts.data ?? []).filter((part) => part.documentId !== document.id && part.status !== "Cancelled");
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const join = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/documents/{id}:join", { params: { path: { id: document.id } }, body: { documentIds: [...picked] } })),
    onSuccess: (updated) => {
      toast.success(t("review.join.done", { count: picked.size + 1 }));
      settle(updated);
      onOpenChange(false);
    },
  });
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t("review.join.title")}</DialogTitle>
          <DialogDescription>{t("review.join.description")}</DialogDescription>
        </DialogHeader>
        {parts.isPending ? (
          <Loader2 className="animate-spin" aria-label={t("review.join.loading")} />
        ) : others.length === 0 ? (
          <p className="text-sm text-muted-foreground" data-testid="join-none">
            {t("review.join.none")}
          </p>
        ) : (
          <ul className="flex flex-col gap-2" data-testid="join-parts">
            {others.map((part) => (
              <li key={part.documentId} className="flex items-center gap-2">
                <Checkbox
                  id={`join-${part.documentId}`}
                  checked={picked.has(part.documentId)}
                  onCheckedChange={(checked) => {
                    const next = new Set(picked);
                    if (checked === true) next.add(part.documentId);
                    else next.delete(part.documentId);
                    setPicked(next);
                  }}
                />
                <Label htmlFor={`join-${part.documentId}`} className="font-normal">
                  {t("review.join.part", { from: Number(part.pageStart), to: Number(part.pageEnd), number: part.documentNumber ?? t("inbox.noNumber"), status: t(`common:documentStatus.${part.status}`) })}
                </Label>
              </li>
            ))}
          </ul>
        )}
        <DialogFooter>
          <Button type="button" onClick={() => join.mutate()} disabled={picked.size === 0 || join.isPending} data-testid="join-submit">
            {join.isPending && <Loader2 className="animate-spin" aria-hidden />}
            {t("review.join.submit")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
