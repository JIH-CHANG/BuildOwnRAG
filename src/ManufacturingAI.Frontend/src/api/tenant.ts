import { apiClient, extractData } from "./client";
import type { TenantAiSettings, OllamaModel, SystemPromptSettings } from "@/types";

export const tenantApi = {
  getAiSettings: () =>
    apiClient
      .get<{ data: TenantAiSettings }>("/v1/tenant/settings/ai-model")
      .then(extractData),

  updateAiSettings: (payload: { provider?: string; model?: string; embeddingProvider?: string; embeddingModel?: string; apiKey?: string; embeddingApiKey?: string; retrievalMode?: string }) =>
    apiClient.patch("/v1/tenant/settings/ai-model", payload),

  getOllamaModels: () =>
    apiClient
      .get<{ data: OllamaModel[] }>("/v1/tenant/settings/ollama-models")
      .then(extractData),

  getProviderModels: (provider: string, type: "chat" | "embedding" = "chat") =>
    apiClient
      .get<{ data: string[] }>(`/v1/tenant/settings/provider-models?provider=${encodeURIComponent(provider.toLowerCase())}&type=${type}`)
      .then(extractData),

  getSystemPrompt: () =>
    apiClient
      .get<{ data: SystemPromptSettings }>("/v1/tenant/settings/system-prompt")
      .then(extractData),

  updateSystemPrompt: (systemPrompt: string) =>
    apiClient.patch("/v1/tenant/settings/system-prompt", { systemPrompt }),
};
