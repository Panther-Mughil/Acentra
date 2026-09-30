#!/usr/bin/env bash
#
# Acentra full stack reset.
#
# Tears the whole local stack down, cleans up what the stack left on the host,
# then rebuilds the application image and starts everything again.
#
# Usage:
#   scripts/reset-stack.sh                 full reset: containers, volumes, images,
#                                          leftover worktrees and temp files, then up
#   scripts/reset-stack.sh --keep-data     keep the Postgres and MinIO volumes
#   scripts/reset-stack.sh --keep-images   keep downloaded base images (faster restart)
#   scripts/reset-stack.sh --down-only     tear down and clean, do not start again
#   scripts/reset-stack.sh --verify-only   change nothing; just smoke test what is running
#   scripts/reset-stack.sh --yes           skip the confirmation prompt
#   scripts/reset-stack.sh --help
#
# Nothing outside this project is touched. In particular this never runs
# 'podman system prune', so unrelated containers, images and volumes on the
# machine are left alone.

set -euo pipefail

# ---------------------------------------------------------------- configuration

readonly PROJECT_MARKER="Acentra.slnx"
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
readonly SERVICE_NAMES_REGEX="^(acentra-web|acentra-postgres|acentra-minio)$"

readonly DEMO_EMAIL="demo@acentra.dev"
readonly DEMO_PASSWORD="Demo!2345"

KEEP_DATA=0
KEEP_IMAGES=0
DOWN_ONLY=0
VERIFY_ONLY=0
ASSUME_YES=0

# ---------------------------------------------------------------------- helpers

if [[ -t 1 ]]; then
    C_INFO=$'\033[1;34m'; C_OK=$'\033[1;32m'; C_WARN=$'\033[1;33m'; C_ERR=$'\033[1;31m'; C_OFF=$'\033[0m'
else
    C_INFO=''; C_OK=''; C_WARN=''; C_ERR=''; C_OFF=''
fi

info() { printf '%s==>%s %s\n' "$C_INFO" "$C_OFF" "$*"; }
ok()   { printf '%s  ok%s %s\n' "$C_OK" "$C_OFF" "$*"; }
warn() { printf '%s  !!%s %s\n' "$C_WARN" "$C_OFF" "$*"; }
fail() { printf '%serror:%s %s\n' "$C_ERR" "$C_OFF" "$*" >&2; exit 1; }

usage() {
    awk 'NR>2 && /^#/ { sub(/^# ?/, ""); print; next } NR>2 { exit }' "$0"
    exit 0
}

# Remove one container if it exists. Never fails the script.
remove_container_if_present() {
    local container="$1"
    if podman container exists "$container" 2>/dev/null; then
        if podman rm -f "$container" >/dev/null 2>&1; then
            ok "removed stray container $container"
        else
            warn "could not remove container $container"
        fi
    fi
}

# ---------------------------------------------------------------- smoke testing

SMOKE_FAILURES=0

probe() {
    # curl prints 000 on a refused connection AND exits non-zero, so never append a
    # fallback here: doing that produced "000000". Return whatever curl said, or
    # 000 only when curl produced no output at all.
    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 15 "http://localhost:${WEB_PORT}$1" 2>/dev/null || true)"
    printf '%s' "${code:-000}"
}

# Wait until the app actually answers HTTP. The container healthcheck only proves the
# TCP port is bound, which happens before Kestrel serves its first request, so without
# this the smoke test races startup and reports false failures.
wait_for_http() {
    local timeout_seconds="$1" deadline
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

run_smoke_tests() {
    info "Smoke test on port ${WEB_PORT}"

    # Public routes, reachable with no tenant.
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

    # The fingerprinted stylesheet must be SERVED, not challenged. This was a real
    # bug: MapStaticAssets serves fingerprinted names from a virtual route, so an
    # allowlist that only looked for files on disk blocked them and the browser
    # fell back to a stale cached stylesheet.
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
    echo "  Stop             podman-compose down"
    echo "  Stop and wipe    podman-compose down -v"
    echo
}

# ------------------------------------------------------------------- arguments

for argument in "$@"; do
    case "$argument" in
        --keep-data)   KEEP_DATA=1 ;;
        --keep-images) KEEP_IMAGES=1 ;;
        --down-only)   DOWN_ONLY=1 ;;
        --verify-only) VERIFY_ONLY=1 ;;
        --yes|-y)      ASSUME_YES=1 ;;
        --help|-h)     usage ;;
        *)             fail "unknown option '$argument'. Try --help." ;;
    esac
done

# ------------------------------------------------------------------- preflight

info "Preflight"

command -v podman >/dev/null 2>&1 || fail "podman not found on PATH."
command -v podman-compose >/dev/null 2>&1 || fail "podman-compose not found on PATH."
command -v curl >/dev/null 2>&1 || fail "curl not found on PATH."

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
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

# ---------------------------------------------------------- verify only shortcut

if [[ "$VERIFY_ONLY" -eq 1 ]]; then
    info "Verify only: nothing will be changed."
    run_smoke_tests
    print_summary
    [[ "$SMOKE_FAILURES" -eq 0 ]] || exit 1
    exit 0
