import { useState, useRef, useEffect } from "react";
import { useMutation } from "@tanstack/react-query";
import { Send, FileText } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button, Spinner } from "@/components/ui";
import { chatApi } from "@/api/chat";
import { getErrorMessage } from "@/api/client";
import type { ChatMessage, QuerySource } from "@/types";

let msgId = 0;
const nextId = () => String(++msgId);

export function ChatPage() {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState("");
  const bottomRef = useRef<HTMLDivElement>(null);

  const mutation = useMutation({
    mutationFn: chatApi.query,
    onSuccess: (result) => {
      setMessages((prev) => [
        ...prev,
        {
          id: nextId(),
          role: "assistant",
          content: result.answer,
          sources: result.sources,
          createdAt: new Date().toISOString(),
        },
      ]);
    },
    onError: (err) => {
      setMessages((prev) => [
        ...prev,
        {
          id: nextId(),
          role: "assistant",
          content: `Error: ${getErrorMessage(err)}`,
          createdAt: new Date().toISOString(),
        },
      ]);
    },
  });

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const handleSend = () => {
    const q = input.trim();
    if (!q || mutation.isPending) return;
    setMessages((prev) => [
      ...prev,
      {
        id: nextId(),
        role: "user",
        content: q,
        createdAt: new Date().toISOString(),
      },
    ]);
    setInput("");
    mutation.mutate(q);
  };

  const handleKey = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

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
                <MessageBubble key={msg.id} message={msg} />
              ))}
              {mutation.isPending && (
                <div className="flex items-center gap-2 text-sm text-slate-400">
                  <Spinner size={14} />
                  Thinking…
                </div>
              )}
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
          <Button
            onClick={handleSend}
            disabled={!input.trim() || mutation.isPending}
            className="h-11 w-11 shrink-0 !p-0"
            aria-label="Send"
          >
            <Send size={16} />
          </Button>
        </div>
      </div>
    </div>
  );
}

function MessageBubble({ message }: { message: ChatMessage }) {
  const isUser = message.role === "user";
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

      <div className="flex max-w-[75%] flex-col gap-2">
        <div
          className={cn(
            "rounded-2xl px-4 py-3 text-sm leading-relaxed",
            isUser
              ? "rounded-tr-sm bg-accent text-white"
              : "rounded-tl-sm bg-surface-border text-slate-200"
          )}
        >
          {message.content}
        </div>

        {message.sources && message.sources.length > 0 && (
          <SourceList sources={message.sources} />
        )}
      </div>
    </div>
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
