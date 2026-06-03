import { useState, useEffect } from "react";
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

  const [provider, setProvider] = useState<AiProvider | "">("");
  const [model, setModel] = useState("");
  const [embeddingProvider, setEmbeddingProvider] = useState<AiProvider | "">("");
  const [embeddingModel, setEmbeddingModel] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [embeddingApiKey, setEmbeddingApiKey] = useState("");
  const [showKey, setShowKey] = useState(false);
  const [showEmbeddingKey, setShowEmbeddingKey] = useState(false);
  const [retrievalMode, setRetrievalMode] = useState<RetrievalMode>("Hybrid");

  // Initialise from server on first load
  useEffect(() => {
    if (settings && provider === "") {
      setProvider(settings.provider);
      setModel(settings.model);
      setEmbeddingProvider(settings.embeddingProvider ?? settings.provider);
      setEmbeddingModel(settings.embeddingModel ?? "");
      setRetrievalMode(settings.retrievalMode ?? "Hybrid");
    }
  }, [settings]);

  // Markdown mode is BM25-only — embeddings are not used, so hide all embedding settings.
  const isMarkdown = retrievalMode === "Markdown";
  const needsSeparateEmbedding = NEEDS_SEPARATE_EMBEDDING.includes(provider as AiProvider);
  // The provider that will actually serve embeddings
  const effectiveEmbeddingProvider = needsSeparateEmbedding ? embeddingProvider : provider;

  // Chat model list
  const { data: chatModels = [], isFetching: chatModelsFetching } = useQuery({
    queryKey: ["provider-models", provider, "chat"],
    queryFn: () => tenantApi.getProviderModels(provider as string, "chat"),
    enabled: !!provider && provider !== "AzureOpenAI",
    staleTime: 5 * 60 * 1000,
  });

  // Embedding model list
  const showEmbeddingModelSelector = EMBEDDING_CAPABLE.includes(effectiveEmbeddingProvider as AiProvider);
  const { data: embeddingModels = [], isFetching: embeddingModelsFetching } = useQuery({
    queryKey: ["provider-models", effectiveEmbeddingProvider, "embedding"],
    queryFn: () => tenantApi.getProviderModels(effectiveEmbeddingProvider as string, "embedding"),
    enabled: !isMarkdown && !!effectiveEmbeddingProvider && showEmbeddingModelSelector && effectiveEmbeddingProvider !== "AzureOpenAI",
    staleTime: 5 * 60 * 1000,
  });

  // Auto-select first chat model on provider switch
  useEffect(() => {
    if (model === "" && chatModels.length > 0) setModel(chatModels[0]);
  }, [chatModels]);

  // Auto-select first embedding model on embedding provider switch
  useEffect(() => {
    if (embeddingModel === "" && embeddingModels.length > 0) setEmbeddingModel(embeddingModels[0]);
  }, [embeddingModels]);

  const saveMut = useMutation({
    mutationFn: () => {
      const payload: {
        provider?: string; model?: string;
        embeddingProvider?: string; embeddingModel?: string; apiKey?: string;
        embeddingApiKey?: string; retrievalMode?: string;
      } = {};
      if (provider && provider !== settings?.provider)                       payload.provider          = provider;
      if (model    && model    !== settings?.model)                          payload.model             = model;
      const saveEmbProv = needsSeparateEmbedding ? embeddingProvider : provider;
      if (saveEmbProv && saveEmbProv !== settings?.embeddingProvider)        payload.embeddingProvider = saveEmbProv as string;
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
    (needsSeparateEmbedding ? embeddingProvider : provider) !== settings.embeddingProvider ||
    embeddingModel !== (settings.embeddingModel ?? "") ||
    apiKey !== "" ||
    embeddingApiKey !== "" ||
    retrievalMode !== settings.retrievalMode;

  const handleProviderChange = (next: AiProvider) => {
    setProvider(next);
    setModel("");
    // If the new provider supports embeddings natively, use it; otherwise keep current embedding provider
    if (EMBEDDING_CAPABLE.includes(next)) {
      setEmbeddingProvider(next);
      setEmbeddingModel("");
    }
  };

  const handleEmbeddingProviderChange = (next: AiProvider) => {
    setEmbeddingProvider(next);
    setEmbeddingModel("");
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
          onChange={(e) => setRetrievalMode(e.target.value as RetrievalMode)}
        >
          <option value="Hybrid">Hybrid RAG (Qdrant + BM25)</option>
          <option value="Markdown">Markdown (BM25 only, no embeddings)</option>
        </Select>
        <p className="mt-1 text-xs text-slate-500">
          Markdown mode skips the embedding model and Qdrant — documents are chunked into Postgres
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
          <input id="model-input" type="text" value={model} onChange={(e) => setModel(e.target.value)}
            placeholder="e.g. gpt-4o-mini"
            className="w-full rounded-md border border-surface-border bg-surface px-3 py-2 text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
          <p className="mt-1 text-xs text-slate-500">Enter your Azure OpenAI deployment name.</p>
        </div>
      ) : (
        <Select
          label={chatModelsFetching ? "Model (loading…)" : "Model"}
          id="model-select"
          value={model}
          onChange={(e) => setModel(e.target.value)}
          disabled={chatModelsFetching}
        >
          {chatModelsFetching && <option value="">Loading models…</option>}
          {!chatModelsFetching && chatModels.map((m) => <option key={m} value={m}>{m}</option>)}
          {!chatModelsFetching && chatModels.length === 0 && (
            <option value={model || ""}>{model || "No models available"}</option>
          )}
        </Select>
      )}

      {/* Embedding Provider — shown when LLM provider has no embeddings (e.g. Groq) */}
      {!isMarkdown && needsSeparateEmbedding && (
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
            {provider === "Claude" ? "Claude" : "Groq"} does not provide embeddings — select a separate provider for document indexing.
          </p>
        </div>
      )}

      {/* Embedding Model */}
      {!isMarkdown && showEmbeddingModelSelector && (
        isAzureEmbedding ? (
          <div>
            <label htmlFor="embedding-input" className="mb-1.5 block text-sm font-medium text-slate-300">
              Embedding Deployment Name
            </label>
            <input id="embedding-input" type="text" value={embeddingModel}
              onChange={(e) => setEmbeddingModel(e.target.value)}
              placeholder="e.g. text-embedding-3-small"
              className="w-full rounded-md border border-surface-border bg-surface px-3 py-2 text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
          </div>
        ) : (
          <Select
            label={embeddingModelsFetching ? "Embedding Model (loading…)" : "Embedding Model"}
            id="embedding-select"
            value={embeddingModel}
            onChange={(e) => setEmbeddingModel(e.target.value)}
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

      {/* API Key */}
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
        {provider === "Ollama" && (
          <p className="mt-1 text-xs text-slate-500">Ollama runs locally — no API key required.</p>
        )}
      </div>

      {/* Embedding API Key — only shown when using a different embedding provider */}
      {!isMarkdown && needsSeparateEmbedding && embeddingProvider && (
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
            setProvider(settings.provider);
            setModel(settings.model);
            setEmbeddingProvider(settings.embeddingProvider ?? settings.provider);
            setEmbeddingModel(settings.embeddingModel ?? "");
            setApiKey("");
            setEmbeddingApiKey("");
            setRetrievalMode(settings.retrievalMode ?? "Hybrid");
          }}>
            Cancel
          </Button>
        )}
      </div>

      <ToastContainer toasts={toasts} onDismiss={dismiss} />
    </div>
  );
}
