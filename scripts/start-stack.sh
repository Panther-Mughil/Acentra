#!/usr/bin/env bash
#
# Acentra: build and start the whole stack, Postgres and MinIO included.
#
# Usage:
#   scripts/start-stack.sh                 build the app image, start all services,
#                                          wait for health, then smoke test
#   scripts/start-stack.sh --no-build      reuse the existing image (faster)
#   scripts/start-stack.sh --help
#
# Brings up three services:
#   acentra-postgres   the control plane and every tenant database
#   acentra-minio      S3 compatible object storage for tenant files
#   acentra-web        the application itself, on port 8080
#
# On first start the app seeds tenants acme and globex and provisions their databases,
# so there is nothing else to do before logging in.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"

BUILD=1

usage() {
    awk 'NR>2 && /^#/ { sub(/^# ?/, ""); print; next } NR>2 { exit }' "$0"
    exit 0
}

for argument in "$@"; do
    case "$argument" in
        --no-build) BUILD=0 ;;
        --help|-h)  usage ;;
        *)          fail "unknown option '$argument'. Try --help." ;;
    esac
done

preflight

info "Starting the stack"
if [[ "$BUILD" -eq 1 ]]; then
    # --force-recreate matters: 'up --build' alone can rebuild the image and still leave
    # the old container running, which silently serves stale code.
    podman-compose up -d --build --force-recreate
else
    podman-compose up -d --force-recreate
fi

wait_for_health "$HEALTH_TIMEOUT_SECONDS" || exit 1

info "Waiting for the application to answer HTTP (timeout ${HTTP_TIMEOUT_SECONDS}s)"
wait_for_http "$HTTP_TIMEOUT_SECONDS" || true

run_smoke_tests
print_summary

[[ "$SMOKE_FAILURES" -eq 0 ]] || exit 1
