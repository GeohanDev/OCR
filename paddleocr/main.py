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

import numpy as np
from PIL import Image, ImageEnhance
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
    blocks.sort(key=lambda b: (round(b.bbox[1] / 8), b.bbox[0]))

    # Preserve document structure: group blocks into visual rows and join with
    # newlines so the resulting text starts from the top of the page and each
    # visual line (vendor name, date header, table row, etc.) is on its own line.
    # This lets Claude identify the vendor name and other header fields correctly.
    row_bins: dict[int, list[BlockResult]] = {}
    for b in blocks:
        key = round(b.bbox[1] / 8)
        row_bins.setdefault(key, []).append(b)
    full_text = "\n".join(
        "  ".join(b.text for b in sorted(row_blocks, key=lambda x: x.bbox[0]))
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
            # Boost contrast so blurry header text (vendor name, address) is
            # more visible to the detector. Factor 1.4 lifts faint text without
            # over-saturating the already-sharp table body lines.
            pil_img = ImageEnhance.Contrast(pil_img).enhance(1.4)
            # PIL gives RGB; PaddleOCR (OpenCV internally) needs BGR — swap R↔B.
            img_rgb = np.array(pil_img)
            img_bgr = img_rgb[:, :, ::-1].copy()
        except Exception as exc:
            raise HTTPException(
                status_code=400,
                detail=f"Cannot decode image for page {page_req.page}: {exc}")

        results.append(_process_page(page_req.page, img_bgr))

    return OcrResponse(pages=results)


@app.get("/health")
def health():
    return {"status": "ok", "engine": "PaddleOCR-PP-OCRv4"}
