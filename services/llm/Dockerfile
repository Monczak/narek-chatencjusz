FROM ollama/ollama:latest

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY services/llm/entrypoint.sh /init/entrypoint.sh

ENTRYPOINT [ "/bin/bash", "/init/entrypoint.sh" ]
