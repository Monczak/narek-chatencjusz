FROM ghcr.io/astral-sh/uv:alpine

RUN apk add --no-cache bash

WORKDIR /app

COPY services/bot/pyproject.toml services/bot/uv.lock ./
RUN uv sync --frozen --no-install-project

COPY shared/proto/ /app/proto/
COPY services/bot/ .

RUN chmod +x scripts/gen-proto.sh && \
    uv run scripts/gen-proto.sh \
        --proto-dir /app/proto \ 
        --out-dir /app/src/generated

CMD ["uv", "run", "src/main.py"]
