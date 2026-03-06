import {
  Document, Packer, Paragraph, TextRun, HeadingLevel, AlignmentType,
  Table, TableRow, TableCell, WidthType, ShadingType,
  convertInchesToTwip, LevelFormat,
} from 'docx';
import { writeFileSync } from 'fs';

// ── Page & column constants ───────────────────────────────────────────────────
const AVAIL = 8784;
const pct = (p) => Math.round(AVAIL * p / 100);

// ── Paragraph helpers ─────────────────────────────────────────────────────────
const h1 = (text) => new Paragraph({ text, heading: HeadingLevel.HEADING_1, spacing: { before: 400, after: 120 } });
const h2 = (text) => new Paragraph({ text, heading: HeadingLevel.HEADING_2, spacing: { before: 280, after: 80 } });
const p  = (text, extra = {}) => new Paragraph({ children: [new TextRun({ text, size: 22, ...extra })], spacing: { after: 80 } });
const gap = () => new Paragraph({ children: [new TextRun({ text: '' })], spacing: { after: 80 } });

// ── Table cell helper ─────────────────────────────────────────────────────────
function tcell(text, { isBold = false, fill = undefined } = {}) {
  return new TableCell({
    shading: fill ? { fill, type: ShadingType.CLEAR, color: 'auto' } : undefined,
    children: [new Paragraph({
      children: [new TextRun({ text: text ?? '', size: 20, bold: isBold })],
      spacing: { after: 40 },
    })],
    margins: { top: 80, bottom: 80, left: 100, right: 100 },
  });
}

function thr(...labels) {
  return new TableRow({
    tableHeader: true,
    children: labels.map(l => tcell(l, { isBold: true, fill: '1E3A5F' })),
  });
}

// Change log table: # | Item / Files Modified | Description
// columns: 6 / 35 / 59 (%)
const CL_COLS = [pct(6), pct(35), pct(59)];

function clTable(rows) {
  return new Table({
    width: { size: AVAIL, type: WidthType.DXA },
    columnWidths: CL_COLS,
    rows: [thr('#', 'Item / Files Modified', 'Description of Change'), ...rows],
  });
}

function clRow(num, item, description) {
  return new TableRow({ children: [
    tcell(num, { isBold: true }),
    tcell(item),
    tcell(description),
  ]});
}

function promptPara(text) {
  return new Paragraph({
    children: [new TextRun({ text: `"${text}"`, size: 21, italics: true, color: '374151' })],
    spacing: { after: 120 },
    indent: { left: 360 },
  });
}

// ─────────────────────────────────────────────────────────────────────────────

