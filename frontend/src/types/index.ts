export interface User {
  id: string;
  acumaticaUserId: string;
  username: string;
  displayName: string;
  email?: string;
  role: 'Normal' | 'Manager' | 'Admin';
  branchId?: string;
  branchName?: string;
  isActive: boolean;
  lastSyncedAt?: string;
  createdAt: string;
}

export interface Vendor {
  id: string;
  acumaticaVendorId: string;
  vendorName: string;
  addressLine1?: string;
  addressLine2?: string;
  city?: string;
  state?: string;
  postalCode?: string;
  country?: string;
  paymentTerms?: string;
  isActive: boolean;
  lastSyncedAt: string;
}

export interface Document {
  id: string;
  documentTypeId?: string;
  documentTypeName?: string;
  originalFilename: string;
  mimeType?: string;
  fileSizeBytes?: number;
  status: DocumentStatus;
  uploadedBy: string;
  uploadedByUsername: string;
  branchId?: string;
  branchName?: string;
  uploadedAt: string;
  processedAt?: string;
  reviewedAt?: string;
  reviewedByUsername?: string;
  approvedAt?: string;
  approvedByUsername?: string;
  pushedAt?: string;
  notes?: string;
  currentVersion: number;
  versions?: DocumentVersion[];
  vendorId?: string;
  vendorName?: string;
}

export interface DocumentVersion {
  id: string;
  versionNumber: number;
  storagePath: string;
  fileHash: string;
  uploadedByUsername: string;
  uploadedAt: string;
}

export type DocumentStatus =
  | 'Uploaded'
  | 'Processing'
  | 'PendingReview'
  | 'ReviewInProgress'
  | 'Approved'
  | 'Rejected'
  | 'Pushed'
  | 'Checked';

export interface OcrResult {
  id: string;
  documentId: string;
  versionNumber: number;
  rawText?: string;
  engineVersion?: string;
  processingMs?: number;
  overallConfidence?: number;
  pageCount?: number;
  fields: ExtractedField[];
  createdAt: string;
}

export interface ExtractedField {
  id: string;
  fieldName: string;
  rawValue?: string;
  normalizedValue?: string;
  confidence: number;
  boundingBox?: BoundingBox;
  isManuallyCorreected: boolean;
  correctedValue?: string;
}

export interface BoundingBox {
  page: number;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface ValidationResult {
  id: string;
  documentId: string;
  extractedFieldId?: string;
  fieldName: string;
  validationType: string;
  status: 'Passed' | 'Failed' | 'Warning' | 'Skipped';
  message?: string;
  erpReference?: unknown;
  erpResponseField?: string;
  validatedAt: string;
}

export interface ValidationSummary {
  documentId: string;
  totalFields: number;
  passedCount: number;
  failedCount: number;
  warningCount: number;
  canApprove: boolean;
  results: ValidationResult[];
}

export type DocumentCategory = 'General' | 'VendorStatement';
export const DOCUMENT_CATEGORIES: { value: DocumentCategory; label: string }[] = [
  { value: 'General',         label: 'General' },
  { value: 'VendorStatement', label: 'Vendor Statement' },
];

export interface DocumentType {
  id: string;
  typeKey: string;
  displayName: string;
  pluginClass: string;
  category: DocumentCategory;
  isActive: boolean;
  createdAt: string;
}

export interface FieldMappingConfig {
  id: string;
  documentTypeId: string;
  fieldName: string;
  displayLabel?: string;
  regexPattern?: string;
  keywordAnchor?: string;
  positionRule?: unknown;
  isRequired: boolean;
  allowMultiple: boolean;
  erpMappingKey?: string;
  erpResponseField?: string;
  dependentFieldKey?: string;
  isManualEntry?: boolean;
  isCheckbox?: boolean;
  confidenceThreshold: number;
  displayOrder: number;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface ErpEntity {
  entityName: string;
  displayName: string;
  fields: string[];
}

export interface DashboardKpis {
  totalDocuments: number;
  pendingReview: number;
  failed: number;
  checked: number;
  recentDocuments: RecentDocument[];
}

export interface RecentDocument {
  id: string;
  originalFilename: string;
  status: string;
  uploadedAt: string;
  uploadedByUsername: string;
}

export interface AuditLog {
  id: number;
  eventType: string;
  actorUserId?: string;
  actorUsername?: string;
  documentId?: string;
  targetEntityType?: string;
  targetEntityId?: string;
  beforeValue?: unknown;
  afterValue?: unknown;
  ipAddress?: string;
  occurredAt: string;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}

export interface TrashedDocument {
  id: string;
  originalFilename: string;
  documentTypeName?: string;
  status: string;
  uploadedByUsername: string;
  uploadedAt: string;
  deletedAt: string;
}

export interface TrashedFieldConfig {
  id: string;
  documentTypeId: string;
  documentTypeName: string;
  fieldName: string;
  displayLabel?: string;
  deletedAt: string;
}

export interface TrashedDocType {
  id: string;
  typeKey: string;
  displayName: string;
  deletedAt: string;
  fieldCount: number;
}
