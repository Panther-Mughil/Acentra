#!/usr/bin/env bash
#
# Shared helpers for the Acentra stack scripts. Sourced, never executed directly.
#
# Everything here is deliberately scoped to resources whose name matches "acentra".
# Other containers on the machine (for example argus-kali) are never touched.

# This file is a sourced library. It intentionally defines configuration and colour
# variables that are consumed by the scripts that source it, so shellcheck cannot see
# those uses when it analyses this file on its own.
# shellcheck disable=SC2034

# ------------------------------------------------------------------- presentation

if [[ -t 1 ]]; then
    C_INFO=$'\033[1;34m'; C_OK=$'\033[1;32m'; C_WARN=$'\033[1;33m'; C_ERR=$'\033[1;31m'; C_OFF=$'\033[0m'
else
    C_INFO=''; C_OK=''; C_WARN=''; C_ERR=''; C_OFF=''
fi

info() { printf '%s==>%s %s\n' "$C_INFO" "$C_OFF" "$*"; }
ok()   { printf '%s  ok%s %s\n' "$C_OK" "$C_OFF" "$*"; }
warn() { printf '%s  !!%s %s\n' "$C_WARN" "$C_OFF" "$*"; }
fail() { printf '%serror:%s %s\n' "$C_ERR" "$C_OFF" "$*" >&2; exit 1; }

# ------------------------------------------------------------------- configuration

readonly PROJECT_MARKER="Acentra.slnx"
readonly SCOPE_TOKEN="acentra"

readonly WEB_IMAGE="localhost/acentra-web:dev"
readonly MINIO_IMAGE="docker.io/pgsty/minio:latest"
readonly POSTGRES_IMAGE="docker.io/library/postgres:17-alpine"
readonly SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0"
readonly RUNTIME_IMAGE="mcr.microsoft.com/dotnet/aspnet:10.0"

readonly WEB_CONTAINER="acentra-web"
readonly POSTGRES_CONTAINER="acentra-postgres"
readonly MINIO_CONTAINER="acentra-minio"

readonly WEB_PORT="${WEB_PORT:-8080}"
readonly HEALTH_TIMEOUT_SECONDS=180
readonly HTTP_TIMEOUT_SECONDS=120

readonly DEMO_EMAIL="demo@acentra.dev"
readonly DEMO_PASSWORD="Demo!2345"

REPO_ROOT=""
COMPOSE_FILE=""
SMOKE_FAILURES=0

# ---------------------------------------------------------------------- discovery

# Names of containers belonging to this project, running, stopped or created.
acentra_containers() {
    podman ps -a --format '{{.Names}}' 2>/dev/null | grep -i "$SCOPE_TOKEN" || true
}

acentra_pods() {
    podman pod ls --format '{{.Name}}' 2>/dev/null | grep -i "$SCOPE_TOKEN" || true
}

acentra_networks() {
    podman network ls --format '{{.Name}}' 2>/dev/null | grep -i "$SCOPE_TOKEN" || true
}

acentra_volumes() {
    podman volume ls --format '{{.Name}}' 2>/dev/null | grep -i "$SCOPE_TOKEN" || true
}

# Containers and pods that exist but are not running. Reported so the operator can
# see that stale, "downed" containers are genuinely being cleaned up.
acentra_stopped_containers() {
    podman ps -a --format '{{.Names}} {{.Status}}' 2>/dev/null \
        | grep -i "$SCOPE_TOKEN" | grep -viE '^[^ ]+ Up ' | awk '{print $1}' || true
}

# ---------------------------------------------------------------------- preflight

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "$1 not found on PATH."
}

preflight() {
    info "Preflight"

    require_command podman
    require_command podman-compose
    require_command curl

    REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
    cd "$REPO_ROOT"

    [[ -f "$PROJECT_MARKER" ]] || fail "$PROJECT_MARKER not found in $REPO_ROOT. Run this from the repository."

    if [[ -f compose.yaml ]]; then
        COMPOSE_FILE="compose.yaml"
    elif [[ -f compose.yml ]]; then
        COMPOSE_FILE="compose.yml"
    else
        fail "no compose.yaml found in $REPO_ROOT."
    fi

    ok "repository: $REPO_ROOT"
    ok "compose file: $COMPOSE_FILE"
    ok "podman: $(podman --version)"
}

confirm() {
    local prompt="$1"
    [[ "${ASSUME_YES:-0}" -eq 1 ]] && return 0

    local reply
    echo
    read -r -p "$prompt [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]]
}

# ------------------------------------------------------------------- smoke testing

probe() {
    # curl prints 000 on a refused connection AND exits non-zero, so never append a
    # fallback here or the result becomes "000000".
    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 15 "http://localhost:${WEB_PORT}$1" 2>/dev/null || true)"
    printf '%s' "${code:-000}"
}

