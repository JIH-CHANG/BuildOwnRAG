import { NavLink } from "react-router-dom";
import {
  MessageSquare,
  FileText,
  BarChart2,
  Settings,
  LogOut,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { useAuthStore } from "@/stores/authStore";

interface NavItem {
  to: string;
  icon: React.ReactNode;
  label: string;
}

export function Sidebar() {
  const { user, logout } = useAuthStore();
  const isAdmin = user?.role === "TenantAdmin";
  const isPro = user?.plan === "Pro";

  const navItems: NavItem[] = [
    { to: "/chat", icon: <MessageSquare size={18} />, label: "Chat" },
    { to: "/documents", icon: <FileText size={18} />, label: "Documents" },
    ...(isAdmin && isPro
      ? [{ to: "/analytics", icon: <BarChart2 size={18} />, label: "Analytics" }]
      : []),
    ...(isAdmin
      ? [{ to: "/settings", icon: <Settings size={18} />, label: "Settings" }]
      : []),
  ];

  return (
    <aside className="flex h-screen w-56 flex-col border-r border-surface-border bg-surface-muted">
      <div className="flex h-14 items-center gap-2 border-b border-surface-border px-4">
        <span className="text-lg font-bold tracking-tight text-slate-100">
          BuildOwn<span className="text-accent">RAG</span>
        </span>
      </div>

      <nav className="flex flex-1 flex-col gap-1 p-3">
        {navItems.map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            className={({ isActive }) =>
              cn(
                "flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors",
                isActive
                  ? "bg-accent/10 text-accent"
                  : "text-slate-400 hover:bg-white/5 hover:text-slate-200"
              )
            }
          >
            {item.icon}
            {item.label}
          </NavLink>
        ))}
      </nav>

      <div className="border-t border-surface-border p-3">
        <div className="mb-2 px-3 py-1">
          <p className="truncate text-xs font-medium text-slate-300">
            {user?.email}
          </p>
          <p className="text-xs text-slate-500">{user?.role}</p>
        </div>
        <button
          onClick={logout}
          className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm text-slate-400 transition-colors hover:bg-white/5 hover:text-slate-200"
        >
          <LogOut size={18} />
          Sign out
        </button>
      </div>
    </aside>
  );
}
