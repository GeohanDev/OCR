import axios from 'axios';

// Empty default → requests go to /api/* → Vite proxy forwards to localhost:5000 in dev.
// Set VITE_API_URL to the backend origin in production (e.g. https://api.example.com).
const API_BASE = import.meta.env.VITE_API_URL ?? '';

export const apiClient = axios.create({
  baseURL: `${API_BASE}/api`,
  withCredentials: true,
  headers: { 'Content-Type': 'application/json' },
});

// Attach JWT from sessionStorage
apiClient.interceptors.request.use((config) => {
  const token = sessionStorage.getItem('access_token');
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

apiClient.interceptors.response.use(
  (r) => r,
  (error) => Promise.reject(error)
);

// ── Documents ──────────────────────────────────────────────────────────
export const documentApi = {
  list: (params?: Record<string, unknown>) =>
    apiClient.get('/documents', { params }),
  getById: (id: string) => apiClient.get(`/documents/${id}`),
  getSignedUrl: (id: string) => apiClient.get(`/documents/${id}/file`),
  upload: (formData: FormData) =>
    apiClient.post('/documents/upload', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    }),
  updateStatus: (id: string, status: string, notes?: string) =>
    apiClient.patch(`/documents/${id}/status`, { status, notes }),
  delete: (id: string) => apiClient.delete(`/documents/${id}`),
  addVersion: (id: string, formData: FormData) =>
    apiClient.post(`/documents/${id}/versions`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    }),
};

// ── OCR ───────────────────────────────────────────────────────────────
export const ocrApi = {
  process: (documentId: string) =>
    apiClient.post(`/ocr/${documentId}/process`),
  getResult: (documentId: string) =>
    apiClient.get(`/ocr/${documentId}/result`),
  getRawText: (documentId: string) =>
    apiClient.get(`/ocr/${documentId}/raw-text`),
  correctField: (documentId: string, fieldId: string, correctedValue: string) =>
    apiClient.patch(`/ocr/${documentId}/fields/${fieldId}`, { correctedValue }),
};

// ── Validation ─────────────────────────────────────────────────────────
export const validationApi = {
  run: (documentId: string) =>
    apiClient.post(`/validation/${documentId}/run`),
  getResults: (documentId: string) =>
    apiClient.get(`/validation/${documentId}/results`),
  approve: (documentId: string, notes?: string) =>
    apiClient.post(`/validation/${documentId}/approve`, { notes }),
  reject: (documentId: string, notes?: string) =>
    apiClient.post(`/validation/${documentId}/reject`, { notes }),
};

// ── ERP ───────────────────────────────────────────────────────────────
export const erpApi = {
  push: (documentId: string) =>
    apiClient.post(`/erp/${documentId}/push`),
  lookupVendor: (vendorId: string) =>
    apiClient.get('/erp/lookup/vendors', { params: { vendorId } }),
  lookupCurrency: (currencyCode: string) =>
    apiClient.get('/erp/lookup/currencies', { params: { currencyCode } }),
  lookupBranch: (branchCode: string) =>
    apiClient.get('/erp/lookup/branches', { params: { branchCode } }),
};

// ── Config ────────────────────────────────────────────────────────────
export const configApi = {
  getDocumentTypes: () => apiClient.get('/config/document-types'),
  registerDocumentType: (data: unknown) =>
    apiClient.post('/config/document-types', data),
  getFieldMappings: (typeId: string) =>
    apiClient.get(`/config/document-types/${typeId}/fields`),
  createFieldMapping: (typeId: string, data: unknown) =>
    apiClient.post(`/config/document-types/${typeId}/fields`, data),
  updateFieldMapping: (typeId: string, fieldId: string, data: unknown) =>
    apiClient.put(`/config/document-types/${typeId}/fields/${fieldId}`, data),
  deleteFieldMapping: (typeId: string, fieldId: string) =>
    apiClient.delete(`/config/document-types/${typeId}/fields/${fieldId}`),
  reorderFieldMappings: (typeId: string, orderedIds: string[]) =>
    apiClient.post(`/config/document-types/${typeId}/fields/reorder`, orderedIds),
};

// ── Dashboard & Audit ─────────────────────────────────────────────────
export const dashboardApi = {
  getKpis: () => apiClient.get('/dashboard/kpis'),
  getAuditLogs: (params?: Record<string, unknown>) =>
    apiClient.get('/audit/logs', { params }),
};

// ── Users ─────────────────────────────────────────────────────────────
export const usersApi = {
  list: (params?: Record<string, unknown>) =>
    apiClient.get('/users', { params }),
  sync: () => apiClient.post('/users/sync'),
  updateRole: (id: string, role: string) =>
    apiClient.patch(`/users/${id}/role`, { role }),
  updateActive: (id: string, isActive: boolean) =>
    apiClient.patch(`/users/${id}/active`, { isActive }),
  setActive: (id: string, isActive: boolean) =>
    apiClient.patch(`/users/${id}/active`, { isActive }),
  getAuditLogs: (params?: Record<string, unknown>) =>
    apiClient.get('/audit/logs', { params }),
};
