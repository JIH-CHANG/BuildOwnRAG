import { NavLink, Outlet } from "react-router-dom";
import { cn } from "@/lib/utils";

const tabs = [
  { to: "/settings/ai-model", label: "AI Model" },
  { to: "/settings/system-prompt", label: "System Prompt" },
  { to: "/settings/connectors", label: "Connectors" },
  { to: "/settings/users", label: "Users" },
  { to: "/settings/api-keys", label: "API Keys" },
];

export function SettingsLayout() {
  return (
    <div className="flex h-full flex-col overflow-hidden">
      <header className="flex h-14 shrink-0 items-center border-b border-surface-border px-6">
        <h1 className="text-sm font-semibold text-slate-200">Settings</h1>
      </header>

      <div className="flex shrink-0 gap-1 border-b border-surface-border px-6">
        {tabs.map((tab) => (
          <NavLink
            key={tab.to}
            to={tab.to}
            className={({ isActive }) =>
              cn(
                "px-3 py-2.5 text-sm font-medium transition-colors border-b-2 -mb-px",
                isActive
                  ? "border-accent text-accent"
                  : "border-transparent text-slate-400 hover:text-slate-200"
              )
            }
          >
            {tab.label}
          </NavLink>
        ))}
      </div>

      <div className="flex-1 overflow-y-auto p-6">
        <Outlet />
      </div>
    </div>
  );
}
