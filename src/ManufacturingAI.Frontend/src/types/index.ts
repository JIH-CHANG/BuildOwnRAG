export interface ApiResponse<T = void> {
  success: boolean;
  data: T;
  error?: string;
  traceId?: string;
}

export type UserRole = "Employee" | "TenantAdmin";
export type Plan = "Free" | "Pro";

export interface AuthUser {
  id: string;
  email: string;
  role: UserRole;
  tenantId: string;
  plan: Plan;
}

export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: {
    id: string;
    email: string;
    role: UserRole;
    tenantId: string;
    plan: Plan;
  };
}

export interface AuthState {
  token: string | null;
  user: AuthUser | null;
}

export interface QuerySource {
  documentId: string;
  title: string;
  sourceType: string;
  pageNumber?: number;
  relevantExcerpt: string;
}

export interface QueryResult {
  id: string;
  answer: string;
  confidenceScore: number;
  sources: QuerySource[];
  isFromCache: boolean;
  isFromFallback: boolean;
  latencyMs: number;
}

export type QueryFeedback = "Positive" | "Negative";

export interface ChatMessage {
  id: string;
  role: "user" | "assistant";
  content: string;
  sources?: QuerySource[];
  // Set once the answer finishes streaming; identifies the QueryLog for feedback.
  queryId?: string;
  feedback?: QueryFeedback;
  createdAt: string;
}

// Retrieval pipeline: Hybrid RAG (Qdrant + BM25) or Lite (BM25-only, no embeddings).
export type RetrievalMode = "Hybrid" | "Lite";

export interface Conversation {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
}

export type DocumentStatus = "Pending" | "Processing" | "Indexed" | "Failed";

export interface Document {
  id: string;
  title: string;
  filePath: string;
  mimeType: string;
  status: DocumentStatus;
  fileSizeBytes: number;
  createdAt: string;
  updatedAt: string;
}

export interface DocumentListResult {
  items: Document[];
  total: number;
  page: number;
  pageSize: number;
}

export type AiProvider = "OpenAI" | "AzureOpenAI" | "Gemini" | "Ollama" | "Groq" | "Claude";

export interface TenantAiSettings {
  provider: AiProvider;
  model: string;
  embeddingProvider: AiProvider;
  embeddingModel: string;
  apiKeySet: boolean;
  apiKeyMasked: string;
  embeddingApiKeySet: boolean;
  embeddingApiKeyMasked: string;
  retrievalMode: RetrievalMode;
}

export interface SystemPromptSettings {
  // The tenant's custom prompt; empty string means the default is in use.
  systemPrompt: string;
  // The built-in default prompt, for display and "reset to default".
  defaultPrompt: string;
}

export interface OllamaModel {
  name: string;
  size: number;
}

export type UserStatus = "Active" | "Inactive";

export interface TenantUser {
  id: string;
  email: string;
  role: UserRole;
  status: UserStatus;
  createdAt: string;
}

export interface UserListResult {
  items: TenantUser[];
  total: number;
}

export interface CreateUserRequest {
  email: string;
  password: string;
  role: UserRole;
}

export interface ApiKey {
  id: string;
  name: string;
  keyPrefix: string;
  createdAt: string;
  lastUsedAt?: string;
}

export interface CreateApiKeyResponse {
  id: string;
  name: string;
  key: string;
}

export type SyncStatus = "Pending" | "Running" | "Completed" | "Failed";

export interface ConnectorSyncStatus {
  connectorId: string;
  displayName: string;
  connectorType: string;
  status: SyncStatus;
  lastSyncedAt?: string;
  errorMessage?: string;
}

export interface SyncStatusResult {
  tenantId: string;
  totalConnectors: number;
  runningJobs: number;
  connectors: ConnectorSyncStatus[];
}

// ── Analytics ────────────────────────────────────────────────────────────────

export type AnalyticsRange = "today" | "week" | "month";

export interface AnalyticsOverview {
  totalQueries: number;
  positiveFeedbackRate: number;
  negativeFeedbackRate: number;
  totalDocuments: number;
  indexedDocuments: number;
  failedDocuments: number;
  averageLatencyMs: number;
  averageConfidenceScore: number;
}

export interface TopQuery {
  question: string;
  count: number;
  averageConfidenceScore: number;
  positiveRate: number;
}

export interface ConfidenceBucket {
  range: string;
  count: number;
  percentage: number;
}

export interface ConfidenceDistribution {
  buckets: ConfidenceBucket[];
}

// date is a DateOnly serialized as "2026-06-05".
export interface DailyQueryCount {
  date: string;
  count: number;
}
