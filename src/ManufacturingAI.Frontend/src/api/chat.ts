import { apiClient, extractData } from "./client";
import type { QueryResult } from "@/types";

export const chatApi = {
  // Retrieval mode (Hybrid / Markdown) is decided server-side from tenant settings.
  query: (question: string) =>
    apiClient
      .post<{ data: QueryResult }>("/v1/query", { question })
      .then(extractData),

  feedback: (queryId: string, feedback: "Positive" | "Negative") =>
    apiClient.post(`/v1/query/${queryId}/feedback`, { feedback }),
};
