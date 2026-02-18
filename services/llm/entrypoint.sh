#!/bin/bash
set -e

MODEL_NAME="${OLLAMA_MODEL_NAME:-pearlsac}"
MODELFILE_PATH="${OLLAMA_MODELFILE_PATH:-/modelfiles/Modelfile}"

# Start Ollama in the background
ollama serve &
OLLAMA_PID=$!

echo "[init] Waiting for Ollama to be ready..."
until curl -sf http://localhost:11434/ > /dev/null 2>&1; do
    sleep 1
    echo "[init] Ollama not ready yet, retrying..."
done
echo "[init] Ollama is up."

# Create (or recreate) the named model from our Modelfile
echo "[init] Creating model '$MODEL_NAME' from $MODELFILE_PATH..."
ollama create "$MODEL_NAME" -f "$MODELFILE_PATH"
echo "[init] Model created."

# Warm up - send an empty chat request so Ollama loads the model into VRAM
echo "[init] Warming up '$MODEL_NAME'..."
curl -sf http://localhost:11434/api/chat \
    -H "Content-Type: application/json" \
    -d "{\"model\":\"$MODEL_NAME\",\"messages\":[],\"stream\":false}" \
    > /dev/null
echo "[init] '$MODEL_NAME' is loaded and ready."

# Hand off to the Ollama server process
wait $OLLAMA_PID
