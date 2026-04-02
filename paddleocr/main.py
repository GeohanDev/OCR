"""
PaddleOCR microservice — PP-OCRv4 text detection + recognition.
Uses the basic PaddleOCR engine (NOT PPStructure) which is stable on CPU.

PP-Structure's layout/table model causes memory corruption ("double free") in CPU
Docker environments and only extracts the header row. The plain PaddleOCR engine
returns ALL text lines with bounding boxes, which is exactly what the .NET
FieldExtractor needs for keyword-anchor + column-position table extraction.

Endpoints:
  POST /ocr      — process page images, return text blocks with bounding boxes
  GET  /health   — liveness probe
"""

import base64
import io
import logging
import sys
import tempfile
import os

import cv2
import numpy as np
from PIL import Image
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

# ── Logging ───────────────────────────────────────────────────────────────────
logging.basicConfig(stream=sys.stdout, level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("paddleocr-svc")

from paddleocr import PaddleOCR

app = FastAPI(title="PaddleOCR microservice", version="2.0.0")

# ── Lazy engine initialisation ─────────────────────────────────────────────────
_engine: PaddleOCR | None = None


def _get_engine() -> PaddleOCR:
    global _engine
    if _engine is None:
        log.info("Initialising PaddleOCR engine (first request) …")
        _engine = PaddleOCR(
            use_textline_score=False, # keep blurry/low-contrast header text (e.g. vendor name)
            det_db_box_thresh=0.3,    # lower box threshold to detect faint text in headers
            lang="en",                # English + Latin script (covers Malay)
            use_gpu=False,            # CPU inference
            cls=False,                # no angle classifier (crashes on this build)
            show_log=False,
        )
        log.info("PaddleOCR engine ready.")
    return _engine


# ── Request / response models ─────────────────────────────────────────────────

class PageRequest(BaseModel):
    page: int
    image_base64: str   # PNG bytes, base64-encoded; must NOT be binarised


class OcrRequest(BaseModel):
    pages: list[PageRequest]


class PdfRequest(BaseModel):
    pdf_base64: str                       # Raw PDF bytes, base64-encoded
    page_crops: dict[str, int] | None = None  # {page_num_str: crop_y_max_pixels at dpi=200}


class BlockResult(BaseModel):
    text: str
    confidence: float
    bbox: list[int]     # [x, y, width, height]
    page: int


class TableResult(BaseModel):
    html: str
    bbox: list[int]
    page: int


class PageResult(BaseModel):
    page: int
    full_text: str
    blocks: list[BlockResult]
    tables: list[TableResult]


class OcrResponse(BaseModel):
    pages: list[PageResult]


# ── Helpers ───────────────────────────────────────────────────────────────────

def _deskew(img_bgr: np.ndarray) -> np.ndarray:
    """
    Detect and correct document skew so text lines are horizontal.

    Strategy: convert to binary, run Probabilistic Hough on the edges to find
    the dominant line angle, then rotate by that angle.  Only angles within
    ±15 ° are corrected — larger rotations likely mean intentional layout or
    a misdetection, so the image is returned unchanged.
    """
    gray = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    # Otsu threshold → binary (text dark on white background)
    _, binary = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU)
    edges = cv2.Canny(binary, 50, 150, apertureSize=3)

    h, w = img_bgr.shape[:2]
    # Require lines at least 1/5 of the image width to ignore short noise blobs
    min_len = max(w // 5, 50)
    lines = cv2.HoughLinesP(edges, 1, np.pi / 180,
                            threshold=80, minLineLength=min_len, maxLineGap=15)
    if lines is None or len(lines) == 0:
        return img_bgr

    angles = []
    for line in lines:
        x1, y1, x2, y2 = line[0]
        if x2 != x1:  # skip vertical lines
            angles.append(np.degrees(np.arctan2(y2 - y1, x2 - x1)))

    if not angles:
        return img_bgr

    median_angle = float(np.median(angles))
    if abs(median_angle) > 15:
        log.debug("Skew angle %.2f° exceeds ±15° limit — skipping deskew", median_angle)
        return img_bgr

    if abs(median_angle) < 0.1:
        return img_bgr  # negligible skew — nothing to do

    log.info("Deskewing image by %.2f°", -median_angle)
    center = (w // 2, h // 2)
    M = cv2.getRotationMatrix2D(center, median_angle, 1.0)
    return cv2.warpAffine(img_bgr, M, (w, h),
                          flags=cv2.INTER_LINEAR,
                          borderMode=cv2.BORDER_CONSTANT,
                          borderValue=(255, 255, 255))


def _quad_to_xywh(pts) -> list[int]:
    """Convert [[x1,y1],[x2,y1],[x2,y2],[x1,y2]] quad to [x, y, w, h]."""
    try:
        xs = [int(p[0]) for p in pts]
        ys = [int(p[1]) for p in pts]
        return [min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys)]
    except Exception:
        return [0, 0, 0, 0]


def _process_page(page_num: int, img_bgr: np.ndarray) -> PageResult:
    """
    Run PaddleOCR on a single BGR page image.

    PaddleOCR.ocr() returns:
      [ [  [quad_pts, (text, confidence)], ... ]  ]
      Outer list = pages (always 1 for ndarray input).
      Inner list = one entry per detected text line.
    """
    try:
        raw = _get_engine().ocr(img_bgr, cls=False)
    except Exception as exc:
        log.exception("PaddleOCR failed on page %d", page_num)
        raise HTTPException(status_code=500, detail=f"OCR failed on page {page_num}: {exc}")

    # raw is [ [lines...] ] for a single ndarray — flatten one level
    lines = raw[0] if (raw and isinstance(raw[0], list)) else []

    blocks: list[BlockResult] = []
    for line in (lines or []):
        try:
            quad_pts, (text, conf) = line[0], line[1]
            text = str(text).strip()
            if text:
                blocks.append(BlockResult(
                    text=text,
                    confidence=float(conf),
                    bbox=_quad_to_xywh(quad_pts),
                    page=page_num,
                ))
        except Exception:
            continue

    # Sort into reading order: top-to-bottom, then left-to-right within each row.
    # Bin lines whose Y centres are within 8 px into the same row so that
    # columns in the same table row sort left-to-right correctly.
    # Bin size 15 px at 200 DPI ≈ 50 % of a typical row height (25–35 px).
    # This groups cells in the same table row (which may differ by up to ~12 px
    # in top-Y) while still keeping adjacent rows apart.
    ROW_BIN = 15
    blocks.sort(key=lambda b: (round(b.bbox[1] / ROW_BIN), b.bbox[0]))

    # Preserve document structure: group blocks into visual rows, then place each
    # block at its proportional character column using the X bounding-box coordinate.
    # At 200 DPI a character is roughly CHAR_WIDTH pixels wide, so dividing X by
    # CHAR_WIDTH gives the column index.  Gaps between columns are filled with spaces
    # so Claude sees the same spatial layout as the original document — essential for
    # vendor-statement tables where column position identifies the field (date vs ref
    # vs amount).  Gaps are capped at MAX_GAP chars to prevent runaway whitespace
    # from very wide margins.
    CHAR_WIDTH = 10   # pixels per character at 200 DPI (≈ 10 pt font)
    MAX_GAP    = 60   # cap inter-column gaps so lines stay readable

    row_bins: dict[int, list[BlockResult]] = {}
    for b in blocks:
        key = round(b.bbox[1] / ROW_BIN)
        row_bins.setdefault(key, []).append(b)

    def _row_to_line(row_blocks: list[BlockResult]) -> str:
        sorted_blocks = sorted(row_blocks, key=lambda x: x.bbox[0])
        parts: list[str] = []
        current_end_col = 0
        for blk in sorted_blocks:
            target_col = blk.bbox[0] // CHAR_WIDTH
            gap = min(max(1, target_col - current_end_col), MAX_GAP)
            parts.append(" " * gap + blk.text)
            current_end_col = target_col + len(blk.text)
        return "".join(parts).lstrip()

    full_text = "\n".join(
        _row_to_line(row_blocks)
        for _, row_blocks in sorted(row_bins.items())
    )
    log.info("Page %d: %d text blocks extracted", page_num, len(blocks))

    return PageResult(page=page_num, full_text=full_text, blocks=blocks, tables=[])


# ── Routes ────────────────────────────────────────────────────────────────────

@app.post("/ocr", response_model=OcrResponse)
def run_ocr(req: OcrRequest) -> OcrResponse:
    if not req.pages:
        raise HTTPException(status_code=400, detail="No pages provided.")

    results: list[PageResult] = []
    for page_req in req.pages:
        try:
            img_bytes = base64.b64decode(page_req.image_base64)
            pil_img = Image.open(io.BytesIO(img_bytes)).convert("RGB")
            # PIL gives RGB; PaddleOCR (OpenCV internally) needs BGR — swap R↔B.
            img_bgr = np.array(pil_img)[:, :, ::-1].copy()
            # Apply CLAHE on the luminance channel so locally dark/faint areas
            # (e.g. scanned letterhead, logo region) are enhanced independently
            # from the already-sharp body text — more effective than a uniform
            # contrast boost for mixed-content scanned documents.
            lab = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2LAB)
            l, a, b = cv2.split(lab)
            clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
            lab = cv2.merge([clahe.apply(l), a, b])
            img_bgr = cv2.cvtColor(lab, cv2.COLOR_LAB2BGR)
        except Exception as exc:
            raise HTTPException(
                status_code=400,
                detail=f"Cannot decode image for page {page_req.page}: {exc}")

        # Crop a small margin from each edge to remove scanner borders/shadows
        # that can confuse the text detector.
        m = 10
        h, w = img_bgr.shape[:2]
        img_bgr = img_bgr[m:h-m, m:w-m]

        # Deskew: rotate so text lines are horizontal.
        img_bgr = _deskew(img_bgr)

        results.append(_process_page(page_req.page, img_bgr))

    return OcrResponse(pages=results)


