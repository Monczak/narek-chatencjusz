#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DEFAULT_PROTO_DIR="${SCRIPT_DIR}/../../../shared/proto"
DEFAULT_OUT_DIR="${SCRIPT_DIR}/../src/generated"

while [[ $# -gt 0 ]]; do
  case $1 in
    --proto-dir) PROTO_DIR="$2"; shift ;;
    --out-dir)   OUT_DIR="$2";   shift ;;
    *) echo "Unknown argument: $1"; exit 1 ;;
  esac
  shift
done

PROTO_DIR="${PROTO_DIR:-$(realpath "$DEFAULT_PROTO_DIR")}"
OUT_DIR="${OUT_DIR:-$(realpath "$DEFAULT_OUT_DIR")}"

echo "Using proto dir: ${PROTO_DIR}"
echo "Using out dir: ${OUT_DIR}"

if [ ! -d "${PROTO_DIR}" ]; then
  echo "Proto directory does not exist: ${PROTO_DIR}"
  exit 1
fi

mkdir -p "${OUT_DIR}"

python -m grpc_tools.protoc \
  -I"${PROTO_DIR}" \
  --python_out="${OUT_DIR}" \
  --grpc_python_out="${OUT_DIR}" \
  --mypy_out="${OUT_DIR}" \
  --mypy_grpc_out="${OUT_DIR}" \
  "${PROTO_DIR}/brain.proto"

sed -i 's/^import brain_pb2 as/from . import brain_pb2 as/' "$OUT_DIR/brain_pb2_grpc.py"
touch ${OUT_DIR}/__init__.py

echo "Generated proto files in ${OUT_DIR}"