const doc = new Document({
  numbering: {
    config: [{ reference: 'steps', levels: [{ level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT }] }],
  },
  styles: {
    paragraphStyles: [
      {
        id: 'Heading1',
        name: 'Heading 1',
        run:  { size: 36, bold: true, color: '1E3A5F' },
        paragraph: { spacing: { before: 400, after: 120 } },
      },
      {
        id: 'Heading2',
        name: 'Heading 2',
        run:  { size: 28, bold: true, color: '1E3A5F' },
        paragraph: { spacing: { before: 280, after: 80 } },
      },
    ],
  },
  sections: [{
    properties: {
      page: {
        margin: {
          top:    convertInchesToTwip(1),
          bottom: convertInchesToTwip(1),
          left:   convertInchesToTwip(1.2),
          right:  convertInchesToTwip(1.2),
        },
      },
    },
    children: [

      // ── Cover ─────────────────────────────────────────────────────────────
      new Paragraph({
        children: [new TextRun({ text: 'OCR ERP Integration System', bold: true, size: 56, color: '1E3A5F' })],
        alignment: AlignmentType.CENTER,
        spacing: { before: 1440, after: 240 },
      }),
      new Paragraph({
        children: [new TextRun({ text: 'Change Log', bold: true, size: 40, color: '2563EB' })],
        alignment: AlignmentType.CENTER,
        spacing: { after: 240 },
      }),
      new Paragraph({
        children: [new TextRun({ text: 'Records all prompts and corresponding system changes by session.', size: 22, color: '6B7280' })],
        alignment: AlignmentType.CENTER,
        spacing: { after: 1440 },
      }),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // Session 1 — 6 March 2026
      // ══════════════════════════════════════════════════════════════════════
      h2('Session 1  ·  6 March 2026'),
      p('Prompt (verbatim):', { bold: true }),
      promptPara(
        '1. The OCR may capture our company name instead of the supplier or vendor company name, suggest a way to solve it. ' +
        '2. For the AP bill, suggest a way to check the correct vendor before the invoice nbr checking. ' +
        '3. Make the files always update to GitHub and consider the database backup method.'
      ),
      gap(),
      clTable([
        clRow('1',
          'Own-company vendor name exclusion\n\nappsettings.json\n.env.example\ndocker-compose.yml\nErpVendorNameValidator.cs',
          'Added a configurable list of own-company name variants (e.g. "Geohan Sdn Bhd") to the application settings. When the OCR engine extracts the vendor name field and the value matches any name in this exclusion list, validation immediately fails with a clear message prompting the user to correct the field. This prevents the common error where OCR picks up the recipient\'s name (your company) from the invoice header instead of the actual supplier name. The list is stored as an array in appsettings.json for local development and as a semicolon-delimited environment variable (OWN_COMPANY_NAMES) for Docker deployments.'
        ),
        clRow('2',
          'Vendor verification before AP invoice check\n\nIVendorResolutionContext.cs (new)\nVendorResolutionContext.cs (new)\nErpVendorNameValidator.cs\nErpApInvoiceValidator.cs\nValidationService.cs\nServiceCollectionExtensions.cs',
          'Introduced a scoped IVendorResolutionContext service that acts as a shared memory slot within a single validation run. When the VendorName field is validated and the vendor is successfully found in Acumatica, the resolved Acumatica VendorID is stored in this context. The AP invoice validator then reads that VendorID and cross-checks it against the VendorID returned from the invoice lookup. If they do not match, validation fails with a message identifying the mismatch. ValidationService was also updated to process fields in DisplayOrder sequence so vendor name is always resolved before invoice number, regardless of the order fields appear in the document.'
        ),
        clRow('3',
          'GitHub CI/CD workflow\n\n.github/workflows/build.yml (new)',
          'Created a GitHub Actions workflow that runs automatically on every push and pull request to the main branch. The workflow has three stages: (1) Backend — restores NuGet packages, builds the .NET 8 solution in Release mode, and runs the test suite. (2) Frontend — installs npm dependencies and runs the Vite/TypeScript production build to catch type errors. (3) Docker — builds both API and frontend Docker images; on merges to main the images are pushed to GitHub Container Registry (ghcr.io) tagged with :latest and the commit SHA. On pull requests Docker only builds without pushing so Dockerfile errors are caught before merging.'
        ),
      ]),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // Session 2 — 6 March 2026
      // ══════════════════════════════════════════════════════════════════════
      h2('Session 2  ·  6 March 2026'),
      p('Prompt (verbatim):', { bold: true }),
      promptPara(
        '1. Remember that build to docker after every updates. ' +
        '2. Autorun the OCR button and the validation button after user uploaded the documents. ' +
        'After user uploaded the document, prompt question to ask user, is they want to directly start OCR and validation, ' +
        'with the notifying the document type selected.'
      ),
      gap(),
      clTable([
        clRow('1',
          'Docker build on every push and pull request\n\n.github/workflows/build.yml',
          'Updated the GitHub Actions workflow so the Docker build step runs on every trigger — not just on main branch merges. On pull requests, both Docker images are built but not pushed, validating that the Dockerfiles are not broken before merging. On every push to main, both images are built and pushed to the container registry. The job-level if: guard was removed and replaced with a per-step push flag, making the intent explicit.'
        ),
        clRow('2',
          'Post-upload OCR & validation prompt\n\nUploadPage.tsx',
          'Replaced the previous automatic redirect-after-upload behaviour with a confirmation modal that appears as soon as all files finish uploading. The modal shows the number of successfully uploaded files and the document type selected (or "Auto-detect"). The user is asked whether to start OCR extraction and validation immediately. Clicking "Start OCR & Validation" calls the OCR process endpoint then the validation run endpoint; for a single file the user is navigated to the document detail page after processing; for multiple files OCR is fired in parallel and the user is taken to the documents list. Clicking "Skip for Now" navigates without starting any processing.'
        ),
      ]),
      gap(),

      // ══════════════════════════════════════════════════════════════════════
      // Session 3 — 6 March 2026
      // ══════════════════════════════════════════════════════════════════════
      h2('Session 3  ·  6 March 2026'),
      p('Prompt (verbatim):', { bold: true }),
      promptPara('Helps to also record the prompt and changes log in a word document, the change log no need to show the code changes, show the items modify and explain a bit on it'),
      gap(),
      clTable([
        clRow('1',
          'Change log Word document\n\nscripts/generate-changelog.mjs (new)\nscripts/generate-doc.mjs',
          'Created a dedicated generate-changelog.mjs script that produces a standalone OCR_ERP_System_Change_Log.docx. The document records each session\'s verbatim prompt and a table listing the items modified with plain-English descriptions (no code). The change log section was removed from the App Flow Plan document (generate-doc.mjs) to keep the two documents separate. Both documents are regenerated by running the respective script with Node.js from the scripts/ folder.'
        ),
      ]),
      gap(),

    ],
  }],
});

const buffer = await Packer.toBuffer(doc);
const outPath = 'C:/Users/Harmen/Downloads/test/OCR_ERP_System_Change_Log.docx';
writeFileSync(outPath, buffer);
console.log('✓ Change log written to:', outPath);
