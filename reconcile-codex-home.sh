#!/usr/bin/env bash
# A renamed/recreated CODEX_HOME leaves Docker bound to the old directory.
# Compare directory identity, never copy rotating OAuth credentials.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
exec 9>"${XDG_RUNTIME_DIR:-/tmp}/ai-mcp-codex-home-${UID}.lock"
flock -n 9 || exit 0
codex_home="${HOME}/.codex"
[[ -d "$codex_home" && -f "$codex_home/auth.json" ]] || exit 0
[[ "$(docker inspect -f '{{.State.Running}}' codex-mcp 2>/dev/null)" == true ]] || exit 0
host_identity=$(stat -c '%d:%i' "$codex_home")
container_identity=$(docker exec codex-mcp stat -c '%d:%i' "$codex_home")
if [[ "$host_identity" != "$container_identity" ]]; then
  echo 'CODEX_HOME was replaced; rebinding Codex service to the current shared directory.'
  ./run-docker.sh --no-deps --force-recreate codex-mcp
fi
