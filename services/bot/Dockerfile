FROM ghcr.io/astral-sh/uv:python3.13-bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    libopus0 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY services/bot/pyproject.toml services/bot/uv.lock services/bot/.python-version ./
RUN uv sync --frozen --no-install-project

COPY shared/proto/ /app/proto/
COPY services/bot/ .

RUN chmod +x scripts/gen-proto.sh && \
    uv run scripts/gen-proto.sh \
        --proto-dir /app/proto \ 
        --out-dir /app/src/generated

CMD ["uv", "run", "src/main.py"]
