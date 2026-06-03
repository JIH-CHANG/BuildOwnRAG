import { apiClient, extractData } from "./client";
import type { SyncStatusResult } from "@/types";

export const ingestApi = {
  getStatus: () =>
    apiClient
      .get<{ data: SyncStatusResult }>("/v1/ingest/status")
      .then(extractData),

  triggerSync: (connectorId?: string) =>
    apiClient.post("/v1/ingest/trigger", connectorId ? { connectorId } : {}),
};
