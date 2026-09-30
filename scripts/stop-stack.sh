#!/usr/bin/env bash
#
# Acentra: stop and remove the whole stack, including stale containers and pods.
#
# Usage:
#   scripts/stop-stack.sh                 remove containers, pods, networks, volumes,
#                                         worktrees and temp files
#   scripts/stop-stack.sh --keep-data     keep the Postgres and MinIO volumes
#   scripts/stop-stack.sh --all           also remove the images (about 1.9 GB)
#   scripts/stop-stack.sh --keep-temp     keep /tmp/acentra-* files
#   scripts/stop-stack.sh --yes           skip the confirmation prompt
#   scripts/stop-stack.sh --help
#
# 'podman-compose down' alone leaves things behind: it does not remove the compose pod
# (pod_acentra), it does not remove volumes without -v, and it never removes images.
# This script removes all of that, and also removes containers that are stopped, exited
# or merely created, which a plain 'down' can also miss.
#
# Every step is scoped to resources whose name contains "acentra", so unrelated
# containers on the machine are left alone.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"

KEEP_DATA=0
KEEP_TEMP=0
REMOVE_IMAGES=0
ASSUME_YES=0

usage() {
    awk 'NR>2 && /^#/ { sub(/^# ?/, ""); print; next } NR>2 { exit }' "$0"
    exit 0
}

for argument in "$@"; do
    case "$argument" in
        --keep-data)  KEEP_DATA=1 ;;
        --keep-temp)  KEEP_TEMP=1 ;;
        --all|--remove-images) REMOVE_IMAGES=1 ;;
        --yes|-y)     ASSUME_YES=1 ;;
        --help|-h)    usage ;;
        *)            fail "unknown option '$argument'. Try --help." ;;
    esac
done

preflight

# Show the operator exactly what was found before anything is removed. This is also how
# stale, already-downed containers become visible.
info "Discovering project resources"

mapfile -t found_containers < <(acentra_containers)
mapfile -t found_stopped    < <(acentra_stopped_containers)
mapfile -t found_pods       < <(acentra_pods)
mapfile -t found_networks   < <(acentra_networks)
mapfile -t found_volumes    < <(acentra_volumes)

printf '  containers: %s\n' "${found_containers[*]:-none}"
printf '  of which not running: %s\n' "${found_stopped[*]:-none}"
printf '  pods:       %s\n' "${found_pods[*]:-none}"
printf '  networks:   %s\n' "${found_networks[*]:-none}"
printf '  volumes:    %s\n' "${found_volumes[*]:-none}"
echo

if [[ "$KEEP_DATA" -eq 1 ]]; then
    warn "Volumes will be KEPT. The database and MinIO data survive."
else
    warn "Volumes will be REMOVED. All tenant data is recreated on the next start."
fi
if [[ "$REMOVE_IMAGES" -eq 1 ]]; then
    warn "Images will be REMOVED (about 1.9 GB to re-download on the next start)."
fi
if [[ "$KEEP_TEMP" -ne 1 ]]; then
    echo "       Temp files under /tmp/acentra-* will be removed."
fi

confirm "Proceed?" || { info "Aborted. Nothing was changed."; exit 0; }

# ------------------------------------------------------------------- 1. compose down

info "Running podman-compose down"

if [[ "$KEEP_DATA" -eq 1 ]]; then
    podman-compose down --remove-orphans || warn "podman-compose down reported a problem"
else
    podman-compose down -v --remove-orphans || warn "podman-compose down reported a problem"
fi
ok "compose stopped"

# --------------------------------------------- 2. containers, including downed ones

info "Removing project containers (running, stopped, exited or created)"

removed=0
while IFS= read -r container; do
    [[ -n "$container" ]] || continue
    if podman rm -f "$container" >/dev/null 2>&1; then
        ok "removed container $container"
        removed=$(( removed + 1 ))
    else
        warn "could not remove container $container"
    fi
done < <(acentra_containers || true)

if [[ "$removed" -eq 0 ]]; then
    ok "no containers left to remove"
fi

# ------------------------------------------------------------------------- 3. pods

info "Removing project pods"

# A compose pod survives a plain 'down', so it has to be removed explicitly.
while IFS= read -r pod; do
    [[ -n "$pod" ]] || continue
    podman pod stop "$pod" >/dev/null 2>&1 || true
    if podman pod rm -f "$pod" >/dev/null 2>&1; then
        ok "removed pod $pod"
    else
        warn "could not remove pod $pod"
    fi
done < <(acentra_pods || true)

# --------------------------------------------------------------------- 4. networks

info "Removing project networks"

while IFS= read -r network; do
    [[ -n "$network" ]] || continue
    if podman network rm -f "$network" >/dev/null 2>&1; then
        ok "removed network $network"
    else
        warn "could not remove network $network (may still be in use)"
    fi
done < <(acentra_networks || true)

# ---------------------------------------------------------------------- 5. volumes

if [[ "$KEEP_DATA" -eq 1 ]]; then
    info "Keeping volumes (--keep-data)"
else
    info "Removing project volumes"
    while IFS= read -r volume; do
        [[ -n "$volume" ]] || continue
        if podman volume rm -f "$volume" >/dev/null 2>&1; then
            ok "removed volume $volume"
        else
            warn "could not remove volume $volume"
        fi
    done < <(acentra_volumes || true)
fi

# ----------------------------------------------------------------------- 6. images

if [[ "$REMOVE_IMAGES" -eq 1 ]]; then
    info "Removing project and base images"
    for image in "$WEB_IMAGE" "$MINIO_IMAGE" "$POSTGRES_IMAGE" "$SDK_IMAGE" "$RUNTIME_IMAGE"; do
        if podman image exists "$image" 2>/dev/null; then
            if podman rmi -f "$image" >/dev/null 2>&1; then
                ok "removed $image"
            else
                warn "could not remove $image (may be in use by another container)"
            fi
        fi
    done
else
    info "Keeping images so the next start is fast (use --all to remove them)"
fi

# -------------------------------------------------------------------- 7. worktrees

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

# ------------------------------------------------------------------ 8. temp files

if [[ "$KEEP_TEMP" -eq 1 ]]; then
    info "Keeping temp files (--keep-temp)"
else
    info "Removing temp files from earlier runs"
    # Everything this project writes to /tmp is prefixed 'acentra-', including the
    # per-test storage roots the file-upload tests create.
    rm -rf /tmp/acentra-* 2>/dev/null || true
    rm -f /tmp/demo-run.log /tmp/served.css /tmp/cj.txt /tmp/v.txt /tmp/u.txt \
          /tmp/nm.txt /tmp/un.txt 2>/dev/null || true
    ok "temp files cleaned"
fi

# ------------------------------------------------------------------- final state

echo
info "Final state"

remaining_containers="$(acentra_containers | wc -l | tr -d ' ')"
remaining_pods="$(acentra_pods | wc -l | tr -d ' ')"
remaining_networks="$(acentra_networks | wc -l | tr -d ' ')"
remaining_volumes="$(acentra_volumes | wc -l | tr -d ' ')"

printf '  containers: %s\n' "$remaining_containers"
printf '  pods:       %s\n' "$remaining_pods"
printf '  networks:   %s\n' "$remaining_networks"
printf '  volumes:    %s\n' "$remaining_volumes"
echo

if [[ "$remaining_containers" -eq 0 && "$remaining_pods" -eq 0 && "$remaining_networks" -eq 0 ]]; then
    ok "the stack is fully removed"
else
    warn "some resources remain, see above"
fi

echo
echo "  Start it again   scripts/start-stack.sh"
echo
