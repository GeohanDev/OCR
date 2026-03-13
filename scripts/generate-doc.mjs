import {
  Document, Packer, Paragraph, TextRun, HeadingLevel, AlignmentType,
  Table, TableRow, TableCell, WidthType, ShadingType, BorderStyle,
  convertInchesToTwip, PageBreak, LevelFormat, UnderlineType,
} from 'docx';
import { writeFileSync } from 'fs';

// ── Page & column constants ───────────────────────────────────────────────────
// Letter paper (12240 dxa) minus 0.6" × 2 margins (1728 dxa) = 10512 dxa available
const AVAIL = 10512;
const pct = (p) => Math.round(AVAIL * p / 100);

// ── Paragraph helpers ─────────────────────────────────────────────────────────
const h1 = (text) => new Paragraph({ text, heading: HeadingLevel.HEADING_1, spacing: { before: 400, after: 120 } });
const h2 = (text) => new Paragraph({ text, heading: HeadingLevel.HEADING_2, spacing: { before: 280, after: 80 } });
const h3 = (text) => new Paragraph({ text, heading: HeadingLevel.HEADING_3, spacing: { before: 200, after: 60 } });
const p  = (text, extra = {}) => new Paragraph({ children: [new TextRun({ text, size: 22, ...extra })], spacing: { after: 80 } });
const bullet = (text) => new Paragraph({ children: [new TextRun({ text, size: 22 })], bullet: { level: 0 }, spacing: { after: 60 } });
const gap = () => new Paragraph({ children: [new TextRun({ text: '' })], spacing: { after: 80 } });

// ── Image placeholder ─────────────────────────────────────────────────────────
// Renders a labelled, bordered box where a diagram/screenshot can be inserted.
function imgPlaceholder(label) {
  const border = { style: BorderStyle.SINGLE, size: 6, color: 'AAAAAA' };
  return new Table({
    width: { size: AVAIL, type: WidthType.DXA },
    columnWidths: [AVAIL],
    borders: { top: border, bottom: border, left: border, right: border, insideH: border, insideV: border },
    rows: [
      new TableRow({
        children: [
          new TableCell({
            shading: { fill: 'F7F7F7', type: ShadingType.CLEAR, color: 'auto' },
            children: [
              new Paragraph({
                alignment: AlignmentType.CENTER,
                spacing: { before: 600, after: 600 },
                children: [
                  new TextRun({ text: `[ ${label} ]`, size: 22, color: '888888', italics: true }),
                ],
              }),
            ],
            margins: { top: 200, bottom: 200, left: 200, right: 200 },
          }),
        ],
      }),
    ],
  });
}

// ── Table cell helper ─────────────────────────────────────────────────────────
function tcell(text, { isBold = false, fill = undefined, textColor = undefined } = {}) {
  return new TableCell({
    shading: fill ? { fill, type: ShadingType.CLEAR, color: 'auto' } : undefined,
    children: [new Paragraph({
      children: [new TextRun({ text: text ?? '', size: 20, bold: isBold, color: textColor })],
      spacing: { after: 40 },
    })],
    margins: { top: 80, bottom: 80, left: 100, right: 100 },
  });
}

// Header row factory — dark charcoal background, white text
function thr(...labels) {
  return new TableRow({
    tableHeader: true,
    children: labels.map(l => tcell(l, { isBold: true, fill: '404040', textColor: 'FFFFFF' })),
  });
}

// Data row factory — subtle light-gray first column, plain white elsewhere
function tdr(shade, ...values) {
  return new TableRow({
    children: values.map((v, i) =>
      tcell(v, { fill: i === 0 && shade ? 'F2F2F2' : undefined, isBold: i === 0 && shade }),
    ),
  });
}

// ── Flow table (step / actor / action / result) ───────────────────────────────
const FLOW_COLS = [pct(8), pct(17), pct(45), pct(30)];

function flowStep(num, actor, action, result) {
  return new TableRow({
    children: [
      tcell(num,    { isBold: true, fill: 'F2F2F2' }),
      tcell(actor,  { fill: 'F2F2F2' }),
      tcell(action),
      tcell(result),
    ],
  });
}

function flowTable(rows) {
  return new Table({
    width: { size: AVAIL, type: WidthType.DXA },
    columnWidths: FLOW_COLS,
    rows: [
      new TableRow({
        tableHeader: true,
        children: [
          tcell('Step',                     { isBold: true, fill: '404040', textColor: 'FFFFFF' }),
          tcell('Actor',                    { isBold: true, fill: '404040', textColor: 'FFFFFF' }),
          tcell('Action / Description',     { isBold: true, fill: '404040', textColor: 'FFFFFF' }),
          tcell('System Response / Result', { isBold: true, fill: '404040', textColor: 'FFFFFF' }),
        ],
      }),
      ...rows,
    ],
  });
}

