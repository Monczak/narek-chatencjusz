FROM nvidia/cuda:13.1.1-cudnn-runtime-ubuntu24.04

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=ghcr.io/astral-sh/uv:latest /uv /uvx /usr/local/bin/

WORKDIR /app

COPY services/asr/pyproject.toml services/asr/uv.lock services/asr/.python-version ./
RUN uv sync --frozen --no-install-project

COPY shared/proto/ /app/proto/
COPY services/asr/ .

RUN chmod +x scripts/gen-proto.sh && \
    uv run scripts/gen-proto.sh \
        --proto-dir /app/proto \ 
        --out-dir /app/src/generated

CMD ["uv", "run", "src/main.py"]
