import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Eye, EyeOff, AlertTriangle } from "lucide-react";
import { Button, Select, Skeleton, ToastContainer } from "@/components/ui";
import { tenantApi } from "@/api/tenant";
import { getErrorMessage } from "@/api/client";
import { useToast } from "@/hooks/useToast";
import type { AiProvider, RetrievalMode } from "@/types";

const PROVIDERS: { value: AiProvider; label: string }[] = [
  { value: "OpenAI",      label: "OpenAI" },
  { value: "AzureOpenAI", label: "Azure OpenAI" },
  { value: "Gemini",      label: "Google Gemini" },
  { value: "Ollama",      label: "Ollama (local)" },
  { value: "Groq",        label: "Groq" },
  { value: "Claude",      label: "Claude (Anthropic)" },
];

// Providers that have their own embedding API
const EMBEDDING_CAPABLE: AiProvider[] = ["OpenAI", "AzureOpenAI", "Gemini", "Ollama"];

// Providers that need a separate embedding provider (no native embeddings)
const NEEDS_SEPARATE_EMBEDDING: AiProvider[] = ["Groq", "Claude"];

export function AiModelPage() {
  const qc = useQueryClient();
  const { toasts, toast, dismiss } = useToast();

  const { data: settings, isLoading } = useQuery({
    queryKey: ["tenant-ai-settings"],
    queryFn: tenantApi.getAiSettings,
  });

  // null = use server value; non-null = user override
  // For model/embeddingModel: "" means "auto-select first available when models load"
  const [providerOverride, setProviderOverride] = useState<AiProvider | null>(null);
  const [modelOverride, setModelOverride] = useState<string | null>(null);
  const [embeddingProviderOverride, setEmbeddingProviderOverride] = useState<AiProvider | null>(null);
  const [embeddingModelOverride, setEmbeddingModelOverride] = useState<string | null>(null);
  const [retrievalModeOverride, setRetrievalModeOverride] = useState<RetrievalMode | null>(null);
  const [apiKey, setApiKey] = useState("");
  const [embeddingApiKey, setEmbeddingApiKey] = useState("");
  const [showKey, setShowKey] = useState(false);
  const [showEmbeddingKey, setShowEmbeddingKey] = useState(false);

  // Derive form values: user override takes precedence; fall back to server state
  const provider = providerOverride ?? settings?.provider ?? "";
  const retrievalMode = retrievalModeOverride ?? settings?.retrievalMode ?? "Hybrid";

  // Lite mode is BM25-only — embeddings are not used, so hide all embedding settings.
  const isLite = retrievalMode === "Lite";
  const needsSeparateEmbedding = NEEDS_SEPARATE_EMBEDDING.includes(provider as AiProvider);

  // Embedding provider is chosen independently of the chat (LLM) provider. Fall back to
  // the LLM provider when none is stored, but only if it supports embeddings; otherwise
  // default to the first embedding-capable provider so the selector always has a valid value.
  const rawEmbeddingProvider = embeddingProviderOverride ?? settings?.embeddingProvider ?? settings?.provider ?? "";
  const embeddingProvider = (
    EMBEDDING_CAPABLE.includes(rawEmbeddingProvider as AiProvider)
      ? rawEmbeddingProvider
      : (EMBEDDING_CAPABLE.includes(provider as AiProvider) ? provider : EMBEDDING_CAPABLE[0])
  ) as AiProvider;
  // The provider that will actually serve embeddings
  const effectiveEmbeddingProvider = embeddingProvider;
  // A separate embedding API key is only needed when embeddings use a different provider
  // than the LLM (so the LLM key can't be reused) and that provider isn't keyless (Ollama).
  const embeddingUsesSeparateKey =
    !isLite && embeddingProvider !== provider && embeddingProvider !== "Ollama";

  // Chat model list
  const { data: chatModels = [], isFetching: chatModelsFetching } = useQuery({
    queryKey: ["provider-models", provider, "chat"],
    queryFn: () => tenantApi.getProviderModels(provider as string, "chat"),
    enabled: !!provider && provider !== "AzureOpenAI",
    staleTime: 5 * 60 * 1000,
  });

  // modelOverride === null → use server value; "" → auto-select first available model
  const model = modelOverride === null
    ? (settings?.model ?? "")
    : (modelOverride || chatModels[0] || "");

  // Embedding model list
  const showEmbeddingModelSelector = EMBEDDING_CAPABLE.includes(effectiveEmbeddingProvider as AiProvider);
  const { data: embeddingModels = [], isFetching: embeddingModelsFetching } = useQuery({
    queryKey: ["provider-models", effectiveEmbeddingProvider, "embedding"],
    queryFn: () => tenantApi.getProviderModels(effectiveEmbeddingProvider as string, "embedding"),
    enabled: !isLite && !!effectiveEmbeddingProvider && showEmbeddingModelSelector && effectiveEmbeddingProvider !== "AzureOpenAI",
    staleTime: 5 * 60 * 1000,
  });

  // embeddingModelOverride === null → use server value; "" → auto-select first available
  const embeddingModel = embeddingModelOverride === null
    ? (settings?.embeddingModel ?? "")
    : (embeddingModelOverride || embeddingModels[0] || "");

  const saveMut = useMutation({
    mutationFn: () => {
      const payload: {
        provider?: string; model?: string;
        embeddingProvider?: string; embeddingModel?: string; apiKey?: string;
        embeddingApiKey?: string; retrievalMode?: string;
      } = {};
      if (provider && provider !== settings?.provider)                       payload.provider          = provider;
      if (model    && model    !== settings?.model)                          payload.model             = model;
      if (embeddingProvider && embeddingProvider !== settings?.embeddingProvider) payload.embeddingProvider = embeddingProvider as string;
      if (embeddingModel && embeddingModel !== settings?.embeddingModel)     payload.embeddingModel    = embeddingModel;
      if (apiKey !== "")                                                     payload.apiKey            = apiKey;
      if (embeddingApiKey !== "")                                            payload.embeddingApiKey   = embeddingApiKey;
      if (retrievalMode !== settings?.retrievalMode)                         payload.retrievalMode     = retrievalMode;
      return tenantApi.updateAiSettings(payload);
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["tenant-ai-settings"] });
      void qc.invalidateQueries({ queryKey: ["provider-models", provider, "chat"] });
      void qc.invalidateQueries({ queryKey: ["provider-models", effectiveEmbeddingProvider, "embedding"] });
      toast.success("Settings saved");
      setApiKey("");
      setEmbeddingApiKey("");
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  if (isLoading) {
    return (
      <div className="flex flex-col gap-4 max-w-lg">
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-32" />
      </div>
    );
  }

  if (!settings) return null;

  const isAzureLLM       = provider === "AzureOpenAI";
  const isAzureEmbedding = effectiveEmbeddingProvider === "AzureOpenAI";

  const isDirty =
    provider !== settings.provider ||
    model !== settings.model ||
    embeddingProvider !== settings.embeddingProvider ||
    embeddingModel !== (settings.embeddingModel ?? "") ||
    apiKey !== "" ||
    embeddingApiKey !== "" ||
    retrievalMode !== settings.retrievalMode;

  const handleProviderChange = (next: AiProvider) => {
    setProviderOverride(next);
    setModelOverride(""); // "" triggers auto-select when chatModels load
    // Embedding provider/model are independent — changing the chat provider no longer
    // overrides them. Pick them separately in the Embedding Provider/Model selectors.
  };

  const handleEmbeddingProviderChange = (next: AiProvider) => {
    setEmbeddingProviderOverride(next);
    setEmbeddingModelOverride(""); // "" triggers auto-select
  };

  return (
    <div className="max-w-lg space-y-4">
      <div>
        <h2 className="mb-1 text-base font-semibold text-slate-100">AI Model</h2>
        <p className="text-sm text-slate-400">
          Configure the LLM provider, model, and API key used for all queries.
        </p>
      </div>

      {/* Retrieval Mode */}
      <div>
        <Select
          label="Retrieval Mode"
          id="retrieval-mode-select"
          value={retrievalMode}
          onChange={(e) => setRetrievalModeOverride(e.target.value as RetrievalMode)}
        >
          <option value="Hybrid">Hybrid RAG (Qdrant + BM25)</option>
          <option value="Lite">Lite (BM25 only, no embeddings)</option>
        </Select>
        <p className="mt-1 text-xs text-slate-500">
          Lite mode skips the embedding model and Qdrant — documents are chunked into Postgres
          and answered via keyword/BM25 search.
        </p>

        {retrievalMode !== settings.retrievalMode && (
          <div className="mt-2 flex gap-2 rounded-md border border-amber-600/40 bg-amber-900/30 px-3 py-2 text-xs text-amber-200">
            <AlertTriangle size={15} className="mt-0.5 shrink-0" />
            <span>
              Switching retrieval mode only affects documents ingested <strong>after</strong> you
              save. Existing documents are not re-processed — re-ingest them to use the new mode.
            </span>
          </div>
        )}
      </div>

      {/* LLM Provider */}
      <Select
        label="Provider"
        id="provider-select"
        value={provider}
        onChange={(e) => handleProviderChange(e.target.value as AiProvider)}
      >
        {PROVIDERS.map((p) => (
          <option key={p.value} value={p.value}>{p.label}</option>
        ))}
      </Select>

      {/* Chat Model */}
      {isAzureLLM ? (
        <div>
          <label htmlFor="model-input" className="mb-1.5 block text-sm font-medium text-slate-300">
            Deployment Name
          </label>
          <input id="model-input" type="text" value={model} onChange={(e) => setModelOverride(e.target.value)}
            placeholder="e.g. gpt-4o-mini"
            className="w-full rounded-md border border-surface-border bg-surface px-3 py-2 text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
          <p className="mt-1 text-xs text-slate-500">Enter your Azure OpenAI deployment name.</p>
        </div>
      ) : (
        <Select
          label={chatModelsFetching ? "Model (loading…)" : "Model"}
          id="model-select"
          value={model}
          onChange={(e) => setModelOverride(e.target.value)}
          disabled={chatModelsFetching}
        >
          {chatModelsFetching && <option value="">Loading models…</option>}
          {!chatModelsFetching && chatModels.map((m) => <option key={m} value={m}>{m}</option>)}
          {!chatModelsFetching && chatModels.length === 0 && (
            <option value={model || ""}>{model || "No models available"}</option>
          )}
        </Select>
      )}

      {/* Embedding Provider — chosen independently of the chat provider */}
      {!isLite && (
        <div>
          <Select
            label="Embedding Provider"
            id="embedding-provider-select"
            value={embeddingProvider}
            onChange={(e) => handleEmbeddingProviderChange(e.target.value as AiProvider)}
          >
            {EMBEDDING_CAPABLE.map((p) => (
              <option key={p} value={p}>{PROVIDERS.find(x => x.value === p)?.label ?? p}</option>
            ))}
          </Select>
          <p className="mt-1 text-xs text-slate-500">
            {needsSeparateEmbedding
              ? `${provider} does not provide embeddings — select a provider for document indexing.`
              : "Used to index and search documents. Can differ from the chat provider — e.g. local Ollama embeddings with a cloud chat model."}
          </p>

          {embeddingProvider !== settings.embeddingProvider && (
            <div className="mt-2 flex gap-2 rounded-md border border-amber-600/40 bg-amber-900/30 px-3 py-2 text-xs text-amber-200">
              <AlertTriangle size={15} className="mt-0.5 shrink-0" />
              <span>
                Changing the embedding provider changes how documents are indexed. Existing
                documents must be <strong>re-ingested</strong> to be searchable with the new provider.
              </span>
            </div>
          )}
        </div>
      )}

      {/* Embedding Model */}
      {!isLite && showEmbeddingModelSelector && (
        isAzureEmbedding ? (
          <div>
            <label htmlFor="embedding-input" className="mb-1.5 block text-sm font-medium text-slate-300">
              Embedding Deployment Name
            </label>
            <input id="embedding-input" type="text" value={embeddingModel}
              onChange={(e) => setEmbeddingModelOverride(e.target.value)}
              placeholder="e.g. text-embedding-3-small"
              className="w-full rounded-md border border-surface-border bg-surface px-3 py-2 text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
          </div>
        ) : (
          <Select
            label={embeddingModelsFetching ? "Embedding Model (loading…)" : "Embedding Model"}
            id="embedding-select"
            value={embeddingModel}
            onChange={(e) => setEmbeddingModelOverride(e.target.value)}
            disabled={embeddingModelsFetching}
          >
            {embeddingModelsFetching && <option value="">Loading models…</option>}
            {!embeddingModelsFetching && embeddingModels.map((m) => <option key={m} value={m}>{m}</option>)}
            {!embeddingModelsFetching && embeddingModels.length === 0 && (
              <option value={embeddingModel || ""}>{embeddingModel || "No models available"}</option>
            )}
          </Select>
        )
      )}

      {/* API Key — hidden for Ollama (local, no key needed) */}
      {provider !== "Ollama" && (
        <div>
          <label htmlFor="api-key" className="mb-1.5 block text-sm font-medium text-slate-300">
            API Key
            {settings.apiKeySet && (
              <span className="ml-2 text-xs font-normal text-slate-500">current: {settings.apiKeyMasked}</span>
            )}
          </label>
          <div className="relative">
            <input id="api-key" type={showKey ? "text" : "password"} value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder={settings.apiKeySet ? "Enter new key to replace" : "Enter API key"}
              className="w-full rounded-md border border-surface-border bg-surface px-3 py-2 pr-10 text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
            <button type="button" onClick={() => setShowKey((v) => !v)}
              className="absolute right-2.5 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-200">
              {showKey ? <EyeOff size={15} /> : <Eye size={15} />}
            </button>
          </div>
        </div>
      )}

      {/* Embedding API Key — only shown when embeddings use a different (non-keyless) provider */}
      {embeddingUsesSeparateKey && (
        <div>
          <label htmlFor="embedding-api-key" className="mb-1.5 block text-sm font-medium text-slate-300">
            Embedding API Key
            <span className="ml-2 text-xs font-normal text-slate-500">({embeddingProvider})</span>
            {settings.embeddingApiKeySet && (
              <span className="ml-2 text-xs font-normal text-slate-500">current: {settings.embeddingApiKeyMasked}</span>
            )}
          </label>
          <div className="relative">
            <input
              id="embedding-api-key"
              type={showEmbeddingKey ? "text" : "password"}
              value={embeddingApiKey}
              onChange={(e) => setEmbeddingApiKey(e.target.value)}
              placeholder={settings.embeddingApiKeySet ? "Enter new key to replace" : `Enter ${embeddingProvider} API key`}
              className="w-full rounded-md border border-surface-border bg-surface px-3 py-2 pr-10 text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
            <button type="button" onClick={() => setShowEmbeddingKey((v) => !v)}
              className="absolute right-2.5 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-200">
              {showEmbeddingKey ? <EyeOff size={15} /> : <Eye size={15} />}
            </button>
          </div>
        </div>
      )}

      <div className="flex gap-3 pt-2">
        <Button
          onClick={() => saveMut.mutate()}
          disabled={!isDirty || chatModelsFetching || embeddingModelsFetching}
          loading={saveMut.isPending}
        >
          Save changes
        </Button>
        {isDirty && (
          <Button variant="ghost" onClick={() => {
            setProviderOverride(null);
            setModelOverride(null);
            setEmbeddingProviderOverride(null);
            setEmbeddingModelOverride(null);
            setRetrievalModeOverride(null);
            setApiKey("");
            setEmbeddingApiKey("");
          }}>
            Cancel
          </Button>
        )}
      </div>

      <ToastContainer toasts={toasts} onDismiss={dismiss} />
    </div>
  );
}
