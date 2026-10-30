import { useId, useRef, useState, type DragEvent } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { FileUp, Loader2, Upload, X } from "lucide-react";
import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger, cn } from "@docproc/ui";
import { formatBytes, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { notifyUploaded } from "@/lib/queries";

export const MAX_UPLOAD_FILES = 20;
export const MAX_UPLOAD_BYTES = 52_428_800;
const ACCEPT = ".pdf,.xml,.png,.jpg,.jpeg,.tif,.tiff,.zip,application/pdf,application/xml,text/xml,image/*";

/**
 * The inbox's Upload button: a dialog with a file dropzone that posts `multipart/form-data` to `POST /ingestions`,
 * toasts the ingestion id and refetches the document lists so the new rows appear (and start polling).
 */
export function UploadDialog({ disabled = false }: { disabled?: boolean }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [files, setFiles] = useState<File[]>([]);
  const [dragging, setDragging] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const descriptionId = useId();

  const upload = useMutation({
    mutationFn: async (selected: File[]) => {
      const body = new FormData();
      for (const file of selected) body.append("files", file, file.name);
      // The generated multipart type lists `files: string[]`; openapi-fetch sends a FormData body as-is with the browser's boundary.
      return unwrap(await client.POST("/api/v1/ingestions", { body: body as unknown as { files: string[] } }));
    },
    onSuccess: (created) => {
      toast.success(t("upload.queued", { count: created.items.length }), { description: t("upload.ingestionId", { id: created.id }) });
      notifyUploaded();
      void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.ingestions.all });
      setFiles([]);
      setOpen(false);
    },
  });

  const addFiles = (incoming: FileList | File[] | null) => {
    if (!incoming) return;
    const next = [...files];
    for (const file of Array.from(incoming)) {
      if (file.size > MAX_UPLOAD_BYTES) {
        toast.error(t("upload.tooLarge", { name: file.name, max: formatBytes(MAX_UPLOAD_BYTES) }));
        continue;
      }
      if (!next.some((f) => f.name === file.name && f.size === file.size)) next.push(file);
    }
    if (next.length > MAX_UPLOAD_FILES) {
      toast.error(t("upload.tooMany", { max: MAX_UPLOAD_FILES }));
      next.length = MAX_UPLOAD_FILES;
    }
    setFiles(next);
  };

  const onDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragging(false);
    addFiles(event.dataTransfer.files);
  };

  const totalBytes = files.reduce((sum, file) => sum + file.size, 0);

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (upload.isPending) return;
        setOpen(next);
        if (!next) setFiles([]);
      }}
    >
      <DialogTrigger asChild>
        <Button disabled={disabled}>
          <Upload aria-hidden />
          {t("upload.button")}
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t("upload.title")}</DialogTitle>
          <DialogDescription id={descriptionId}>{t("upload.description", { max: MAX_UPLOAD_FILES, size: formatBytes(MAX_UPLOAD_BYTES) })}</DialogDescription>
        </DialogHeader>
        <div
          role="button"
          tabIndex={0}
          aria-describedby={descriptionId}
          aria-label={t("upload.dropzone")}
          onClick={() => inputRef.current?.click()}
          onKeyDown={(event) => {
            if (event.key === "Enter" || event.key === " ") {
              event.preventDefault();
              inputRef.current?.click();
            }
          }}
          onDragOver={(event) => {
            event.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={onDrop}
          className={cn(
            "flex cursor-pointer flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed px-6 py-10 text-center text-sm transition-colors",
            dragging ? "border-primary bg-primary/5" : "border-muted-foreground/30 hover:border-primary/60",
          )}
          data-testid="upload-dropzone"
        >
          <FileUp className="size-8 text-muted-foreground" aria-hidden />
          <span className="font-medium">{t("upload.dropHere")}</span>
          <span className="text-muted-foreground">{t("upload.orBrowse")}</span>
        </div>
        {/* Outside the dropzone: a control nested in role="button" is invisible to assistive technology. */}
        <input
          ref={inputRef}
          type="file"
          multiple
          accept={ACCEPT}
          className="sr-only"
          tabIndex={-1}
          aria-label={t("upload.chooseFiles")}
          onChange={(event) => {
            addFiles(event.target.files);
            event.target.value = "";
          }}
        />
        {files.length > 0 && (
          <ul className="max-h-48 divide-y overflow-auto rounded-md border text-sm" aria-label={t("upload.selected", { count: files.length })}>
            {files.map((file) => (
              <li key={`${file.name}-${file.size}`} className="flex items-center justify-between gap-2 px-3 py-2">
                <span className="min-w-0 truncate">{file.name}</span>
                <span className="flex shrink-0 items-center gap-2 text-muted-foreground">
                  <span className="tabular-nums">{formatBytes(file.size)}</span>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-xs"
                    aria-label={t("upload.remove", { name: file.name })}
                    onClick={() => setFiles(files.filter((f) => f !== file))}
                    disabled={upload.isPending}
                  >
                    <X aria-hidden />
                  </Button>
                </span>
              </li>
            ))}
          </ul>
        )}
        <DialogFooter className="sm:justify-between">
          <span className="text-xs text-muted-foreground" aria-live="polite">
            {files.length > 0 ? t("upload.summary", { count: files.length, size: formatBytes(totalBytes) }) : ""}
          </span>
          <div className="flex gap-2">
            <Button type="button" variant="outline" onClick={() => setOpen(false)} disabled={upload.isPending}>
              {t("common:dialog.cancel")}
            </Button>
            <Button type="button" onClick={() => upload.mutate(files)} disabled={files.length === 0 || upload.isPending} aria-busy={upload.isPending}>
              {upload.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Upload aria-hidden />}
              {upload.isPending ? t("upload.uploading") : t("upload.submit", { count: files.length })}
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
