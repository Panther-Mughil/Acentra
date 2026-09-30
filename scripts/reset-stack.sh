#!/usr/bin/env bash
#
# Acentra: full reset. Remove everything, then start it again.
#
# Usage:
#   scripts/reset-stack.sh                 full clean then start
#   scripts/reset-stack.sh --keep-data     keep the Postgres and MinIO volumes
#   scripts/reset-stack.sh --keep-temp     keep /tmp/acentra-* files
#   scripts/reset-stack.sh --no-build      reuse the existing image on restart
#   scripts/reset-stack.sh --yes           skip the confirmation prompt
#   scripts/reset-stack.sh --help
#
# This is a thin wrapper around two scripts you can also run on their own:
#
#   scripts/stop-stack.sh    stop and remove containers, pods, networks, volumes,
#                            worktrees and temp files
#   scripts/start-stack.sh   build and start everything, then smoke test
#
# Use those directly when you only want one half. Use this when you want a clean
# rebuild from scratch.
#
# By default the volumes are removed, so the database and MinIO data are wiped and
# recreated on the next start. Pass --keep-data to preserve them.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

usage() {
    awk 'NR>2 && /^#/ { sub(/^# ?/, ""); print; next } NR>2 { exit }' "$0"
    exit 0
}

stop_arguments=()
start_arguments=()

for argument in "$@"; do
    case "$argument" in
        --keep-data) stop_arguments+=("--keep-data") ;;
        --keep-temp) stop_arguments+=("--keep-temp") ;;
        --no-build)  start_arguments+=("--no-build") ;;
        --yes|-y)    stop_arguments+=("--yes") ;;
        --help|-h)   usage ;;
        *)           printf 'error: unknown option %s. Try --help.\n' "$argument" >&2; exit 1 ;;
    esac
done

# Stop first. If the operator cancels the confirmation, do not start anything.
"$SCRIPT_DIR/stop-stack.sh" ${stop_arguments[@]+"${stop_arguments[@]}"}
"$SCRIPT_DIR/start-stack.sh" ${start_arguments[@]+"${start_arguments[@]}"}