expect() {
    local path="$1" want="$2" actual
    actual="$(probe "$path")"
    if [[ "$actual" == "$want" ]]; then
        ok "$(printf '%-26s %s' "$path" "$actual")"
    else
        warn "$(printf '%-26s %s (expected %s)' "$path" "$actual" "$want")"
        SMOKE_FAILURES=$(( SMOKE_FAILURES + 1 ))
    fi
}

# Wait until the app actually answers HTTP. The container healthcheck only proves the
# TCP port is bound, which happens before Kestrel serves its first request, so without
# this the smoke test races startup and reports false failures.
wait_for_http() {
    local timeout_seconds="${1:-$HTTP_TIMEOUT_SECONDS}" deadline
    deadline=$(( SECONDS + timeout_seconds ))
    while :; do
        if [[ "$(probe /Account/Login)" == 200 ]]; then
            ok "application is answering HTTP"
            return 0
        fi
        if (( SECONDS >= deadline )); then
            warn "application did not answer HTTP within ${timeout_seconds}s"
            return 1
        fi
        sleep 2
    done
}

wait_for_health() {
    local timeout_seconds="${1:-$HEALTH_TIMEOUT_SECONDS}" deadline running unhealthy
    deadline=$(( SECONDS + timeout_seconds ))

    info "Waiting for all three services to report healthy (timeout ${timeout_seconds}s)"
    while :; do
        running="$(podman ps --format '{{.Names}}' 2>/dev/null | grep -ci "$SCOPE_TOKEN" || true)"
        unhealthy="$(podman ps --format '{{.Names}} {{.Status}}' 2>/dev/null \
            | grep -i "$SCOPE_TOKEN" | grep -vc '(healthy)' || true)"

        if [[ "${running:-0}" -ge 3 && "${unhealthy:-0}" -eq 0 ]]; then
            ok "all three services are healthy"
            return 0
        fi

        if (( SECONDS >= deadline )); then
            warn "timed out after ${timeout_seconds}s. Current state:"
            podman ps --format '{{.Names}}  {{.Status}}' 2>/dev/null | grep -i "$SCOPE_TOKEN" || true
            warn "Inspect the logs with: podman logs ${WEB_CONTAINER} --tail 50"
            return 1
        fi
        sleep 3
    done
}

run_smoke_tests() {
    SMOKE_FAILURES=0
    info "Smoke test on port ${WEB_PORT}"

    expect "/" 200
    expect "/Account/Login" 200
    expect "/counter" 200
    expect "/weather" 200
    expect "/favicon.svg" 200
    expect "/favicon.ico" 200

    # Tenant scoped routes must fail closed with no tenant hint.
    expect "/inventory" 302
    expect "/orders" 302
    expect "/suppliers" 302

    # The tenant file route must never be excused by its file extension.
    expect "/files/tenants/00000000000000000000000000000000/documents/note.txt" 302

    # The fingerprinted stylesheet must be SERVED, not challenged. This was a real bug:
    # MapStaticAssets serves fingerprinted names from a virtual route, so an allowlist
    # that only looked for files on disk blocked them.
    local stylesheet actual
    stylesheet="$(curl -s -m 15 "http://localhost:${WEB_PORT}/Account/Login" 2>/dev/null \
        | grep -oE 'app\.[A-Za-z0-9]+\.css' | head -1 || true)"

    if [[ -n "$stylesheet" ]]; then
        actual="$(probe "/$stylesheet")"
        if [[ "$actual" == 200 ]]; then
            ok "$(printf '%-26s %s' "/$stylesheet" "$actual")"
        else
            warn "/$stylesheet returned $actual, expected 200. The UI will render unstyled."
            SMOKE_FAILURES=$(( SMOKE_FAILURES + 1 ))
        fi
    else
        warn "no fingerprinted stylesheet reference found in the login page"
        SMOKE_FAILURES=$(( SMOKE_FAILURES + 1 ))
    fi
}

print_summary() {
    local lan_ip
    lan_ip="$(ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1 || true)"

    echo
    if [[ "$SMOKE_FAILURES" -eq 0 ]]; then
        info "Stack is up and every smoke check passed."
    else
        warn "$SMOKE_FAILURES smoke check(s) failed. See above."
    fi

    echo
    echo "  Application      http://localhost:${WEB_PORT}"
    if [[ -n "$lan_ip" ]]; then
        echo "  From your LAN    http://${lan_ip}:${WEB_PORT}"
    fi
    echo "  Login            ${DEMO_EMAIL}"
    echo "  Password         ${DEMO_PASSWORD}"
    echo "  Seeded tenants   acme, globex"
    echo
    echo "  MinIO console    http://localhost:9001   (acentra / acentra_dev_password)"
    echo "  Postgres         localhost:5432          (acentra / acentra_dev_password)"
    echo
    echo "  Logs             podman logs ${WEB_CONTAINER} --tail 50"
    echo "  Stop             scripts/stop-stack.sh"
    echo "  Stop and wipe    scripts/stop-stack.sh --all"
    echo
}
