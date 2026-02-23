import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from './contexts/AuthContext';
import ProtectedRoute from './components/ProtectedRoute';
import AppShell from './components/layout/AppShell';

import LoginPage from './pages/LoginPage';
import AuthCallbackPage from './pages/AuthCallbackPage';
import DashboardPage from './pages/DashboardPage';
import DocumentListPage from './pages/DocumentListPage';
import DocumentDetailPage from './pages/DocumentDetailPage';
import UploadPage from './pages/UploadPage';
import VerificationPage from './pages/VerificationPage';
import UserManagementPage from './pages/admin/UserManagementPage';
import FieldMappingConfigPage from './pages/admin/FieldMappingConfigPage';
import AuditLogPage from './pages/admin/AuditLogPage';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 1,
    },
  },
});

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <BrowserRouter>
          <Routes>
            {/* Public */}
            <Route path="/login" element={<LoginPage />} />
            <Route path="/auth/callback" element={<AuthCallbackPage />} />

            {/* Protected — any authenticated user */}
            <Route element={<ProtectedRoute />}>
              <Route element={<AppShell />}>
                <Route path="/" element={<Navigate to="/dashboard" replace />} />
                <Route path="/dashboard" element={<DashboardPage />} />
                <Route path="/documents" element={<DocumentListPage />} />
                <Route path="/documents/upload" element={<UploadPage />} />
                <Route path="/documents/:id" element={<DocumentDetailPage />} />
                <Route path="/documents/:id/verify" element={<VerificationPage />} />

                {/* Admin / Manager only */}
                <Route element={<ProtectedRoute requireManager />}>
                  <Route path="/admin/users" element={<UserManagementPage />} />
                  <Route path="/admin/audit" element={<AuditLogPage />} />
                </Route>
                <Route element={<ProtectedRoute requireAdmin />}>
                  <Route path="/admin/config" element={<FieldMappingConfigPage />} />
                </Route>
              </Route>
            </Route>

            {/* Fallback */}
            <Route path="*" element={<Navigate to="/dashboard" replace />} />
          </Routes>
        </BrowserRouter>
      </AuthProvider>
    </QueryClientProvider>
  );
}
