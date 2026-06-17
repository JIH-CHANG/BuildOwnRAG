import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Pencil, Plus, RefreshCw, Trash2 } from "lucide-react";
import {
  Badge,
  Button,
  Skeleton,
  Spinner,
  ToastContainer,
} from "@/components/ui";
import { ingestApi } from "@/api/ingest";
import { connectorsApi } from "@/api/connectors";
import { getErrorMessage } from "@/api/client";
import { useToast } from "@/hooks/useToast";
import type { ConnectorSyncStatus, SyncStatus } from "@/types";
import { AddConnectorModal } from "./AddConnectorModal";
import { EditConnectorModal } from "./EditConnectorModal";
import { syncIntervalLabel } from "./connectorFields";

function StatusBadge({ status }: { status: SyncStatus }) {
  switch (status) {
    case "Running":
      return (
        <Badge variant="info">
          <Spinner size={10} />
          Running
        </Badge>
      );
    case "Completed":
      return <Badge variant="success">Completed</Badge>;
    case "Failed":
      return <Badge variant="error">Failed</Badge>;
    default:
      return <Badge variant="muted">Pending</Badge>;
  }
}

function formatDateTime(iso?: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleString("en-US", {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

export function ConnectorsPage() {
  const qc = useQueryClient();
  const { toasts, toast, dismiss } = useToast();
  const [triggeringId, setTriggeringId] = useState<string | null>(null);
  const [addOpen, setAddOpen] = useState(false);
  const [editId, setEditId] = useState<string | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ["ingest-status"],
    queryFn: ingestApi.getStatus,
    refetchInterval: 5_000,
  });

  const triggerMut = useMutation({
    mutationFn: (connectorId?: string) => ingestApi.triggerSync(connectorId),
    onSuccess: (_, connectorId) => {
      void qc.invalidateQueries({ queryKey: ["ingest-status"] });
      toast.success(connectorId ? "Sync started" : "All connectors syncing");
      setTriggeringId(null);
    },
    onError: (err) => {
      toast.error(getErrorMessage(err));
      setTriggeringId(null);
    },
  });

  const handleTrigger = (connectorId?: string) => {
    setTriggeringId(connectorId ?? "all");
    triggerMut.mutate(connectorId);
  };

  const deleteMut = useMutation({
    mutationFn: (connectorId: string) => connectorsApi.remove(connectorId),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["ingest-status"] });
      toast.success("Connector deleted");
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  const handleDelete = (connector: ConnectorSyncStatus) => {
    if (window.confirm(`Delete connector “${connector.displayName}”? This cannot be undone.`))
      deleteMut.mutate(connector.connectorId);
  };

  const connectors = data?.connectors ?? [];

  return (
    <div className="max-w-3xl">
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h2 className="text-base font-semibold text-slate-100">Connectors</h2>
          <p className="text-sm text-slate-400">
            Manage document sources and sync status
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="ghost" size="sm" onClick={() => setAddOpen(true)}>
            <Plus size={14} />
            Add Connector
          </Button>
          <Button
            size="sm"
            onClick={() => handleTrigger()}
            loading={triggerMut.isPending && triggeringId === "all"}
            disabled={triggerMut.isPending}
          >
            <RefreshCw size={14} />
            Sync All
          </Button>
        </div>
      </div>

      {isLoading ? (
        <div className="flex flex-col gap-2">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : connectors.length === 0 ? (
        <div className="flex flex-col items-center justify-center rounded-lg border border-surface-border py-16 text-center">
          <p className="text-sm text-slate-400">No connectors configured.</p>
          <p className="mt-1 text-xs text-slate-500">
            Use “Add Connector” to connect a Google Drive folder or a local folder.
          </p>
        </div>
      ) : (
        <>
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-surface-border text-left text-xs text-slate-500">
                <th className="pb-2 pr-4 font-medium">Name</th>
                <th className="pb-2 pr-4 font-medium">Type</th>
                <th className="pb-2 pr-4 font-medium">Schedule</th>
                <th className="pb-2 pr-4 font-medium">Status</th>
                <th className="pb-2 pr-4 font-medium">Last Sync</th>
                <th className="pb-2 font-medium" />
              </tr>
            </thead>
            <tbody>
              {connectors.map((c) => (
                <ConnectorRow
                  key={c.connectorId}
                  connector={c}
                  onSync={() => handleTrigger(c.connectorId)}
                  onEdit={() => setEditId(c.connectorId)}
                  onDelete={() => handleDelete(c)}
                  isSyncing={
                    triggerMut.isPending && triggeringId === c.connectorId
                  }
                  isDeleting={
                    deleteMut.isPending && deleteMut.variables === c.connectorId
                  }
                  disabled={triggerMut.isPending}
                />
              ))}
            </tbody>
          </table>

          <p className="mt-4 text-xs text-slate-500">
            {data?.totalConnectors ?? 0} connector
            {(data?.totalConnectors ?? 0) !== 1 ? "s" : ""}
            {(data?.runningJobs ?? 0) > 0 && (
              <>
                {" · "}
                <span className="text-blue-400">
                  {data!.runningJobs} running
                </span>
              </>
            )}
          </p>
        </>
      )}

      <AddConnectorModal
        open={addOpen}
        onClose={() => setAddOpen(false)}
        onResult={(success, message) => {
          void qc.invalidateQueries({ queryKey: ["ingest-status"] });
          if (success) toast.success(`Connector added — ${message}`);
          else toast.error(`Connector added but test failed — ${message}`);
        }}
      />

      <EditConnectorModal
        connectorId={editId}
        onClose={() => setEditId(null)}
        onResult={(success, message) => {
          void qc.invalidateQueries({ queryKey: ["ingest-status"] });
          if (success) toast.success(message);
          else toast.error(`Saved but test failed — ${message}`);
        }}
      />

      <ToastContainer toasts={toasts} onDismiss={dismiss} />
    </div>
  );
}

function ConnectorRow({
  connector,
  onSync,
  onEdit,
  onDelete,
  isSyncing,
  isDeleting,
  disabled,
}: {
  connector: ConnectorSyncStatus;
  onSync: () => void;
  onEdit: () => void;
  onDelete: () => void;
  isSyncing: boolean;
  isDeleting: boolean;
  disabled: boolean;
}) {
  return (
    <tr className="border-b border-surface-border/50 hover:bg-white/[0.02]">
      <td className="py-3 pr-4 font-medium text-slate-200">
        {connector.displayName}
      </td>
      <td className="py-3 pr-4">
        <Badge variant="muted">{connector.connectorType}</Badge>
      </td>
      <td className="py-3 pr-4 text-slate-400">
        {connector.syncIntervalMinutes > 0
          ? syncIntervalLabel(connector.syncIntervalMinutes)
          : "Manual only"}
      </td>
      <td className="py-3 pr-4">
        <StatusBadge status={connector.status} />
      </td>
      <td className="py-3 pr-4 text-slate-400">
        {formatDateTime(connector.lastSyncedAt)}
      </td>
      <td className="py-3">
        <div className="flex items-center justify-end gap-3">
          {connector.status === "Failed" && connector.errorMessage && (
            <span
              className="max-w-[180px] truncate text-xs text-red-400"
              title={connector.errorMessage}
            >
              {connector.errorMessage}
            </span>
          )}
          <Button
            variant="ghost"
            size="sm"
            onClick={onSync}
            loading={isSyncing}
            disabled={disabled}
          >
            Sync
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={onEdit}
            disabled={disabled || isDeleting}
            aria-label="Edit connector"
          >
            <Pencil size={14} />
          </Button>
          <Button
            variant="danger"
            size="sm"
            onClick={onDelete}
            loading={isDeleting}
            disabled={disabled}
            aria-label="Delete connector"
          >
            <Trash2 size={14} />
          </Button>
        </div>
      </td>
    </tr>
  );
}
