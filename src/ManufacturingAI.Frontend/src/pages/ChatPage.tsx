import { useState, useRef, useEffect } from "react";
import { Send, Square, FileText, ThumbsUp, ThumbsDown } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui";
import { chatApi } from "@/api/chat";
import { getErrorMessage } from "@/api/client";
import type { ChatMessage, QueryFeedback, QuerySource } from "@/types";

let msgId = 0;
const nextId = () => String(++msgId);

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";
// Feedback targets a logged query by id; both Hybrid and Lite modes persist logs.
const canGiveFeedback = (queryId?: string): queryId is string =>
  !!queryId && queryId !== EMPTY_GUID;

export function ChatPage() {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState("");
  const [isStreaming, setIsStreaming] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  // Cancel any in-flight stream if the user leaves the page.
  useEffect(() => () => abortRef.current?.abort(), []);

  const handleSend = async () => {
    const q = input.trim();
    if (!q || isStreaming) return;

    // Push the user message plus an empty assistant bubble we'll stream into.
    const userId = nextId();
    const assistantId = nextId();
    setMessages((prev) => [
      ...prev,
      { id: userId, role: "user", content: q, createdAt: new Date().toISOString() },
      { id: assistantId, role: "assistant", content: "", createdAt: new Date().toISOString() },
    ]);
    setInput("");
    setIsStreaming(true);

    const controller = new AbortController();
    abortRef.current = controller;

    const patchAssistant = (patch: (m: ChatMessage) => ChatMessage) =>
      setMessages((prev) => prev.map((m) => (m.id === assistantId ? patch(m) : m)));

    try {
      await chatApi.streamQuery(
        q,
        {
          onToken: (token) =>
            patchAssistant((m) => ({ ...m, content: m.content + token })),
          onComplete: ({ sources, queryId }) =>
            patchAssistant((m) => ({ ...m, sources, queryId })),
        },
        controller.signal,
      );
    } catch (err) {
      if (controller.signal.aborted) {
        patchAssistant((m) => ({
          ...m,
          content: m.content ? m.content + "\n\n*[Response stopped]*" : "*[Response stopped]*",
        }));
        return;
      }
      const message = `Error: ${getErrorMessage(err)}`;
      patchAssistant((m) => ({
        ...m,
        content: m.content ? `${m.content}\n\n${message}` : message,
      }));
    } finally {
      if (abortRef.current === controller) abortRef.current = null;
      setIsStreaming(false);
    }
  };

  const handleStop = () => abortRef.current?.abort();

  const handleKey = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const handleFeedback = async (message: ChatMessage, feedback: QueryFeedback) => {
    if (!canGiveFeedback(message.queryId)) return;
    const next = message.feedback === feedback ? undefined : feedback;
    const setFeedback = (value: QueryFeedback | undefined) =>
      setMessages((prev) =>
        prev.map((m) => (m.id === message.id ? { ...m, feedback: value } : m)),
      );

    setFeedback(next); // optimistic
    if (!next) return; // un-toggling: nothing to send
    try {
      await chatApi.feedback(message.queryId, next);
    } catch {
      setFeedback(message.feedback); // revert on failure
    }
  };

  // The bubble currently being streamed into is always the last message.
  const lastMsg = messages[messages.length - 1];
  const streamingId = isStreaming && lastMsg?.role === "assistant" ? lastMsg.id : null;

  return (
    <div className="flex h-full flex-col">
      <header className="flex h-14 items-center border-b border-surface-border px-6">
        <h1 className="text-sm font-semibold text-slate-200">Chat</h1>
      </header>

      <div className="flex flex-1 overflow-hidden">
        <div className="flex flex-1 flex-col overflow-y-auto px-4 py-6">
          {messages.length === 0 ? (
            <div className="flex flex-1 flex-col items-center justify-center text-center">
              <p className="text-2xl font-semibold text-slate-300">
                Ask anything about your documents
              </p>
              <p className="mt-2 text-sm text-slate-500">
                SOP, BOM, quality standards — ask in natural language
              </p>
            </div>
          ) : (
            <div className="mx-auto flex w-full max-w-3xl flex-col gap-6">
              {messages.map((msg) => (
                <MessageBubble
                  key={msg.id}
                  message={msg}
                  streaming={msg.id === streamingId}
                  onFeedback={handleFeedback}
                />
              ))}
            </div>
          )}
          <div ref={bottomRef} />
        </div>
      </div>

      <div className="border-t border-surface-border p-4">
        <div className="mx-auto flex w-full max-w-3xl gap-3">
          <textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={handleKey}
            placeholder="Ask about SOPs, BOM, quality docs…"
            rows={1}
            className={cn(
              "flex-1 resize-none rounded-lg border border-surface-border",
              "bg-surface-muted px-4 py-3 text-sm text-slate-200",
              "placeholder:text-slate-500 focus:outline-none focus:ring-2 focus:ring-accent/50",
              "max-h-40 overflow-y-auto"
            )}
          />
          {isStreaming ? (
            <Button
              onClick={handleStop}
              className="h-11 w-11 shrink-0 !p-0 bg-rose-600 hover:bg-rose-500"
              aria-label="Stop"
            >
              <Square size={14} fill="currentColor" />
            </Button>
          ) : (
            <Button
              onClick={handleSend}
              disabled={!input.trim()}
              className="h-11 w-11 shrink-0 !p-0"
              aria-label="Send"
            >
              <Send size={16} />
            </Button>
          )}
        </div>
      </div>
    </div>
  );
}

