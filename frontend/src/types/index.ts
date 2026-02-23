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
  | 'Pushed';

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

export interface DocumentType {
  id: string;
  typeKey: string;
  displayName: string;
  pluginClass: string;
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
  erpMappingKey?: string;
  confidenceThreshold: number;
  displayOrder: number;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface DashboardKpis {
  totalDocuments: number;
  pendingReview: number;
  approved: number;
  failed: number;
  pushedToErp: number;
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
