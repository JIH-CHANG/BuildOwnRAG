import { useEffect, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Button, Input, Modal, Spinner } from "@/components/ui";
import { connectorsApi } from "@/api/connectors";
import { getErrorMessage } from "@/api/client";
import {
  ConnectorTypeFields,
  DEFAULT_SYNC_INTERVAL,
  SyncIntervalSelect,
  TYPE_LABELS,
  buildSettingsJson,
  emptyFields,
  validateFields,
  type ConnectorFieldsValue,
  type ConnectorType,
} from "./connectorFields";

interface EditConnectorModalProps {
  connectorId: string | null;
  onClose: () => void;
  /** Called after a successful save (and re-test if settings were replaced). */
  onResult: (success: boolean, message: string) => void;
}

export function EditConnectorModal({ connectorId, onClose, onResult }: EditConnectorModalProps) {
  const [loading, setLoading] = useState(false);
  const [type, setType] = useState<ConnectorType>("googledrive");
  const [displayName, setDisplayName] = useState("");
  const [isEnabled, setIsEnabled] = useState(true);
  const [syncInterval, setSyncInterval] = useState(DEFAULT_SYNC_INTERVAL);
  const [replaceSettings, setReplaceSettings] = useState(false);
  const [fields, setFields] = useState<ConnectorFieldsValue>(emptyFields);
  const [error, setError] = useState<string | null>(null);

  const patch = (p: Partial<ConnectorFieldsValue>) => setFields((f) => ({ ...f, ...p }));

  useEffect(() => {
    if (!connectorId) return;
    setLoading(true);
    setError(null);
    setReplaceSettings(false);
    setFields(emptyFields);
    connectorsApi
      .get(connectorId)
      .then((c) => {
        setType(c.connectorType as ConnectorType);
        setDisplayName(c.displayName);
        setIsEnabled(c.isEnabled);
        setSyncInterval(c.syncIntervalMinutes);
      })
      .catch((err) => setError(getErrorMessage(err)))
      .finally(() => setLoading(false));
  }, [connectorId]);

  const mutation = useMutation({
    mutationFn: async () => {
      if (!connectorId) throw new Error("No connector selected.");
      await connectorsApi.update(connectorId, {
        displayName: displayName.trim(),
        isEnabled,
        syncIntervalMinutes: syncInterval,
        settingsJson: replaceSettings ? buildSettingsJson(type, fields) : undefined,
      });
      // Re-test only when settings changed; otherwise report the save itself.
      if (replaceSettings) return connectorsApi.test(connectorId);
      return null;
    },
    onSuccess: (test) => {
      if (test) {
        onResult(
          test.success,
          test.errorMessage ?? (test.success ? "Connection OK." : "Connection test failed.")
        );
      } else {
        onResult(true, "Connector updated.");
      }
      onClose();
    },
    onError: (err) => setError(getErrorMessage(err)),
  });

  const handleSubmit = () => {
    if (!displayName.trim()) {
      setError("Display name is required.");
      return;
    }
    if (replaceSettings) {
      const v = validateFields(type, fields);
      if (v) {
        setError(v);
        return;
      }
    }
    setError(null);
    mutation.mutate();
  };

  return (
    <Modal open={connectorId !== null} onClose={onClose} title="Edit Connector">
      {loading ? (
        <div className="flex items-center justify-center py-8 text-slate-400">
          <Spinner size={20} />
        </div>
      ) : (
        <div className="flex flex-col gap-4">
          <p className="text-xs text-slate-500">
            Type: <span className="text-slate-300">{TYPE_LABELS[type] ?? type}</span>
          </p>

          <Input
            label="Display name"
            id="edit-connector-name"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
          />

          <label className="flex items-center gap-2 text-sm text-slate-300">
            <input
              type="checkbox"
              checked={isEnabled}
              onChange={(e) => setIsEnabled(e.target.checked)}
              className="accent-accent"
            />
            Enabled
          </label>

          <SyncIntervalSelect value={syncInterval} onChange={setSyncInterval} />

          <label className="flex items-center gap-2 text-sm text-slate-300">
            <input
              type="checkbox"
              checked={replaceSettings}
              onChange={(e) => setReplaceSettings(e.target.checked)}
              className="accent-accent"
            />
            Replace connection settings
          </label>

          {replaceSettings ? (
            <ConnectorTypeFields type={type} value={fields} onChange={patch} />
          ) : (
            <p className="text-xs text-slate-500">
              Existing settings are kept. Tick the box above to re-enter them (secrets are not
              shown for security).
            </p>
          )}

          {error && <p className="text-sm text-red-400">{error}</p>}

          <div className="mt-2 flex justify-end gap-3">
            <Button variant="ghost" size="sm" onClick={onClose} disabled={mutation.isPending}>
              Cancel
            </Button>
            <Button size="sm" onClick={handleSubmit} loading={mutation.isPending}>
              {replaceSettings ? "Save & test" : "Save"}
            </Button>
          </div>
        </div>
      )}
    </Modal>
  );
}