function MessageBubble({
  message,
  streaming = false,
  onFeedback,
}: {
  message: ChatMessage;
  streaming?: boolean;
  onFeedback: (message: ChatMessage, feedback: QueryFeedback) => void;
}) {
  const isUser = message.role === "user";
  const showFeedback =
    !isUser && !streaming && canGiveFeedback(message.queryId);
  return (
    <div className={cn("flex gap-3", isUser && "flex-row-reverse")}>
      <div
        className={cn(
          "h-8 w-8 shrink-0 rounded-full flex items-center justify-center text-xs font-semibold",
          isUser ? "bg-accent text-white" : "bg-surface-border text-slate-400"
        )}
      >
        {isUser ? "U" : "AI"}
      </div>

      <div className="flex min-w-0 max-w-[75%] flex-col gap-2">
        <div
          className={cn(
            "rounded-2xl px-4 py-3 text-sm leading-relaxed",
            // Preserve newlines and force long unbroken tokens (e.g. hex keys) to wrap
            // instead of overflowing the bubble horizontally.
            "whitespace-pre-wrap break-words [overflow-wrap:anywhere]",
            isUser
              ? "rounded-tr-sm bg-accent text-white"
              : "rounded-tl-sm bg-surface-border text-slate-200"
          )}
        >
          {message.content}
          {streaming && (
            <span className="ml-0.5 inline-block animate-pulse text-accent">▍</span>
          )}
        </div>

        {message.sources && message.sources.length > 0 && (
          <SourceList sources={message.sources} />
        )}

        {showFeedback && (
          <div className="flex items-center gap-1 pl-1">
            <FeedbackButton
              label="Helpful"
              active={message.feedback === "Positive"}
              activeClass="text-emerald-400"
              onClick={() => onFeedback(message, "Positive")}
            >
              <ThumbsUp size={14} />
            </FeedbackButton>
            <FeedbackButton
              label="Not helpful"
              active={message.feedback === "Negative"}
              activeClass="text-rose-400"
              onClick={() => onFeedback(message, "Negative")}
            >
              <ThumbsDown size={14} />
            </FeedbackButton>
          </div>
        )}
      </div>
    </div>
  );
}

function FeedbackButton({
  label,
  active,
  activeClass,
  onClick,
  children,
}: {
  label: string;
  active: boolean;
  activeClass: string;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      aria-pressed={active}
      title={label}
      className={cn(
        "rounded-md p-1.5 text-slate-500 transition-colors hover:bg-surface-muted hover:text-slate-300",
        active && activeClass,
      )}
    >
      {children}
    </button>
  );
}

function SourceList({ sources }: { sources: QuerySource[] }) {
  return (
    <div className="flex flex-col gap-1">
      <p className="text-xs text-slate-500">Sources</p>
      {sources.map((s) => (
        <div
          key={s.documentId}
          className="flex items-center gap-2 rounded-md bg-surface-muted px-3 py-1.5 text-xs text-slate-400"
        >
          <FileText size={12} className="shrink-0 text-accent" />
          <span className="truncate">{s.title}</span>
          {s.pageNumber && (
            <span className="ml-auto shrink-0 text-slate-500">
              p. {s.pageNumber}
            </span>
          )}
        </div>
      ))}
    </div>
  );
}
