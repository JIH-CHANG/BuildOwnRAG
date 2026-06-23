import { Input, Select } from "@/components/ui";
import { cn } from "@/lib/utils";

export type ConnectorType = "googledrive" | "folder" | "sharepoint";

/** Auto-sync cadence presets (minutes). 0 disables the schedule (manual only). */
export const SYNC_INTERVAL_OPTIONS: { value: number; label: string }[] = [
  { value: 0, label: "Manual only" },
  { value: 60, label: "Every hour" },
  { value: 360, label: "Every 6 hours" },
  { value: 720, label: "Every 12 hours" },
  { value: 1440, label: "Every day" },
];

export const DEFAULT_SYNC_INTERVAL = 60;

export function syncIntervalLabel(minutes: number): string {
  return (
    SYNC_INTERVAL_OPTIONS.find((o) => o.value === minutes)?.label ??
    `Every ${minutes} minutes`
  );
}

interface SyncIntervalSelectProps {
  value: number;
  onChange: (minutes: number) => void;
}

export function SyncIntervalSelect({ value, onChange }: SyncIntervalSelectProps) {
  return (
    <div className="flex flex-col gap-1">
      <Select
        label="Auto-sync frequency"
        id="sync-interval"
        value={String(value)}
        onChange={(e) => onChange(Number(e.target.value))}
      >
        {SYNC_INTERVAL_OPTIONS.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </Select>
      <p className="text-xs text-slate-500">
        How often the connector automatically checks the source for new or changed
        files. You can still sync manually at any time.
      </p>
    </div>
  );
}

export const TYPE_LABELS: Record<ConnectorType, string> = {
  googledrive: "Google Drive (service account)",
  folder: "Local Folder",
  sharepoint: "SharePoint (Entra app)",
};

export interface ConnectorFieldsValue {
  // Google Drive
  serviceAccountJson: string;
  rootFolderId: string;
  // Local folder
  folderPath: string;
  watchMode: boolean;
  // SharePoint
  tenantId: string;
  clientId: string;
  clientSecret: string;
  siteUrl: string;
  driveName: string;
  // Shared
  includeSubfolders: boolean;
  maxFileSizeMB: number;
}

export const emptyFields: ConnectorFieldsValue = {
  serviceAccountJson: "",
  rootFolderId: "",
  folderPath: "",
  watchMode: false,
  tenantId: "",
  clientId: "",
  clientSecret: "",
  siteUrl: "",
  driveName: "",
  includeSubfolders: true,
  maxFileSizeMB: 50,
};

/** Accepts a bare folder ID or a full Drive URL (".../folders/<id>?...") and returns the ID. */
export function extractFolderId(input: string): string {
  const s = input.trim();
  const m = s.match(/\/folders\/([^/?#]+)/);
  return m ? m[1] : s;
}

export function buildSettingsJson(type: ConnectorType, v: ConnectorFieldsValue): string {
  let settings: Record<string, unknown>;
  if (type === "googledrive") {
    settings = {
      serviceAccountJson: v.serviceAccountJson,
      rootFolderId: extractFolderId(v.rootFolderId),
      includeSubfolders: v.includeSubfolders,
      maxFileSizeMB: v.maxFileSizeMB,
    };
  } else if (type === "sharepoint") {
    settings = {
      tenantId: v.tenantId.trim(),
      clientId: v.clientId.trim(),
      clientSecret: v.clientSecret,
      siteUrl: v.siteUrl.trim(),
      driveName: v.driveName.trim(),
      maxFileSizeMB: v.maxFileSizeMB,
    };
  } else {
    settings = {
      folderPath: v.folderPath.trim(),
      includeSubfolders: v.includeSubfolders,
      maxFileSizeMB: v.maxFileSizeMB,
      watchMode: v.watchMode,
    };
  }
  return JSON.stringify(settings);
}

export function validateFields(type: ConnectorType, v: ConnectorFieldsValue): string | null {
  if (type === "googledrive") {
    if (!v.serviceAccountJson.trim()) return "Service account JSON is required.";
    try {
      JSON.parse(v.serviceAccountJson);
    } catch {
      return "Service account JSON is not valid JSON.";
    }
    if (!v.rootFolderId.trim()) return "Folder ID is required.";
  } else if (type === "sharepoint") {
    if (!v.tenantId.trim()) return "Tenant ID is required.";
    if (!v.clientId.trim()) return "Client ID is required.";
    if (!v.clientSecret.trim()) return "Client secret is required.";
    if (!v.siteUrl.trim()) return "Site URL is required.";
    try {
      const u = new URL(v.siteUrl);
      if (!u.hostname) return "Site URL is not a valid URL.";
    } catch {
      return "Site URL is not a valid URL.";
    }
  } else if (!v.folderPath.trim()) {
    return "Folder path is required.";
  }
  return null;
}

interface ConnectorTypeFieldsProps {
  type: ConnectorType;
  value: ConnectorFieldsValue;
  onChange: (patch: Partial<ConnectorFieldsValue>) => void;
}

export function ConnectorTypeFields({ type, value, onChange }: ConnectorTypeFieldsProps) {
  const textareaClass = cn(
    "w-full rounded-md border border-surface-border bg-surface-muted",
    "px-3 py-2 text-sm text-slate-200 placeholder:text-slate-500 font-mono",
    "focus:outline-none focus:ring-2 focus:ring-accent/50"
  );

  return (
    <>
      {type === "googledrive" && (
        <>
          <div className="flex flex-col gap-1">
            <label htmlFor="sa-json" className="text-sm text-slate-400">
              Service account JSON key
            </label>
            <textarea
              id="sa-json"
              rows={5}
              placeholder='{ "type": "service_account", ... }'
              value={value.serviceAccountJson}
              onChange={(e) => onChange({ serviceAccountJson: e.target.value })}
              className={textareaClass}
            />
            <p className="text-xs text-slate-500">
              Paste the full contents of the downloaded key file. Share the target
              Drive folder with the service account email first.
            </p>
          </div>
          <Input
            label="Folder ID or URL"
            id="root-folder-id"
            placeholder="1AbC… or https://drive.google.com/drive/folders/1AbC…"
            value={value.rootFolderId}
            onChange={(e) => onChange({ rootFolderId: e.target.value })}
          />
        </>
      )}

      {type === "sharepoint" && (
        <>
          <Input
            label="Tenant ID"
            id="sp-tenant"
            placeholder="00000000-0000-0000-0000-000000000000 or contoso.onmicrosoft.com"
            value={value.tenantId}
            onChange={(e) => onChange({ tenantId: e.target.value })}
          />
          <Input
            label="Client (Application) ID"
            id="sp-client"
            placeholder="00000000-0000-0000-0000-000000000000"
            value={value.clientId}
            onChange={(e) => onChange({ clientId: e.target.value })}
          />
          <Input
            label="Client secret"
            id="sp-secret"
            type="password"
            placeholder="••••••••"
            value={value.clientSecret}
            onChange={(e) => onChange({ clientSecret: e.target.value })}
          />
          <Input
            label="Site URL"
            id="sp-site"
            placeholder="https://contoso.sharepoint.com/sites/marketing"
            value={value.siteUrl}
            onChange={(e) => onChange({ siteUrl: e.target.value })}
          />
          <div className="flex flex-col gap-1">
            <Input
              label="Document library name (optional)"
              id="sp-drive"
              placeholder="Documents"
              value={value.driveName}
              onChange={(e) => onChange({ driveName: e.target.value })}
            />
            <p className="text-xs text-slate-500">
              Leave empty to use the site's default document library.
              The Entra app needs Sites.Read.All and Files.Read.All (Application) with admin consent.
            </p>
          </div>
        </>
      )}

      {type === "folder" && (
        <Input
          label="Folder path"
          id="folder-path"
          placeholder="e.g. /data/docs or C:\\docs"
          value={value.folderPath}
          onChange={(e) => onChange({ folderPath: e.target.value })}
        />
      )}

      <div className="flex items-center gap-4">
        <Input
          label="Max file size (MB)"
          id="max-size"
          type="number"
          min={1}
          value={value.maxFileSizeMB}
          onChange={(e) => onChange({ maxFileSizeMB: Number(e.target.value) })}
          className="w-32"
        />
        {type !== "sharepoint" && (
          <label className="mt-5 flex items-center gap-2 text-sm text-slate-300">
            <input
              type="checkbox"
              checked={value.includeSubfolders}
              onChange={(e) => onChange({ includeSubfolders: e.target.checked })}
              className="accent-accent"
            />
            Include subfolders
          </label>
        )}
      </div>

      {type === "folder" && (
        <label className="flex items-center gap-2 text-sm text-slate-300">
          <input
            type="checkbox"
            checked={value.watchMode}
            onChange={(e) => onChange({ watchMode: e.target.checked })}
            className="accent-accent"
          />
          Watch folder for changes (immediate sync)
        </label>
      )}
    </>
  );
}
