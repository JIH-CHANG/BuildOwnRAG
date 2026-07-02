import { cn } from "@/lib/utils";
import { X } from "lucide-react";
import { forwardRef } from "react";
import type {
  ButtonHTMLAttributes,
  InputHTMLAttributes,
  SelectHTMLAttributes,
  ReactNode,
} from "react";

type ButtonVariant = "primary" | "ghost" | "danger";

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: "sm" | "md";
  loading?: boolean;
}

export function Button({
  variant = "primary",
  size = "md",
  loading,
  className,
  children,
  disabled,
  ...props
}: ButtonProps) {
  return (
    <button
      {...props}
      disabled={disabled ?? loading}
      className={cn(
        "inline-flex items-center justify-center gap-2 rounded-md font-medium transition-colors",
        "focus:outline-none focus:ring-2 focus:ring-accent/50",
        "disabled:opacity-50 disabled:cursor-not-allowed",
        size === "sm" ? "px-3 py-1.5 text-sm" : "px-4 py-2 text-sm",
        variant === "primary" && "bg-accent text-white hover:bg-accent-hover",
        variant === "ghost" &&
          "bg-transparent text-slate-300 hover:bg-white/5 border border-surface-border",
        variant === "danger" &&
          "bg-red-600/20 text-red-400 border border-red-600/30 hover:bg-red-600/30",
        className
      )}
    >
      {loading && <Spinner size={14} />}
      {children}
    </button>
  );
}

interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: string;
  error?: string;
}

export const Input = forwardRef<HTMLInputElement, InputProps>(
  ({ label, error, className, id, ...props }, ref) => (
    <div className="flex flex-col gap-1">
      {label && (
        <label htmlFor={id} className="text-sm text-slate-400">
          {label}
        </label>
      )}
      <input
        id={id}
        ref={ref}
        {...props}
        className={cn(
          "w-full rounded-md border border-surface-border bg-surface-muted",
          "px-3 py-2 text-sm text-slate-200 placeholder:text-slate-500",
          "focus:outline-none focus:ring-2 focus:ring-accent/50",
          error && "border-red-500",
          className
        )}
      />
      {error && <p className="text-xs text-red-400">{error}</p>}
    </div>
  )
);
Input.displayName = "Input";

interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label?: string;
}

export function Select({ label, className, children, id, ...props }: SelectProps) {
  return (
    <div className="flex flex-col gap-1">
      {label && (
        <label htmlFor={id} className="text-sm text-slate-400">
          {label}
        </label>
      )}
      <select
        id={id}
        {...props}
        className={cn(
          "w-full rounded-md border border-surface-border bg-surface-muted",
          "px-3 py-2 text-sm text-slate-200",
          "focus:outline-none focus:ring-2 focus:ring-accent/50",
          className
        )}
      >
        {children}
      </select>
    </div>
  );
}

type BadgeVariant = "success" | "error" | "warning" | "info" | "muted";

export function Badge({
  variant = "muted",
  children,
}: {
  variant?: BadgeVariant;
  children: ReactNode;
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium",
        variant === "success" && "bg-emerald-500/15 text-emerald-400",
        variant === "error" && "bg-red-500/15 text-red-400",
        variant === "warning" && "bg-amber-500/15 text-amber-400",
        variant === "info" && "bg-blue-500/15 text-blue-400",
        variant === "muted" && "bg-slate-500/15 text-slate-400"
      )}
    >
      {children}
    </span>
  );
}

export function Skeleton({ className }: { className?: string }) {
  return (
    <div className={cn("animate-pulse rounded bg-surface-border", className)} />
  );
}

export function Spinner({ size = 16 }: { size?: number }) {
  return (
    <svg
      className="animate-spin text-current"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      aria-hidden="true"
    >
      <circle
        className="opacity-25"
        cx="12"
        cy="12"
        r="10"
        stroke="currentColor"
        strokeWidth="4"
      />
      <path
        className="opacity-75"
        fill="currentColor"
        d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
      />
    </svg>
  );
}

interface ModalProps {
  open: boolean;
  onClose: () => void;
  title: string;
  children: ReactNode;
}

export function Modal({ open, onClose, title, children }: ModalProps) {
  if (!open) return null;
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
      onClick={onClose}
    >
      <div
        className="flex max-h-full w-full max-w-md flex-col rounded-xl border border-surface-border bg-surface-muted p-6 shadow-2xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-4 flex shrink-0 items-center justify-between">
          <h2 className="text-lg font-semibold text-slate-100">{title}</h2>
          <button
            onClick={onClose}
            className="text-slate-400 hover:text-slate-200"
            aria-label="Close"
          >
            <X size={18} />
          </button>
        </div>
        <div className="-mr-2 min-h-0 overflow-y-auto pr-2">{children}</div>
      </div>
    </div>
  );
}

interface ToastItem {
  id: string;
  type: "success" | "error" | "info";
  message: string;
}

export function ToastContainer({
  toasts,
  onDismiss,
}: {
  toasts: ToastItem[];
  onDismiss: (id: string) => void;
}) {
  if (toasts.length === 0) return null;
  return (
    <div className="fixed bottom-4 right-4 z-50 flex flex-col gap-2">
      {toasts.map((t) => (
        <div
          key={t.id}
          className={cn(
            "flex items-center gap-3 rounded-lg border px-4 py-3 shadow-lg text-sm",
            t.type === "success" &&
              "border-emerald-600/40 bg-emerald-900/60 text-emerald-200",
            t.type === "error" &&
              "border-red-600/40 bg-red-900/60 text-red-200",
            t.type === "info" &&
              "border-blue-600/40 bg-blue-900/60 text-blue-200"
          )}
        >
          <span className="flex-1">{t.message}</span>
          <button
            onClick={() => onDismiss(t.id)}
            className="opacity-60 hover:opacity-100"
            aria-label="Dismiss"
          >
            <X size={14} />
          </button>
        </div>
      ))}
    </div>
  );
}