@app.post("/ocr-pdf", response_model=OcrResponse)
def run_ocr_pdf(req: PdfRequest) -> OcrResponse:
    """
    Render every page of a PDF with Poppler (pdf2image) then run PaddleOCR.
    Poppler composites ALL PDF layers — digital text, embedded raster images,
    Form XObjects, etc. — into a single raster, so hybrid PDFs (scanned
    letterhead + digital body text) are fully captured.
    """
    from pdf2image import convert_from_bytes

    pdf_bytes = base64.b64decode(req.pdf_base64)
    try:
        pil_images = convert_from_bytes(pdf_bytes, dpi=200, fmt="png")
    except Exception as exc:
        raise HTTPException(status_code=400, detail=f"PDF render failed: {exc}")

    results: list[PageResult] = []
    for page_num, pil_img in enumerate(pil_images, start=1):
        img_bgr = np.array(pil_img.convert("RGB"))[:, :, ::-1].copy()

        # CLAHE for local contrast enhancement (same as /ocr endpoint)
        lab = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2LAB)
        l, a, b = cv2.split(lab)
        clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
        lab = cv2.merge([clahe.apply(l), a, b])
        img_bgr = cv2.cvtColor(lab, cv2.COLOR_LAB2BGR)

        # Crop margin
        m = 10
        h, w = img_bgr.shape[:2]
        img_bgr = img_bgr[m:h - m, m:w - m]

        # Deskew: rotate so text lines are horizontal.
        img_bgr = _deskew(img_bgr)

        # Apply per-page image-area crop hint (letterhead only — body text supplied by PdfPig)
        if req.page_crops:
            crop_y = req.page_crops.get(str(page_num))
            if crop_y is not None:
                # Adjust for the margin already removed from the top
                crop_y_adj = max(0, min(crop_y - m, img_bgr.shape[0]))
                img_bgr = img_bgr[:crop_y_adj, :]
                log.info("Page %d: cropped to image area (%d px)", page_num, crop_y_adj)

        results.append(_process_page(page_num, img_bgr))

    return OcrResponse(pages=results)


@app.get("/health")
def health():
    return {"status": "ok", "engine": "PaddleOCR-PP-OCRv4"}
