import { useState, useCallback } from "react";

interface Toast {
  id: string;
  type: "success" | "error" | "info";
  message: string;
}

let toastId = 0;

export function useToast() {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const addToast = useCallback(
    (type: Toast["type"], message: string, durationMs = 4000) => {
      const id = String(++toastId);
      setToasts((prev) => [...prev, { id, type, message }]);
      setTimeout(() => {
        setToasts((prev) => prev.filter((t) => t.id !== id));
      }, durationMs);
    },
    []
  );

  const dismiss = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  return {
    toasts,
    toast: {
      success: (msg: string) => addToast("success", msg),
      error: (msg: string) => addToast("error", msg),
      info: (msg: string) => addToast("info", msg),
    },
    dismiss,
  };
}