// ─────────────────────────────────────────────────────────────────────────────

const doc = new Document({
  numbering: {
    config: [{
      reference: 'steps',
      levels: [{
        level: 0,
        format: LevelFormat.DECIMAL,
        text: '%1.',
        alignment: AlignmentType.LEFT,
      }],
    }],
  },
  styles: {
    paragraphStyles: [
      {
        id: 'Heading1',
        name: 'Heading 1',
        run:  { size: 36, bold: true, color: '111111' },
        paragraph: { spacing: { before: 400, after: 120 } },
      },
      {
        id: 'Heading2',
        name: 'Heading 2',
        run:  { size: 28, bold: true, color: '222222' },
        paragraph: { spacing: { before: 280, after: 80 } },
      },
      {
        id: 'Heading3',
        name: 'Heading 3',
        run:  { size: 24, bold: true, color: '444444' },
        paragraph: { spacing: { before: 200, after: 60 } },
      },
    ],
  },
  sections: [{
    properties: {
      page: {
        margin: {
          top:    convertInchesToTwip(0.7),
          bottom: convertInchesToTwip(0.7),
          left:   convertInchesToTwip(0.6),
          right:  convertInchesToTwip(0.6),
        },
      },
    },
    children: [

      // ── Cover page ────────────────────────────────────────────────────────
      new Paragraph({
        children: [new TextRun({ text: 'OCR ERP Integration System', bold: true, size: 56 })],
        alignment: AlignmentType.CENTER,
        spacing: { before: 1440, after: 240 },
      }),
      new Paragraph({
        children: [new TextRun({ text: 'Architecture & User Guide', bold: true, size: 40 })],
        alignment: AlignmentType.CENTER,
        spacing: { after: 240 },
      }),
      new Paragraph({
        children: [new TextRun({ text: 'Version 1.0  ·  March 2026', size: 22, color: '6B7280' })],
        alignment: AlignmentType.CENTER,
        spacing: { after: 480 },
      }),
      gap(),
      imgPlaceholder('Insert System Architecture Diagram Here'),
      new Paragraph({ children: [new PageBreak()] }),

      // ══════════════════════════════════════════════════════════════════════
      // 1. System Overview
      // ══════════════════════════════════════════════════════════════════════
      h1('1. System Overview'),
      p('The OCR ERP Integration System is an on-premise web application that automates the extraction, validation, and review of financial documents (e.g., AP invoices, vendor statements) against an Acumatica ERP instance.'),
      gap(),
      p('Key capabilities:', { bold: true }),
      bullet('Intelligent OCR powered by Claude AI and Tesseract to extract structured fields from PDF and image documents.'),
      bullet('Dynamic field validation against live Acumatica ERP data (vendors, AP bills, purchase orders, inventory).'),
      bullet('Inline field correction with per-field re-validation.'),
      bullet('Role-based access control aligned with Acumatica user roles.'),
      bullet('Configurable field mapping per document type, driven by the Admin UI.'),
      bullet('Audit trail of all system events.'),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 2. Technology Stack
      // ══════════════════════════════════════════════════════════════════════
      h1('2. Technology Stack'),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(25), pct(75)],
        rows: [
          thr('Layer', 'Technology'),
          tdr(true, 'Frontend', 'React 18, TypeScript, Vite, Tailwind CSS, React Query, Axios, react-pdf'),
          tdr(true, 'Backend API', 'ASP.NET Core 8, EF Core 8, Hangfire, Serilog'),
          tdr(true, 'OCR Engine', 'Claude AI (primary), Tesseract OCR (fallback)'),
          tdr(true, 'Database', 'PostgreSQL 16 (via Npgsql + EF Core code-first migrations)'),
          tdr(true, 'ERP Integration', 'Acumatica 2025 R1 — OData REST API v25.100.001'),
          tdr(true, 'Authentication', 'Acumatica OAuth 2.0 OIDC (private_key_jwt); app-issued HS256 JWT (8 h)'),
          tdr(true, 'Deployment', 'Docker Compose (on-premise): postgres + api + nginx/frontend'),
        ],
      }),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 3. User Roles
      // ══════════════════════════════════════════════════════════════════════
      h1('3. User Roles & Permissions'),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(20), pct(80)],
        rows: [
          thr('Role', 'Permissions'),
          tdr(true, 'Normal', 'Upload documents, run OCR, review and edit extracted fields, mark as Checked.'),
          tdr(true, 'Manager', 'All Normal permissions, plus access to other users\' documents within the same branch.'),
          tdr(true, 'Admin', 'All Manager permissions, plus Admin panel: field mapping config, audit log, user management, document deletion.'),
        ],
      }),
      gap(),
      p('Note: Roles are synced from Acumatica during login. A daily background sync (02:00 AM) keeps roles and branch data current.', { italics: true, color: '6B7280' }),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 4. Application Flows
      // ══════════════════════════════════════════════════════════════════════
      h1('4. Application Flow'),

      // ── Flow 1: Authentication ─────────────────────────────────────────
      h2('Flow 1 — User Authentication & Login'),
      p('Triggered when the user first accesses the application or after signing out. Authentication is delegated to Acumatica; the app issues its own short-lived JWT after verification.'),
      gap(),
      flowTable([
        flowStep('1', 'User',      'Opens the app URL in browser.', 'Browser loads the React SPA. Unauthenticated users are redirected to the Login page.'),
        flowStep('2', 'User',      'Clicks "Sign in with Acumatica".', 'App builds an OAuth 2.0 authorize URL with prompt=login and redirects to Acumatica identity server.'),
        flowStep('3', 'Acumatica', 'Shows Acumatica login screen. prompt=login forces a fresh login every time — no SSO reuse.', 'User enters credentials and grants scope: openid, profile, email, api.'),
        flowStep('4', 'Acumatica', 'Issues authorization code; redirects browser to /auth/callback with code and state.', 'Browser lands on Auth Callback page.'),
        flowStep('5', 'API',       'Backend exchanges code for Acumatica tokens via private_key_jwt client assertion. Syncs user profile and role from Acumatica UserManagement API.', 'App JWT (HS256, 8-hour expiry) is issued and returned to the frontend.'),
        flowStep('6', 'System',    'Frontend stores JWT in localStorage; React Query cache primed.', 'User is redirected to Dashboard.'),
        flowStep('7', 'User',      'Signs out (via menu or session expiry).', 'JWT cleared from storage. Browser redirected to Login page. Next visit always shows a fresh Acumatica login screen.'),
      ]),
      gap(),
      imgPlaceholder('Insert Screenshot — Login / Auth Flow Here'),
      gap(),

      // ── Flow 2: Upload ─────────────────────────────────────────────────
      h2('Flow 2 — Document Upload'),
      p('Users upload one or more PDF or image files (PNG, TIFF). The system stores the file and creates a Document record with status "Uploaded".'),
      gap(),
      flowTable([
        flowStep('1', 'User',   'Navigates to Upload page.', 'Drag-and-drop zone and file-picker button are displayed.'),
        flowStep('2', 'User',   'Drags or selects one or more PDF / PNG / TIFF files.', 'Client-side validation: checks file type and size. Rejected files are listed with reasons.'),
        flowStep('3', 'User',   'Optionally selects a Document Type before uploading.', 'Selecting a document type ensures the correct Field Mapping Config is applied during OCR.'),
        flowStep('4', 'System', 'Uploads each file to the API. API stores the file in the document storage volume and creates a Document record.', 'Document status = "Uploaded". A warning banner appears if a document with the same filename already exists.'),
        flowStep('5', 'User',   'Views upload result list. Clicks a document name to navigate to Document Detail.', 'Redirected to Document Detail page for that document.'),
      ]),
      gap(),
      imgPlaceholder('Insert Screenshot — Upload Page Here'),
      gap(),

      // ── Flow 3: OCR ────────────────────────────────────────────────────
      h2('Flow 3 — OCR Processing'),
      p('The OCR pipeline extracts structured fields from the document. Field extraction is guided by the Field Mapping Config assigned to the document type.'),
      gap(),
      flowTable([
        flowStep('1', 'User',   'Clicks "Run OCR" (first run) or "Re-run OCR" (subsequent run) in the Action Bar on Document Detail.', 'Document status changes to "Processing". Spinner shown.'),
        flowStep('2', 'API',    'Runs the OCR pipeline: image pre-processing → Claude AI primary extraction → Tesseract fallback → confidence scoring.', 'Raw text and structured fields are extracted. Each field carries a value, confidence score (0–1), and bounding box coordinates.'),
        flowStep('3', 'System', 'Fields are matched against the Field Mapping Config (regex patterns, keyword anchors, position rules) for the assigned document type.', 'ExtractedField records stored in DB. Document status = "PendingReview".'),
        flowStep('4', 'System', 'Validation is automatically triggered immediately after OCR completes.', 'See Flow 4 — Field Validation.'),
        flowStep('5', 'User',   'Document Detail page auto-refreshes. OCR Summary card displays: confidence %, fields extracted, processing time, and validation found / not found counts.', 'User can proceed to review extracted fields.'),
      ]),
      gap(),
      imgPlaceholder('Insert Screenshot / Diagram — OCR Pipeline & Results Here'),
      gap(),

      // ── Flow 4: Validation ─────────────────────────────────────────────
      h2('Flow 4 — Field Validation Against ERP'),
      p('After OCR, each extracted field is validated against Acumatica. Validation rules are driven by the ERP Mapping Key configured in Field Mapping Config.'),
      gap(),
      flowTable([
        flowStep('1', 'System', 'Validation service loads all extracted fields and their Field Mapping Configs for the document.', 'Each field is matched to an appropriate validator based on its ERP Mapping Key.'),
        flowStep('2', 'System', 'RequiredFieldValidator runs for all fields. Field has a value → Skipped. Empty required field → Failed. Field was not extracted at all → Failed (missing from document).', 'Ensures all required fields are present before ERP checks.'),
        flowStep('3', 'System', 'DynamicErpValidator runs for fields with an "Entity:Field" ERP mapping key (e.g., "Vendor:VendorName", "Bill:VendorRef", "InventoryItem:InventoryCD").', 'Queries Acumatica OData endpoint. Returns: Found / Not Found / Warning (e.g., inactive vendor).'),
        flowStep('4', 'System', 'Validation results saved to DB. Each result includes: status, human-readable message, full ERP response data, and the ERP Response Field (key to surface in the UI on success).', 'ValidationResult records persisted.'),
        flowStep('5', 'UI',     'Validation counts (Found / Warnings / Not Found) displayed in OCR Summary card. Individual field badges and messages shown on Document Detail and Review Fields pages.', 'User sees a clear per-field pass/fail/warning status.'),
      ]),
      gap(),
      p('Validation Statuses:', { bold: true }),
      bullet('Found (✓ In ERP) — Value verified in Acumatica. Success label shows key: value (e.g., ✓ RefNbr: GESB-001).'),
      bullet('Not Found (✗) — Value could not be matched in Acumatica. Full error message displayed below badge.'),
      bullet('Warning (⚠ Review) — Value found but flagged (e.g., inactive vendor). Message displayed below badge.'),
      bullet('Skipped — Validation not applicable (no ERP key configured, or required field already has a value).'),
      gap(),
      imgPlaceholder('Insert Screenshot — Field Validation Results / Badges Here'),
      gap(),

      // ── Flow 5: Review & Correction ────────────────────────────────────
      h2('Flow 5 — Field Review & Inline Correction'),
      p('Users review extracted fields, correct errors, and trigger re-validation. Two views are available: Document Detail (summary layout) and Review Fields (full split-pane with PDF viewer).'),
      gap(),
      flowTable([
        flowStep('1', 'User',   'Opens Document Detail page or clicks "Review Fields" to open the split-pane verification view.', 'Left pane: original PDF rendered via PDF.js. Right pane: extracted fields with validation status.'),
        flowStep('2', 'User',   'Reviews Header Fields (single-value fields) — sees field name, extracted value, confidence %, and validation badge.', 'Badge: "✓ In ERP" / "⚠ Review" / "✗ Not found". Full ERP message displayed directly below badge in matching colour.'),
        flowStep('3', 'User',   'Reviews Table Data (multi-value line items) — rows in a table with a Valid. column per row.', 'Valid. column shows: "✓ RefNbr: GESB-001" for found, "✗ Not found" for failed, "⚠ Review" for warnings.'),
        flowStep('4', 'User',   'Clicks any value to edit inline. Types corrected value, presses Enter to save (or Escape to cancel).', 'Correction saved. System immediately re-validates that field (and all sibling fields if it is a table row).'),
        flowStep('5', 'User',   'Clicks "Run Validation" button (in Action Bar, right of Run OCR) to re-validate all fields simultaneously.', 'All fields validated in parallel. Per-field spinners show progress. Results update in real time.'),
        flowStep('6', 'User',   'Clicks the trash icon on a table row to delete a line item.', 'Row and its validation results removed. OCR result updated.'),
      ]),
      gap(),
      imgPlaceholder('Insert Screenshot — Split-Pane Review Fields View Here'),
      gap(),

      // ── Flow 6: Mark as Checked ────────────────────────────────────────
      h2('Flow 6 — Mark Document as Checked'),
      p('After reviewing all fields and confirming the data is accurate, a user marks the document as Checked to signal human review is complete.'),
      gap(),
      flowTable([
        flowStep('1', 'User',   'Reviews the document fields and validation results. May correct fields as needed.', 'Validation failures do not block the Checked action — human judgement takes precedence.'),
        flowStep('2', 'User',   'Clicks "Mark as Checked" in the Action Bar on Document Detail page.', 'Document status changes to "Checked".'),
        flowStep('3', 'System', 'Document status updated in DB. Audit log entry created.', 'Dashboard KPI "Checked" count increments. Document shows Checked badge in Document List.'),
      ]),
      gap(),
      imgPlaceholder('Insert Screenshot — Document Detail / Mark as Checked Here'),
      gap(),

      // ── Flow 7: Document List ──────────────────────────────────────────
      h2('Flow 7 — Document List & Management'),
      p('Users view, filter, search, and manage documents they have access to. Admins can delete documents.'),
      gap(),
      flowTable([
        flowStep('1', 'User',  'Navigates to Document List page.', 'Paginated list displays: filename, document type, status badge, uploaded by, upload date.'),
        flowStep('2', 'User',  'Filters by status (All / Pending Review / Processing / Checked / etc.) and/or types in the search box.', 'List filters in real time as criteria change.'),
        flowStep('3', 'User',  'Clicks a document row to open Document Detail.', 'Full document detail view opens.'),
        flowStep('4', 'Admin', 'Clicks "Delete" on Document Detail page, confirms in the confirmation modal.', 'Document, all OCR results, extracted fields, and validation records permanently deleted.'),
      ]),
      gap(),
      imgPlaceholder('Insert Screenshot — Document List Page Here'),
      gap(),

      // ── Flow 8: Field Mapping Config ───────────────────────────────────
      h2('Flow 8 — Admin: Field Mapping Configuration'),
      p('Admins configure what fields to extract per document type and how to validate them. This drives the entire OCR extraction and ERP validation pipeline.'),
      gap(),
      flowTable([
        flowStep('1', 'Admin', 'Navigates to Admin > Field Mapping Config.', 'List of document types is shown. Admin selects a document type (e.g., "AP Invoice").'),
        flowStep('2', 'Admin', 'Views existing field mappings for the selected type.', 'Table shows: Field Name, Display Label, Regex, Keyword Anchor, ERP Mapping Key, Success Label, Required, Active, Order.'),
        flowStep('3', 'Admin', 'Clicks "Add Field" or the edit icon on an existing mapping to open the configuration form.', 'Modal form opens.'),
        flowStep('4', 'Admin', 'Fills in the field configuration:\n• Field Name — internal key (e.g., "vendorName")\n• Display Label — shown in UI (e.g., "Vendor Name")\n• Regex Pattern — extraction hint for the OCR engine\n• ERP Mapping Key — "Entity:Field" for validation (e.g., "Vendor:VendorName")\n• Validation Success Label — ERP response field to show on pass (e.g., "vendorId", "RefNbr")\n• Required — whether a missing field is a validation failure\n• Allow Multiple — marks the field as a repeating table column\n• Display Order — controls the display order in the UI', 'Form submitted after validation.'),
        flowStep('5', 'System', 'Field mapping saved to DB. Next OCR run for this document type uses the updated configuration immediately.', 'No restart required. Config is active for all subsequent OCR and validation operations.'),
      ]),
      gap(),
      imgPlaceholder('Insert Screenshot — Field Mapping Config Admin Panel Here'),
      gap(),

      // ── Flow 9: Dashboard ──────────────────────────────────────────────
      h2('Flow 9 — Dashboard Overview'),
      p('The Dashboard provides a high-level summary of document processing status, filtered by role and branch.'),
      gap(),
      flowTable([
        flowStep('1', 'User',   'Navigates to Dashboard (default landing page after login).', 'KPI cards and recent document list load.'),
        flowStep('2', 'System', 'Queries DB for document counts, filtered by user role / branch.', 'KPI cards show: Total Documents, Pending Review, Failed (validation failures not yet resolved), Checked.'),
        flowStep('3', 'User',   'Clicks a KPI card to jump to Document List pre-filtered by that status.', 'Document List opens with the relevant status filter pre-selected.'),
        flowStep('4', 'System', 'Recent Documents list shows the 10 most recently uploaded documents.', 'Each entry shows: filename, status badge, uploader name, upload date/time.'),
      ]),
      gap(),
      imgPlaceholder('Insert Screenshot — Dashboard KPI Cards Here'),
      gap(),

      // ── Flow 10: Background Sync ───────────────────────────────────────
      h2('Flow 10 — Background Sync (Automated)'),
      p('A Hangfire background job runs daily at 02:00 AM to synchronise user data and roles from Acumatica, keeping the app in sync without manual steps.'),
      gap(),
      flowTable([
        flowStep('1', 'Scheduler', 'Hangfire triggers the AcumaticaUserSync job at 02:00 AM (configurable in system_config table).', 'Job authenticates to Acumatica using the system service account credentials.'),
        flowStep('2', 'System',    'Fetches all active users from Acumatica UserManagement API v25.100.001.', 'User list returned with roles, branch, email, and display name.'),
        flowStep('3', 'System',    'Compares against local DB: creates new user records for new Acumatica users; updates role, branch, display name, and email for existing users.', 'All local user records kept in sync with Acumatica.'),
        flowStep('4', 'System',    'Job completes. Hangfire schedules next run for the following day.', 'Audit log entry created for the sync run.'),
      ]),
      gap(),
      imgPlaceholder('Insert Diagram — Background Sync / Hangfire Job Schedule Here'),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 5. Page & Route Map
      // ══════════════════════════════════════════════════════════════════════
      new Paragraph({ children: [new PageBreak()] }),
      h1('5. Page & Route Map'),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(28), pct(24), pct(14), pct(34)],
        rows: [
          thr('Route', 'Page', 'Access', 'Description'),
          tdr(true,  '/login',                   'Login Page',             'Public',    'Acumatica OAuth sign-in button.'),
          tdr(true,  '/auth/callback',            'Auth Callback',          'Public',    'Handles OAuth redirect; exchanges code for app JWT.'),
          tdr(true,  '/',                         'Dashboard',              'All roles', 'KPI cards + 10 recent documents.'),
          tdr(true,  '/documents',                'Document List',          'All roles', 'Paginated list with status filter and filename search.'),
          tdr(true,  '/upload',                   'Upload Page',            'All roles', 'Drag-and-drop multi-file upload.'),
          tdr(true,  '/documents/:id',            'Document Detail',        'All roles', 'OCR summary, field list, action bar, version history.'),
          tdr(true,  '/documents/:id/verify',     'Review Fields',          'All roles', 'Split-pane: PDF viewer (left) + FieldReviewPanel (right).'),
          tdr(true,  '/admin/field-mapping',      'Field Mapping Config',   'Admin',     'Configure extraction and validation rules per document type.'),
          tdr(true,  '/admin/users',              'User Management',        'Admin',     'View and manage Acumatica-synced users.'),
          tdr(true,  '/admin/audit-log',          'Audit Log',              'Admin',     'Browse all system audit events.'),
        ],
      }),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 6. Document Status State Machine
      // ══════════════════════════════════════════════════════════════════════
      h1('6. Document Status State Machine'),
      p('Each document moves through the following statuses during its lifecycle:'),
      gap(),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(20), pct(40), pct(40)],
        rows: [
          thr('Status', 'Meaning', 'Next Possible Status'),
          tdr(true, 'Uploaded',         'File received. OCR has not been run yet.',                                   'Processing (when Run OCR clicked)'),
          tdr(true, 'Processing',       'OCR pipeline is running.',                                                    'PendingReview (on success)'),
          tdr(true, 'PendingReview',    'OCR complete. Fields extracted. Validation auto-ran.',                        'ReviewInProgress, Checked, or Processing (re-run)'),
          tdr(true, 'ReviewInProgress', 'User has opened the Review Fields page at least once.',                       'Checked or Processing (re-run)'),
          tdr(true, 'Checked',          'User confirmed review is complete. Document considered human-verified.',      'Processing (re-run OCR if needed)'),
        ],
      }),
      gap(),
      imgPlaceholder('Insert Diagram — Document Status State Machine Here'),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 7. ERP Integration Details
      // ══════════════════════════════════════════════════════════════════════
      h1('7. ERP Integration Details'),
      h3('Supported Acumatica Entities'),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(30), pct(22), pct(48)],
        rows: [
          thr('ERP Mapping Key', 'Acumatica Entity', 'Example Use Case'),
          tdr(true, 'Vendor:VendorName',       'Vendor',           'Verify vendor name on AP invoice matches an active vendor in Acumatica.'),
          tdr(true, 'Bill:VendorRef',          'AP Invoice (Bill)', 'Check vendor invoice reference number exists as an AP Bill.'),
          tdr(true, 'PurchaseOrder:OrderNbr',  'Purchase Order',   'Verify PO number referenced on invoice exists.'),
          tdr(true, 'InventoryItem:InventoryCD','Inventory Item',   'Validate item code on line items against inventory master.'),
          tdr(true, 'Customer:CustomerName',   'Customer',         'Verify customer name on a statement matches an ERP customer record.'),
          tdr(true, 'Currency:CurrencyID',     'Currency',         'Validate currency code is active in Acumatica.'),
        ],
      }),
      gap(),
      h3('Validation Success Label (ERP Response Field)'),
      p('Each field mapping config can specify a "Validation Success Label". When validation passes, this key is looked up in the Acumatica response data and displayed in the UI next to the ✓ badge.'),
      gap(),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(35), pct(20), pct(45)],
        rows: [
          thr('ERP Mapping Key', 'Success Label', 'UI Display'),
          tdr(true, 'Vendor:VendorName', 'vendorId', '✓ vendorId: V00001'),
          tdr(true, 'Bill:VendorRef',    'RefNbr',   '✓ RefNbr: GESB-001'),
        ],
      }),
      gap(),
      imgPlaceholder('Insert Diagram — ERP Validation Flow / Acumatica Integration Here'),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 8. Confidence Scoring
      // ══════════════════════════════════════════════════════════════════════
      h1('8. Confidence Scoring'),
      p('Every extracted field receives a confidence score between 0 and 1, computed as a weighted combination of three factors:'),
      gap(),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(30), pct(15), pct(55)],
        rows: [
          thr('Factor', 'Weight', 'Description'),
          tdr(true, 'Tesseract OCR score',    '40%', 'Raw confidence from Tesseract\'s character recognition engine.'),
          tdr(true, 'Regex strategy match',   '30%', 'Whether the extracted value matches the configured regex pattern for the field.'),
          tdr(true, 'Keyword proximity',      '30%', 'How close the extracted value is to the configured keyword anchor in the document layout.'),
        ],
      }),
      gap(),
      p('Confidence thresholds (configurable per field):'),
      bullet('≥ 90% — High confidence (green badge).'),
      bullet('70–89% — Medium confidence (amber badge).'),
      bullet('< 70% — Low confidence (red badge). Field is highlighted for review.'),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 9. Glossary
      // ══════════════════════════════════════════════════════════════════════
      h1('9. Glossary'),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(25), pct(75)],
        rows: [
          thr('Term', 'Definition'),
          tdr(true, 'OCR',                  'Optical Character Recognition — extracts text and structured data from scanned or digital documents.'),
          tdr(true, 'ERP',                  'Enterprise Resource Planning — in this system, Acumatica cloud ERP.'),
          tdr(true, 'Field Mapping Config', 'Admin-defined rules that tell the OCR pipeline what fields to extract from a document type and how to validate them.'),
          tdr(true, 'ExtractedField',       'A single piece of data extracted from a document (e.g., vendorName = "ABC Corp"), with a confidence score and bounding box.'),
          tdr(true, 'ValidationResult',     'The outcome of a validator run. Statuses: Found, Not Found, Warning, Skipped.'),
          tdr(true, 'ERP Mapping Key',      '"Entity:Field" format directing the validator to an Acumatica entity (e.g., "Vendor:VendorName").'),
          tdr(true, 'Success Label',        'The ERP response field key shown in the UI when validation passes (e.g., "RefNbr", "vendorId").'),
          tdr(true, 'Header Field',         'A single-value field on a document (e.g., invoice number, vendor name). Appears in the Document Fields section.'),
          tdr(true, 'Table Field',          'A repeating/multi-value field (allowMultiple = true), such as line items on an invoice. Appears in the Table Data section.'),
          tdr(true, 'Hangfire',             'Background job scheduler used for the nightly Acumatica user sync job.'),
          tdr(true, 'DXA / Twip',          'Unit of measurement in Word documents. 1 inch = 1440 dxa/twips. Used for page margins and column widths.'),
        ],
      }),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // 10. Change Log
      // ══════════════════════════════════════════════════════════════════════
      new Paragraph({ children: [new PageBreak()] }),
      h1('10. Change Log'),
      p('One entry per change session. Each row records the user prompt that triggered the change and a one-line summary of what was changed.'),
      gap(),
      new Table({
        width: { size: AVAIL, type: WidthType.DXA },
        columnWidths: [pct(15), pct(40), pct(45)],
        rows: [
          thr('Date', 'Prompt', 'Changes'),
          tdr(true, '2026-03-09', 'Force logout and redirect to login when Acumatica session expires', 'Added session-expiry detection in Axios interceptor; API returns 401 on expired Acumatica token; frontend forces logout and redirects to /login.'),
          tdr(true, '2026-03-09', 'Show warning when Run Validation is clicked without an Acumatica session', 'FieldReviewPanel and DocumentDetailPage show a warning banner if no active Acumatica session is detected before validation runs.'),
          tdr(true, '2026-03-09', 'Add vendor exclusion, vendor-invoice cross-check, CI/CD, post-upload OCR prompt, and change log docs', 'Added vendor exclusion list, cross-check validator, GitHub Actions CI/CD pipeline, auto-OCR prompt after upload, and documentation updates.'),
          tdr(true, '2026-03-09', 'Change the change log format to record only prompt and changes in 1 line each', 'Section 10 change log added to generate-doc.mjs with a simple 3-column table (Date, Prompt, Changes).'),
          tdr(true, '2026-03-09', 'Vendor sync page, document grouping by vendor, outstanding balance/aging validation, Docker build', 'Added Vendor entity + migration, vendor sync service (Acumatica → local DB), VendorManagementPage (Manager+), vendor filter + group-by-vendor in DocumentListPage, ErpVendorStatementValidator for outstanding balance and aging (VendorStatement:* ERP keys), auto-links document to vendor after validation, FetchOpenBillsForVendorAsync in AcumaticaClient.'),
          tdr(true, '2026-03-10', 'Add manual entry fields, rubbish bin, vendor sync, vendor statement validation, and checkbox field config', 'Added ManualEntry flag to ExtractedField, delete-document (rubbish bin) action in UI, checkbox FieldType with configurable true/false values, vendor statement field validation via ERP keys, and updated OcrPipelineService to support the new field types.'),
          tdr(true, '2026-03-10', 'Let Claude auto-detect settled/paid status for checkbox table fields', 'OcrPipelineService uses Claude AI to infer boolean settled/paid status from AllowMultiple table fields; ClaudeFieldCorrectionService corrects low-confidence fields using raw OCR context.'),
          tdr(true, '2026-03-10', 'Fix validation spinner, checkbox in detail table, and dependency-chain skip logic', 'Fixed per-field validation spinner not dismissing; checkbox fields now render correctly in DocumentDetailPage table; dependency-chain validation correctly skips dependent fields when parent fails.'),
          tdr(true, '2026-03-10', 'Fix auto-validation UI + truncate table row validation messages', 'Auto-validation no longer re-triggers on every render; table row validation messages truncated to single line with tooltip; FieldReviewPanel shows concise inline validation status.'),
          tdr(true, '2026-03-10', 'Fix 499 client-disconnect error and stuck-Processing documents', 'OcrPipelineService uses a detached CancellationToken for OCR jobs so client disconnects do not abort processing; documents no longer get stuck in Processing state.'),
          tdr(true, '2026-03-10', 'Fix simultaneous pass/fail messages on same field', 'Validation state machine prevents pass and fail banners from appearing at the same time; only the latest validation result is shown per field.'),
          tdr(true, '2026-03-10', 'Add per-field re-validate button on status icon hover in Document Detail', 'Hovering the status icon on a field row in DocumentDetailPage reveals a re-validate button that triggers single-field re-validation without running the full document validation.'),
          tdr(true, '2026-03-10', 'Pad AllowMultiple table columns to uniform row count after OCR', 'OcrPipelineService pads all AllowMultiple (table) field columns to the same row count so the UI renders a complete, aligned table even when OCR misses values in some rows.'),
          tdr(true, '2026-03-11', 'Reformat doc as architecture & user guide; remove title colors; grayscale tables; add image placeholders', 'Cover retitled to "Architecture & User Guide"; all heading/title colors removed (plain black); table headers changed to dark charcoal with white text; row shading changed to light gray; added image placeholder boxes after System Overview, OCR Pipeline, Review Fields, and State Machine sections.'),
        ],
      }),
      gap(),

    ],
  }],
});

const buffer = await Packer.toBuffer(doc);
const outPath = 'C:/Users/Harmen/Documents/test/OCR_ERP_System_App_Flow_Plan.docx';
writeFileSync(outPath, buffer);
console.log('✓ Document written to:', outPath);
