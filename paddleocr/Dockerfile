# ── PaddleOCR microservice ────────────────────────────────────────────────────
# Runs PP-OCRv5 + PP-StructureV3 as a FastAPI REST service on port 8001.
# First run downloads model weights into /root/.paddleocr (mounted as a volume
# in docker-compose so the models persist across container restarts).

FROM python:3.10-slim

# System deps required by PaddleOCR (libGL for OpenCV, libgomp for paddle)
RUN apt-get update && apt-get install -y --no-install-recommends \
        libglib2.0-0 \
        libsm6 \
        libxext6 \
        libxrender1 \
        libgomp1 \
        libgl1 \
        curl \
        poppler-utils \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
# Install CPU-only PaddlePaddle first (the GPU build is much larger)
RUN pip install --no-cache-dir paddlepaddle==2.6.2 -f https://www.paddlepaddle.org.cn/whl/linux/mkl/avx/stable.html \
    && pip install --no-cache-dir -r requirements.txt

COPY main.py .

# Models are downloaded on first request to ~/.paddleocr — mount as a volume
# so they survive container rebuilds.
VOLUME ["/root/.paddleocr"]

EXPOSE 8001
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8001", "--workers", "1"]
