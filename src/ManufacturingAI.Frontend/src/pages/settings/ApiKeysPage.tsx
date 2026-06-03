import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Key, Copy, Trash2 } from "lucide-react";
import {
  Badge,
  Button,
  Input,
  Modal,
  Skeleton,
  ToastContainer,
} from "@/components/ui";
import { apiKeysApi } from "@/api/users";
import { getErrorMessage } from "@/api/client";
import { useToast } from "@/hooks/useToast";
import { formatDate } from "@/lib/utils";

const schema = z.object({
  name: z.string().min(1, "Name is required").max(64),
});

type FormValues = z.infer<typeof schema>;

export function ApiKeysPage() {
  const qc = useQueryClient();
  const { toasts, toast, dismiss } = useToast();
  const [showModal, setShowModal] = useState(false);
  const [newKey, setNewKey] = useState<string | null>(null);

  const { data: keys, isLoading } = useQuery({
    queryKey: ["api-keys"],
    queryFn: apiKeysApi.list,
  });

  const createMut = useMutation({
    mutationFn: (name: string) => apiKeysApi.create(name),
    onSuccess: (data) => {
      void qc.invalidateQueries({ queryKey: ["api-keys"] });
      setNewKey(data.key);
      reset();
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  const revokeMut = useMutation({
    mutationFn: apiKeysApi.revoke,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["api-keys"] });
      toast.success("API key revoked");
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema) });

  const onSubmit = (values: FormValues) => createMut.mutate(values.name);

  const copyKey = () => {
    if (!newKey) return;
    void navigator.clipboard.writeText(newKey);
    toast.success("Copied to clipboard");
  };

  return (
    <div className="max-w-3xl">
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h2 className="text-base font-semibold text-slate-100">API Keys</h2>
          <p className="text-sm text-slate-400">
            Machine-to-machine access — key is shown only once at creation
          </p>
        </div>
        <Button size="sm" onClick={() => setShowModal(true)}>
          <Key size={14} />
          New key
        </Button>
      </div>

      {isLoading ? (
        <div className="flex flex-col gap-2">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-surface-border text-left text-xs text-slate-500">
              <th className="pb-2 pr-4 font-medium">Name</th>
              <th className="pb-2 pr-4 font-medium">Prefix</th>
              <th className="pb-2 pr-4 font-medium">Created</th>
              <th className="pb-2 pr-4 font-medium">Last used</th>
              <th className="pb-2 font-medium" />
            </tr>
          </thead>
          <tbody>
            {keys?.map((k) => (
              <tr
                key={k.id}
                className="border-b border-surface-border/50 hover:bg-white/[0.02]"
              >
                <td className="py-3 pr-4 font-medium text-slate-200">
                  {k.name}
                </td>
                <td className="py-3 pr-4">
                  <Badge variant="muted">{k.keyPrefix}…</Badge>
                </td>
                <td className="py-3 pr-4 text-slate-400">
                  {formatDate(k.createdAt)}
                </td>
                <td className="py-3 pr-4 text-slate-400">
                  {k.lastUsedAt ? formatDate(k.lastUsedAt) : "Never"}
                </td>
                <td className="py-3">
                  <Button
                    variant="danger"
                    size="sm"
                    onClick={() => revokeMut.mutate(k.id)}
                    disabled={revokeMut.isPending}
                    aria-label="Revoke key"
                  >
                    <Trash2 size={13} />
                  </Button>
                </td>
              </tr>
            ))}
            {keys?.length === 0 && (
              <tr>
                <td colSpan={5} className="py-12 text-center text-slate-500">
                  No API keys yet.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      )}

      <Modal
        open={showModal}
        onClose={() => {
          setShowModal(false);
          setNewKey(null);
          reset();
        }}
        title="Create API key"
      >
        {newKey ? (
          <div className="flex flex-col gap-4">
            <p className="text-sm text-amber-300">
              Copy this key now — it will not be shown again.
            </p>
            <div className="flex items-center gap-2 rounded-md bg-surface border border-surface-border px-3 py-2">
              <code className="flex-1 overflow-x-auto text-xs text-slate-300">
                {newKey}
              </code>
              <Button variant="ghost" size="sm" onClick={copyKey}>
                <Copy size={13} />
              </Button>
            </div>
            <Button
              onClick={() => {
                setShowModal(false);
                setNewKey(null);
              }}
              className="w-full"
            >
              Done
            </Button>
          </div>
        ) : (
          <form
            onSubmit={handleSubmit(onSubmit)}
            className="flex flex-col gap-4"
            noValidate
          >
            <Input
              id="key-name"
              label="Key name"
              placeholder="e.g. CI Pipeline"
              error={errors.name?.message}
              {...register("name")}
            />
            <div className="flex gap-3">
              <Button type="submit" loading={isSubmitting} className="flex-1">
                Create
              </Button>
              <Button
                type="button"
                variant="ghost"
                onClick={() => setShowModal(false)}
              >
                Cancel
              </Button>
            </div>
          </form>
        )}
      </Modal>

      <ToastContainer toasts={toasts} onDismiss={dismiss} />
    </div>
  );
}
