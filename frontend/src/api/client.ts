import axios from 'axios';
import type { TrashedDocument, TrashedFieldConfig, TrashedDocType } from '../types';

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

  // Forward the user's Acumatica token on ERP and validation requests.
  // Validation runs call ERP validators internally, so they need the token too.
  const acumaticaToken = sessionStorage.getItem('acumatica_token');
  if (acumaticaToken && (config.url?.includes('/erp/') || config.url?.includes('/validation/') || config.url?.includes('/vendors/'))) {
    config.headers['X-Acumatica-Token'] = acumaticaToken;
  }

  return config;
});

// On 401, clear the stale token and redirect to login so auto-login retries.
// Skip /auth/ routes — they handle their own errors.
apiClient.interceptors.response.use(
  (r) => r,
  (error) => {
    const url: string = error.config?.url ?? '';
    if (error.response?.status === 401 && !url.includes('/auth/')) {
      sessionStorage.removeItem('access_token');
      window.location.href = '/login';
    }
    return Promise.reject(error);
  }
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
  assignDocumentType: (id: string, documentTypeId: string | null) =>
    apiClient.patch(`/documents/${id}/type`, { documentTypeId }),
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
  deleteField: (documentId: string, fieldId: string) =>
    apiClient.delete(`/ocr/${documentId}/fields/${fieldId}`),
};

// ── Validation ─────────────────────────────────────────────────────────
export const validationApi = {
  run: (documentId: string) =>
    apiClient.post(`/validation/${documentId}/run`),
  validateField: (documentId: string, fieldId: string) =>
    apiClient.post(`/validation/${documentId}/field/${fieldId}`),
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
  getVendors: (top?: number) =>
    apiClient.get('/erp/lookup/vendors/list', top ? { params: { top } } : undefined),
  lookupVendor: (vendorId: string) =>
    apiClient.get('/erp/lookup/vendors', { params: { vendorId } }),
  lookupVendorByName: (vendorName: string) =>
    apiClient.get('/erp/lookup/vendors', { params: { vendorName } }),
  lookupCurrency: (currencyCode: string) =>
    apiClient.get('/erp/lookup/currencies', { params: { currencyCode } }),
  lookupBranch: (branchCode: string) =>
    apiClient.get('/erp/lookup/branches', { params: { branchCode } }),
  lookupApInvoice: (invoiceNbr: string) =>
    apiClient.get('/erp/lookup/ap-invoices', { params: { invoiceNbr } }),
  getErpEntities: () =>
    apiClient.get('/erp/entities'),
  getODataEntities: () =>
    apiClient.get('/erp/odata-entities'),
  getODataEntitiesRaw: () =>
    apiClient.get('/erp/odata-entities/raw', { responseType: 'text' }),
  lookupGeneric: (entity: string, field: string, value: string) =>
    apiClient.get('/erp/lookup/generic', { params: { entity, field, value } }),
  probeEntity: (entity: string) =>
    apiClient.get(`/erp/probe/${entity}`),
  lookupVendorBalance: (vendorId: string, period: string) =>
    apiClient.get('/erp/lookup/vendor-balance', { params: { vendorId, period } }),
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
  deleteDocumentType: (typeId: string) =>
    apiClient.delete(`/config/document-types/${typeId}`),
  reorderFieldMappings: (typeId: string, orderedIds: string[]) =>
    apiClient.post(`/config/document-types/${typeId}/fields/reorder`, orderedIds),
};

// ── Dashboard & Audit ─────────────────────────────────────────────────
export const dashboardApi = {
  getKpis: () => apiClient.get('/dashboard/kpis'),
  getAuditLogs: (params?: Record<string, unknown>) =>
    apiClient.get('/audit/logs', { params }),
};

// ── Vendors ───────────────────────────────────────────────────────────
export const vendorApi = {
  list: (params?: Record<string, unknown>) =>
    apiClient.get('/vendors', { params }),
  sync: () => apiClient.post('/vendors/sync'),
};

// ── Trash ─────────────────────────────────────────────────────────────
export const trashApi = {
  getTrashedDocuments: () => apiClient.get<TrashedDocument[]>('/trash/documents'),
  restoreDocument: (id: string) => apiClient.post(`/trash/documents/${id}/restore`),
  getTrashedFieldMappings: () => apiClient.get<TrashedFieldConfig[]>('/trash/field-mappings'),
  restoreFieldMapping: (id: string) => apiClient.post(`/trash/field-mappings/${id}/restore`),
  getTrashedDocTypes: () => apiClient.get<TrashedDocType[]>('/trash/document-types'),
  restoreDocType: (id: string) => apiClient.post(`/trash/document-types/${id}/restore`),
  purgeAll: () => apiClient.delete('/trash/purge'),
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
