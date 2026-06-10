import { apiClient, extractData } from "./client";
import { useAuthStore } from "@/stores/authStore";
import type { QueryResult, QuerySource } from "@/types";

// Terminal metadata delivered by the stream once the answer is fully generated.
export interface QueryStreamMeta {
  sources: QuerySource[];
  queryId?: string;
  confidenceScore?: number;
}

export interface QueryStreamHandlers {
  onToken: (token: string) => void;
  onComplete: (meta: QueryStreamMeta) => void;
}

// Non-OK response from the stream endpoint, carrying the HTTP status so the
// UI can distinguish e.g. 503 (index migration in progress) from other errors.
export class QueryStreamError extends Error {
  constructor(
    message: string,
    public readonly status: number,
  ) {
    super(message);
    this.name = "QueryStreamError";
  }
}

export const isMigrationInProgress = (err: unknown): err is QueryStreamError =>
  err instanceof QueryStreamError && err.status === 503;

// One SSE payload from /v1/query/stream. Either `token` (an incremental
// answer chunk) or `sources` (the terminal event) is populated.
interface QueryStreamEvent {
  token?: string | null;
  sources?: QuerySource[] | null;
  queryId?: string | null;
  confidenceScore?: number | null;
}

function dispatchEvent(raw: string, handlers: QueryStreamHandlers): void {
  let evt: QueryStreamEvent;
  try {
    evt = JSON.parse(raw);
  } catch {
    return; // ignore malformed frames
  }
  if (evt.token) handlers.onToken(evt.token);
  if (evt.sources) {
    handlers.onComplete({
      sources: evt.sources,
      queryId: evt.queryId ?? undefined,
      confidenceScore: evt.confidenceScore ?? undefined,
    });
  }
}

// Pull the payload out of an SSE frame (one or more "data:" lines).
function frameData(frame: string): string {
  return frame
    .split("\n")
    .filter((line) => line.startsWith("data:"))
    .map((line) => line.slice(5).trim())
    .join("");
}

export const chatApi = {
  // Retrieval mode (Hybrid / Lite) is decided server-side from tenant settings.
  query: (question: string) =>
    apiClient
      .post<{ data: QueryResult }>("/v1/query", { question })
      .then(extractData),

  feedback: (queryId: string, feedback: "Positive" | "Negative") =>
    apiClient.post(`/v1/query/${queryId}/feedback`, { feedback }),

  // SSE streaming: reads /v1/query/stream as text/event-stream and replays the
  // server's QueryStreamEvent frames — answer tokens, then a final event with
  // the cited sources. axios can't stream a response body, so we use fetch.
  streamQuery: async (
    question: string,
    handlers: QueryStreamHandlers,
    signal?: AbortSignal,
  ): Promise<void> => {
    const token = useAuthStore.getState().token;
    const res = await fetch("/api/v1/query/stream", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: JSON.stringify({ question }),
      signal,
    });

    if (res.status === 401) {
      useAuthStore.getState().logout();
      window.location.href = "/login";
      return;
    }
    if (!res.ok || !res.body) {
      // Error responses are JSON ApiResponse bodies, not SSE — surface the
      // server's error message when one is present.
      let message = `Query failed (${res.status})`;
      try {
        const body = (await res.json()) as { error?: string };
        if (body.error) message = body.error;
      } catch {
        // non-JSON body: keep the generic message
      }
      throw new QueryStreamError(message, res.status);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        // SSE frames are separated by a blank line.
        let sep: number;
        while ((sep = buffer.indexOf("\n\n")) !== -1) {
          const data = frameData(buffer.slice(0, sep));
          buffer = buffer.slice(sep + 2);
          if (data && data !== "[DONE]") dispatchEvent(data, handlers);
        }
      }
    } finally {
      reader.releaseLock();
    }
  },
};
