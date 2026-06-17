import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Button, Input, Modal, Select } from "@/components/ui";
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

interface AddConnectorModalProps {
  open: boolean;
  onClose: () => void;
  /** Called after the connector is created and its connection test has run. */
  onResult: (success: boolean, message: string) => void;
}

export function AddConnectorModal({ open, onClose, onResult }: AddConnectorModalProps) {
  const [type, setType] = useState<ConnectorType>("googledrive");
  const [displayName, setDisplayName] = useState("");
  const [fields, setFields] = useState<ConnectorFieldsValue>(emptyFields);
  const [syncInterval, setSyncInterval] = useState(DEFAULT_SYNC_INTERVAL);
  const [error, setError] = useState<string | null>(null);

  const patch = (p: Partial<ConnectorFieldsValue>) => setFields((f) => ({ ...f, ...p }));

  const reset = () => {
    setType("googledrive");
    setDisplayName("");
    setFields(emptyFields);
    setSyncInterval(DEFAULT_SYNC_INTERVAL);
    setError(null);
  };

  const close = () => {
    reset();
    onClose();
  };

  const mutation = useMutation({
    mutationFn: async () => {
      const created = await connectorsApi.create({
        connectorType: type,
        displayName: displayName.trim(),
        settingsJson: buildSettingsJson(type, fields),
        syncIntervalMinutes: syncInterval,
      });
      // Run the connection test immediately so the user gets instant feedback.
      return connectorsApi.test(created.id);
    },
    onSuccess: (test) => {
      onResult(
        test.success,
        test.errorMessage ?? (test.success ? "Connection OK." : "Connection test failed.")
      );
      close();
    },
    onError: (err) => setError(getErrorMessage(err)),
  });

  const handleSubmit = () => {
    if (!displayName.trim()) {
      setError("Display name is required.");
      return;
    }
    const v = validateFields(type, fields);
    if (v) {
      setError(v);
      return;
    }
    setError(null);
    mutation.mutate();
  };

  return (
    <Modal open={open} onClose={close} title="Add Connector">
      <div className="flex flex-col gap-4">
        <Select
          label="Type"
          id="connector-type"
          value={type}
          onChange={(e) => setType(e.target.value as ConnectorType)}
        >
          {(Object.keys(TYPE_LABELS) as ConnectorType[]).map((t) => (
            <option key={t} value={t}>
              {TYPE_LABELS[t]}
            </option>
          ))}
        </Select>

        <Input
          label="Display name"
          id="connector-name"
          placeholder="e.g. Engineering Drive"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
        />

        <ConnectorTypeFields type={type} value={fields} onChange={patch} />

        <SyncIntervalSelect value={syncInterval} onChange={setSyncInterval} />

        {error && <p className="text-sm text-red-400">{error}</p>}

        <div className="mt-2 flex justify-end gap-3">
          <Button variant="ghost" size="sm" onClick={close} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={handleSubmit} loading={mutation.isPending}>
            Create &amp; test
          </Button>
        </div>
      </div>
    </Modal>
  );
}
