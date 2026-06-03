import { Outlet } from "react-router-dom";
import { Sidebar } from "./Sidebar";

export function AppLayout() {
  return (
    <div className="flex h-screen overflow-hidden">
      <Sidebar />
      <main className="flex flex-1 flex-col overflow-hidden">
        <Outlet />
      </main>
    </div>
  );
}
