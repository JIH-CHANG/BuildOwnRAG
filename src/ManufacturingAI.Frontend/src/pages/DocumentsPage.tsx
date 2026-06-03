import { useCallback, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Upload, Trash2, RefreshCw } from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge, Button, Skeleton, ToastContainer } from "@/components/ui";
import { documentsApi } from "@/api/documents";
import { getErrorMessage } from "@/api/client";
import { useToast } from "@/hooks/useToast";
import { formatBytes, formatDate } from "@/lib/utils";
import type { Document, DocumentStatus } from "@/types";

const STATUS_VARIANT: Record<DocumentStatus, "success" | "warning" | "error" | "info"> = {
  Indexed: "success",
  Processing: "warning",
  Pending: "info",
  Failed: "error",
};

export function DocumentsPage() {
  const qc = useQueryClient();
  const { toasts, toast, dismiss } = useToast();

  const { data, isLoading, refetch } = useQuery({
    queryKey: ["documents"],
    queryFn: () => documentsApi.list(),
    // Poll every 3s while any document is still being processed
    refetchInterval: (query) => {
      const items = query.state.data?.items ?? [];
      const hasActive = items.some((d) => d.status === "Pending" || d.status === "Processing");
      return hasActive ? 3000 : false;
    },
  });

  const deleteMut = useMutation({
    mutationFn: documentsApi.delete,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["documents"] });
      toast.success("Document deleted");
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  const uploadMut = useMutation({
    mutationFn: documentsApi.upload,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["documents"] });
      toast.success("Upload started — document will be indexed shortly");
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  const handleFiles = useCallback(
    (files: FileList | null) => {
      if (!files || files.length === 0) return;
      const valid = Array.from(files).filter((f) => {
        const mime = f.type;
        const ext = f.name.split(".").pop()?.toLowerCase() ?? "";
        return [
          "application/pdf",
          "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
          "application/msword",
          "text/csv",
          "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
          "text/plain",
          "text/markdown",
          "text/x-markdown",
        ].includes(mime) || ["txt", "md", "markdown"].includes(ext);
      });
      if (valid.length === 0) {
        toast.error("Only PDF, Word, Excel, CSV, TXT, or Markdown files are supported");
        return;
      }
      uploadMut.mutate(valid);
    },
    [uploadMut, toast]
  );

  const handleDrop = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      handleFiles(e.dataTransfer.files);
    },
    [handleFiles]
  );

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <header className="flex h-14 shrink-0 items-center justify-between border-b border-surface-border px-6">
        <h1 className="text-sm font-semibold text-slate-200">Documents</h1>
        <div className="flex gap-2">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => void refetch()}
            aria-label="Refresh"
          >
            <RefreshCw size={14} />
            Refresh
          </Button>
          <label className="cursor-pointer">
            <span
              className={`inline-flex items-center justify-center gap-2 rounded-md px-3 py-1.5 text-sm font-medium transition-colors bg-accent text-white hover:bg-accent-hover ${uploadMut.isPending ? "opacity-50 cursor-not-allowed" : ""}`}
            >
              <Upload size={14} />
              {uploadMut.isPending ? "Uploading…" : "Upload"}
            </span>
            <input
              type="file"
              multiple
              accept=".pdf,.docx,.doc,.csv,.xlsx,.txt,.md,.markdown"
              className="sr-only"
              disabled={uploadMut.isPending}
              onChange={(e) => handleFiles(e.target.files)}
            />
          </label>
        </div>
      </header>

      <DropZone onDrop={handleDrop} className="m-4 shrink-0" />

      <div className="flex-1 overflow-y-auto px-4 pb-4">
        {isLoading ? (
          <div className="flex flex-col gap-2">
            {Array.from({ length: 5 }).map((_, i) => (
              <Skeleton key={i} className="h-12 w-full" />
            ))}
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-surface-border text-left text-xs text-slate-500">
                <th className="pb-2 pr-4 font-medium">Title</th>
                <th className="pb-2 pr-4 font-medium">Size</th>
                <th className="pb-2 pr-4 font-medium">Uploaded</th>
                <th className="pb-2 pr-4 font-medium">Status</th>
                <th className="pb-2 font-medium" />
              </tr>
            </thead>
            <tbody>
              {data?.items.map((doc) => (
                <DocumentRow
                  key={doc.id}
                  doc={doc}
                  onDelete={() => deleteMut.mutate(doc.id)}
                  isDeleting={deleteMut.isPending}
                />
              ))}
              {data?.items.length === 0 && (
                <tr>
                  <td colSpan={5} className="py-12 text-center text-slate-500">
                    No documents yet. Upload your first document.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </div>

      <ToastContainer toasts={toasts} onDismiss={dismiss} />
    </div>
  );
}

function DocumentRow({
  doc,
  onDelete,
  isDeleting,
}: {
  doc: Document;
  onDelete: () => void;
  isDeleting: boolean;
}) {
  return (
    <tr className="border-b border-surface-border/50 hover:bg-white/[0.02]">
      <td className="py-3 pr-4">
        <span className="truncate font-medium text-slate-200">{doc.title}</span>
      </td>
      <td className="py-3 pr-4 text-slate-400">{formatBytes(doc.fileSizeBytes)}</td>
      <td className="py-3 pr-4 text-slate-400">{formatDate(doc.createdAt)}</td>
      <td className="py-3 pr-4">
        <Badge variant={STATUS_VARIANT[doc.status]}>{doc.status}</Badge>
      </td>
      <td className="py-3">
        <Button
          variant="danger"
          size="sm"
          onClick={onDelete}
          disabled={isDeleting}
          aria-label="Delete document"
        >
          <Trash2 size={13} />
        </Button>
      </td>
    </tr>
  );
}

function DropZone({
  onDrop,
  className,
}: {
  onDrop: (e: React.DragEvent<HTMLDivElement>) => void;
  className?: string;
}) {
  const [dragging, setDragging] = useState(false);

  return (
    <div
      onDrop={onDrop}
      onDragOver={(e) => {
        e.preventDefault();
        setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      className={cn(
        "flex flex-col items-center justify-center rounded-lg border-2 border-dashed py-6",
        "text-sm text-slate-500 transition-colors",
        dragging
          ? "border-accent bg-accent/5 text-accent"
          : "border-surface-border",
        className
      )}
    >
      <Upload size={20} className="mb-1" />
      Drag &amp; drop PDF, Word, Excel, CSV, TXT, or Markdown files here
    </div>
  );
}
