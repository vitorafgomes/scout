#!/usr/bin/env bash
set -euo pipefail

# Publishes Scout.Host.Worker as a self-contained single-file binary for the
# requested RID and places the output under ui/build/worker/<RID>/.
#
# Usage:  ./publish-worker.sh [RID]
# If RID is omitted the script detects it from the current OS/arch.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../../.." && pwd)"
UI_ROOT="${REPO_ROOT}/ui"
CSPROJ="${REPO_ROOT}/src/Scout.Host.Console/Scout.Host.Console.csproj"

detect_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"

  case "${os}" in
    Linux)
      case "${arch}" in
        x86_64)  echo "linux-x64" ;;
        aarch64) echo "linux-arm64" ;;
        *)        echo "linux-x64" ;;
      esac
      ;;
    Darwin)
      case "${arch}" in
        arm64)   echo "osx-arm64" ;;
        x86_64)  echo "osx-x64" ;;
        *)        echo "osx-x64" ;;
      esac
      ;;
    MINGW*|MSYS*|CYGWIN*)
      echo "win-x64"
      ;;
    *)
      echo "linux-x64"
      ;;
  esac
}

SUPPORTED_RIDS=("linux-x64" "osx-arm64" "osx-x64" "win-x64")

RID="${1:-$(detect_rid)}"

valid=0
for r in "${SUPPORTED_RIDS[@]}"; do
  if [[ "${RID}" == "${r}" ]]; then
    valid=1
    break
  fi
done

if [[ "${valid}" -eq 0 ]]; then
  echo "error: unsupported RID '${RID}'. Supported: ${SUPPORTED_RIDS[*]}" >&2
  exit 1
fi

OUTPUT_DIR="${UI_ROOT}/build/worker/${RID}"

# Clean the output dir first so stale artifacts from a previous build (e.g.
# old project pdb/xml) are never bundled into the package.
rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

echo "publishing Scout.Host.Console for ${RID} -> ${OUTPUT_DIR}"

dotnet publish "${CSPROJ}" \
  --configuration Release \
  --runtime "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  --output "${OUTPUT_DIR}"

if [[ "${RID}" == win-x64 ]]; then
  BINARY="${OUTPUT_DIR}/Scout.Host.Console.exe"
else
  BINARY="${OUTPUT_DIR}/Scout.Host.Console"
fi

if [[ ! -f "${BINARY}" ]]; then
  echo "error: expected binary not found at ${BINARY}" >&2
  exit 1
fi

SIZE="$(stat -c%s "${BINARY}" 2>/dev/null || stat -f%z "${BINARY}")"
if [[ "${SIZE}" -eq 0 ]]; then
  echo "error: binary at ${BINARY} has zero size" >&2
  exit 1
fi

if [[ "${RID}" != win-x64 ]]; then
  if [[ ! -x "${BINARY}" ]]; then
    echo "error: binary at ${BINARY} is not executable" >&2
    exit 1
  fi
fi

echo "ok: ${BINARY} (${SIZE} bytes)"
