import { Routes, Route, Navigate } from "react-router-dom";
import { AppLayout } from "@/components/layout/AppLayout";
import { ProtectedRoute } from "@/components/ProtectedRoute";
import { LoginPage } from "@/pages/LoginPage";
import { ChatPage } from "@/pages/ChatPage";
import { DocumentsPage } from "@/pages/DocumentsPage";
import { SettingsLayout } from "@/pages/settings/SettingsLayout";
import { AiModelPage } from "@/pages/settings/AiModelPage";
import { SystemPromptPage } from "@/pages/settings/SystemPromptPage";
import { ConnectorsPage } from "@/pages/settings/ConnectorsPage";
import { UsersPage } from "@/pages/settings/UsersPage";
import { ApiKeysPage } from "@/pages/settings/ApiKeysPage";

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />

      <Route
        element={
          <ProtectedRoute>
            <AppLayout />
          </ProtectedRoute>
        }
      >
        <Route index element={<Navigate to="/chat" replace />} />
        <Route path="chat" element={<ChatPage />} />
        <Route path="documents" element={<DocumentsPage />} />

        <Route
          path="settings"
          element={
            <ProtectedRoute requiredRole="TenantAdmin">
              <SettingsLayout />
            </ProtectedRoute>
          }
        >
          <Route index element={<Navigate to="ai-model" replace />} />
          <Route path="ai-model" element={<AiModelPage />} />
          <Route path="system-prompt" element={<SystemPromptPage />} />
          <Route path="connectors" element={<ConnectorsPage />} />
          <Route path="users" element={<UsersPage />} />
          <Route path="api-keys" element={<ApiKeysPage />} />
        </Route>
      </Route>

      <Route path="*" element={<Navigate to="/chat" replace />} />
    </Routes>
  );
}