fi

# --------------------------------------------------------------- confirmation

if [[ "$ASSUME_YES" -ne 1 ]]; then
    echo
    if [[ "$KEEP_DATA" -eq 1 ]]; then
        warn "This removes the containers and the built image, but KEEPS the database and MinIO volumes."
    else
        warn "This DELETES the database and MinIO volumes. All tenant data is recreated on the next start."
    fi
    echo "       It also removes leftover git worktrees and temp files from previous runs."
    echo
    read -r -p "Proceed? [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]] || { info "Aborted. Nothing was changed."; exit 0; }
fi

# -------------------------------------------------------------------- teardown

info "Stopping and removing containers"

if [[ "$KEEP_DATA" -eq 1 ]]; then
    podman-compose down --remove-orphans || warn "podman-compose down reported a problem"
    ok "containers and network removed, volumes kept"
else
    podman-compose down -v --remove-orphans || warn "podman-compose down reported a problem"
    ok "containers, network and volumes removed"
fi

# Belt and braces: a named container can survive a failed compose run.
remove_container_if_present "$WEB_CONTAINER"
remove_container_if_present "$POSTGRES_CONTAINER"
remove_container_if_present "$MINIO_CONTAINER"

if [[ "$KEEP_DATA" -ne 1 ]]; then
    for volume in acentra_acentra-pgdata acentra_acentra-minio-data; do
        if podman volume exists "$volume" 2>/dev/null; then
            if podman volume rm -f "$volume" >/dev/null 2>&1; then
                ok "removed volume $volume"
            else
                warn "could not remove volume $volume"
            fi
        fi
    done
fi

if [[ "$KEEP_IMAGES" -ne 1 ]]; then
    info "Removing project and base images"
    for image in "$WEB_IMAGE" "$MINIO_IMAGE" "$POSTGRES_IMAGE" "$SDK_IMAGE" "$RUNTIME_IMAGE"; do
        if podman image exists "$image" 2>/dev/null; then
            if podman rmi -f "$image" >/dev/null 2>&1; then
                ok "removed $image"
            else
                warn "could not remove $image (may be in use)"
            fi
        fi
    done
else
    info "Keeping images (--keep-images)"
fi

info "Removing leftover git worktrees from earlier runs"

while IFS= read -r worktree_path; do
    case "$worktree_path" in
        /tmp/acentra-*)
            if git worktree remove --force "$worktree_path" >/dev/null 2>&1; then
                ok "removed worktree $worktree_path"
            else
                warn "could not remove worktree $worktree_path"
            fi
            ;;
    esac
done < <(git worktree list --porcelain | awk '/^worktree /{print $2}' || true)

git worktree prune || true
ok "worktree metadata pruned"

info "Removing temp files from earlier runs"
# Everything this project writes to /tmp is prefixed 'acentra-', so remove the lot. An
# earlier version listed specific names and missed the per-test temp storage roots
# (/tmp/acentra-req005-*) that the file-upload tests create.
rm -rf /tmp/acentra-* 2>/dev/null || true
rm -f /tmp/demo-run.log /tmp/served.css /tmp/cj.txt /tmp/v.txt /tmp/u.txt \
      /tmp/nm.txt /tmp/un.txt 2>/dev/null || true
ok "temp files cleaned"

if [[ "$DOWN_ONLY" -eq 1 ]]; then
    echo
    info "Down only requested. Finished. Nothing was started."
    exit 0
fi

# --------------------------------------------------------------------- startup

info "Building the application image and starting the stack"
podman-compose up -d --build --force-recreate

info "Waiting for all services to report healthy (timeout ${HEALTH_TIMEOUT_SECONDS}s)"

deadline=$(( SECONDS + HEALTH_TIMEOUT_SECONDS ))
while :; do
    running="$(podman ps --format '{{.Names}}' 2>/dev/null | grep -cE "$SERVICE_NAMES_REGEX" || true)"
    unhealthy="$(podman ps --format '{{.Names}} {{.Status}}' 2>/dev/null \
        | grep -E "$SERVICE_NAMES_REGEX" | grep -vc '(healthy)' || true)"

    if [[ "${running:-0}" -eq 3 && "${unhealthy:-0}" -eq 0 ]]; then
        ok "all three services are healthy"
        break
    fi

    if (( SECONDS >= deadline )); then
        warn "timed out after ${HEALTH_TIMEOUT_SECONDS}s waiting for health. Current state:"
        podman ps --format '{{.Names}}  {{.Status}}' 2>/dev/null | grep acentra || true
        warn "Inspect the logs with: podman logs ${WEB_CONTAINER} --tail 50"
        exit 1
    fi

    sleep 3
done

# ------------------------------------------------------------------ final report

info "Waiting for the application to answer HTTP (timeout 120s)"
wait_for_http 120 || true

run_smoke_tests
print_summary

[[ "$SMOKE_FAILURES" -eq 0 ]] || exit 1
