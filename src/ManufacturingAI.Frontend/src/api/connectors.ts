import { apiClient, extractData } from "./client";

export interface ConnectorConfig {
  id: string;
  tenantId: string;
  connectorType: string;
  displayName: string;
  isEnabled: boolean;
  /** Auto-sync cadence in minutes; 0 means manual only. */
  syncIntervalMinutes: number;
  createdAt: string;
  updatedAt: string;
}

export interface CreateConnectorRequest {
  connectorType: string;
  displayName: string;
  /** JSON string of the type-specific settings; the server encrypts it at rest. */
  settingsJson: string;
  /** Auto-sync cadence in minutes; 0 means manual only. */
  syncIntervalMinutes: number;
}

export interface UpdateConnectorRequest {
  displayName: string;
  isEnabled: boolean;
  /** Optional: omit/blank to preserve existing settings (avoids re-entering secrets). */
  settingsJson?: string;
  /** Auto-sync cadence in minutes; 0 means manual only. */
  syncIntervalMinutes: number;
}

export interface ConnectorTestResult {
  success: boolean;
  /** Carries the success message on success, or the failure reason on error. */
  errorMessage?: string;
}

export const connectorsApi = {
  list: () =>
    apiClient
      .get<{ data: ConnectorConfig[] }>("/v1/connectors")
      .then(extractData),

  get: (id: string) =>
    apiClient
      .get<{ data: ConnectorConfig }>(`/v1/connectors/${id}`)
      .then(extractData),

  create: (req: CreateConnectorRequest) =>
    apiClient
      .post<{ data: ConnectorConfig }>("/v1/connectors", req)
      .then(extractData),

  update: (id: string, req: UpdateConnectorRequest) =>
    apiClient.put(`/v1/connectors/${id}`, req),

  test: (id: string) =>
    apiClient
      .post<{ data: ConnectorTestResult }>(`/v1/connectors/${id}/test`)
      .then(extractData),

  remove: (id: string) => apiClient.delete(`/v1/connectors/${id}`),
};
