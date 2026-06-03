import { apiClient, extractData } from "./client";
import type { Document, DocumentListResult } from "@/types";

export const documentsApi = {
  list: (page = 1, pageSize = 20, status?: string) => {
    const params: Record<string, string> = {
      page: String(page),
      pageSize: String(pageSize),
    };
    if (status) params.status = status;
    return apiClient
      .get<{ data: DocumentListResult }>("/v1/documents", { params })
      .then(extractData);
  },

  get: (id: string) =>
    apiClient
      .get<{ data: Document }>(`/v1/documents/${id}`)
      .then(extractData),

  delete: (id: string) => apiClient.delete(`/v1/documents/${id}`),

  upload: (files: File[]) => {
    const form = new FormData();
    files.forEach((f) => form.append("files", f));
    return apiClient.post("/v1/ingest/upload", form, {
      headers: { "Content-Type": "multipart/form-data" },
    });
  },
};
