import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { RotateCcw } from "lucide-react";
import { Button, Skeleton, ToastContainer } from "@/components/ui";
import { tenantApi } from "@/api/tenant";
import { getErrorMessage } from "@/api/client";
import { useToast } from "@/hooks/useToast";

export function SystemPromptPage() {
  const qc = useQueryClient();
  const { toasts, toast, dismiss } = useToast();

  const { data: settings, isLoading } = useQuery({
    queryKey: ["tenant-system-prompt"],
    queryFn: tenantApi.getSystemPrompt,
  });

  // null = use server value (custom prompt if set, else default); non-null = user override
  const [textOverride, setTextOverride] = useState<string | null>(null);
  const text = textOverride ?? settings?.systemPrompt ?? settings?.defaultPrompt ?? "";

  const saveMut = useMutation({
    // Sending the default text (or empty) resets the tenant back to the built-in default.
    mutationFn: () => {
      const trimmed = text.trim();
      const isDefault = trimmed === (settings?.defaultPrompt ?? "").trim();
      return tenantApi.updateSystemPrompt(isDefault ? "" : text);
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["tenant-system-prompt"] });
      toast.success("System prompt saved");
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  if (isLoading) {
    return (
      <div className="flex max-w-2xl flex-col gap-4">
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-64 w-full" />
        <Skeleton className="h-10 w-32" />
      </div>
    );
  }

  if (!settings) return null;

  const effective = settings.systemPrompt || settings.defaultPrompt;
  const isDirty = text !== effective;
  const usingDefault = !settings.systemPrompt;
  const matchesDefault = text.trim() === settings.defaultPrompt.trim();

  return (
    <div className="max-w-2xl space-y-4">
      <div>
        <h2 className="mb-1 text-base font-semibold text-slate-100">System Prompt</h2>
        <p className="text-sm text-slate-400">
          Customize the instructions the assistant follows when answering questions. Applies to
          both Hybrid and Lite retrieval modes.
        </p>
      </div>

      <div className="flex items-center gap-2 text-xs">
        <span
          className={
            usingDefault
              ? "rounded-full bg-slate-700/60 px-2 py-0.5 text-slate-300"
              : "rounded-full bg-accent/20 px-2 py-0.5 text-accent"
          }
        >
          {usingDefault ? "Using default prompt" : "Custom prompt active"}
        </span>
      </div>

      <div>
        <textarea
          id="system-prompt"
          value={text}
          onChange={(e) => setTextOverride(e.target.value)}
          rows={14}
          spellCheck={false}
          className="w-full resize-y rounded-md border border-surface-border bg-surface px-3 py-2 font-mono text-sm leading-relaxed text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        />
        <p className="mt-1 text-xs text-slate-500">
          The retrieved reference documents are appended automatically — do not add a{" "}
          <code className="rounded bg-surface-muted px-1 py-0.5 text-slate-400">{"{context}"}</code>{" "}
          placeholder.
        </p>
      </div>

      <div className="flex gap-3 pt-1">
        <Button
          onClick={() => saveMut.mutate()}
          disabled={!isDirty}
          loading={saveMut.isPending}
        >
          Save changes
        </Button>
        <Button
          variant="ghost"
          onClick={() => setTextOverride(settings.defaultPrompt)}
          disabled={matchesDefault}
        >
          <RotateCcw size={15} className="mr-1.5" />
          Reset to default
        </Button>
        {isDirty && (
          <Button variant="ghost" onClick={() => setTextOverride(null)}>
            Cancel
          </Button>
        )}
      </div>

      <ToastContainer toasts={toasts} onDismiss={dismiss} />
    </div>
  );
}
