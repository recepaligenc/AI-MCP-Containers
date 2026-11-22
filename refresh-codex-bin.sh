#!/usr/bin/env bash
# Keep Codex service's codex binary alive across VSCode ChatGPT extension updates.
#
# THE PROBLEM: the ChatGPT extension auto-updates into a NEW version folder
# (openai.chatgpt-<ver>-linux-x64/bin/linux-x86_64) and VSCode deletes the old one.
# If the bridge mounts that volatile path directly, every extension update yanks the
# binary out from under the running container → "No provider available" / crashes.
#
# THE FIX (root cause, not a patch): the bridge mounts a STABLE COPY at $STABLE
# (~/.codex-ext-bin), NOT a symlink into the extensions dir. This script syncs the
# newest extension bin INTO that stable copy:
#   - The running container is decoupled from the extensions dir — deleting an old
#     extension version cannot break it; it always holds a complete, valid copy.
#   - rsync writes a temp file then renames (never opens the in-use binary for write),
#     so a sync can't hit ETXTBSY and can't corrupt an in-flight codex run. The bridge
#     execs `codex` fresh per call, so new calls pick up the new binary with NO restart.
#   - A one-time container recreate happens only when first converting symlink->dir (to
#     repoint the live mount). Steady-state syncs deliberately leave the service running.
#
# Run it however you like; for hands-off operation wire the systemd user units in
# systemd/ (a .path watching the extensions dir + a .timer backstop) — see systemd/README.
#
# Usage: ./refresh-codex-bin.sh            # sync newest bin into the stable copy
#        ./refresh-codex-bin.sh --check    # show resolved paths/versions, change nothing
#        ./refresh-codex-bin.sh --recreate # sync AND force-recreate the container
set -euo pipefail

COMPOSE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AI_MCP_PATH_VALUES="$(node "$COMPOSE_DIR/scripts/host-paths.mjs" --codex)"
mapfile -t AI_MCP_SELECTED_PATHS <<< "$AI_MCP_PATH_VALUES"
EXT_DIR="${AI_MCP_SELECTED_PATHS[0]}"
STABLE="${AI_MCP_SELECTED_PATHS[1]}"

# Pick the highest numeric extension version. Archive timestamps can be older
# than an earlier release and must not determine which executable is current.
newest_bin() {
  local codex extension version candidate
  local -a candidates=()
  shopt -s nullglob
  for codex in "${EXT_DIR}"/openai.chatgpt-*-linux-x64/bin/linux-x86_64/codex; do
    [[ -x "$codex" ]] || continue
    extension="$(basename "$(dirname "$(dirname "$(dirname "$codex")")")")"
    version="${extension#openai.chatgpt-}"
    version="${version%-linux-x64}"
    [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || continue
    candidates+=("$codex")
  done
  ((${#candidates[@]})) || return 0
  # The common directory prefix and suffix are identical for every candidate.
  candidate="$(printf '%s\n' "${candidates[@]}" | LC_ALL=C sort -Vr | sed -n '1p')"
  dirname "$candidate"
}

TARGET="$(newest_bin)"
if [[ -z "$TARGET" ]]; then
  echo "ERROR: no openai.chatgpt-*/bin/linux-x86_64/codex found under ${EXT_DIR}" >&2
  exit 1
fi

stable_kind() { [[ -L "$STABLE" ]] && echo symlink || { [[ -d "$STABLE" ]] && echo dir || echo missing; }; }

if [[ "${1:-}" == "--check" ]]; then
  echo "newest ext bin : $TARGET"
  echo "stable copy    : $STABLE ($(stable_kind))"
  echo "stable codex   : $([[ -x "$STABLE/codex" ]] && stat -c '%y' "$STABLE/codex" || echo '(none)')"
  echo "target codex   : $(stat -c '%y' "$TARGET/codex")"
  exit 0
fi

FORCE_RECREATE=0
[[ "${1:-}" == "--recreate" ]] && FORCE_RECREATE=1
node "$COMPOSE_DIR/scripts/codex-destination.mjs" "$TARGET" "$STABLE"
MARKER=.ai-mcp-managed-codex

CONVERTED=0
if [[ -L "$STABLE" || ! -d "$STABLE" ]]; then
  # One-time conversion: replace the legacy symlink (or absent path) with a real copy.
  echo "converting $STABLE -> stable copy (decoupling the service from the extensions dir)..."
  mkdir -p "$(dirname "$STABLE")"
  AI_MCP_NEW_COPY="$(mktemp -d "${STABLE}.tmp.XXXXXX")"
  trap 'rm -rf -- "$AI_MCP_NEW_COPY"' EXIT
  rsync -a --delete "$TARGET"/ "$AI_MCP_NEW_COPY"/
  printf '%s\n' ai-mcp-containers-managed-codex-v1 > "$AI_MCP_NEW_COPY/$MARKER"
  [[ ! -L "$STABLE" ]] || rm -f -- "$STABLE"
  mv "$AI_MCP_NEW_COPY" "$STABLE"
  trap - EXIT
  CONVERTED=1
  echo "stable copy created: $STABLE"
elif [[ -x "$STABLE/codex" ]] && diff -qr --exclude="$MARKER" "$TARGET" "$STABLE" >/dev/null; then
  printf '%s\n' ai-mcp-containers-managed-codex-v1 > "$STABLE/$MARKER"
  echo "codex bin already current ($TARGET) — nothing to do."
else
  # Steady-state: rename-safe in-place sync. No container restart; new calls adopt it.
  echo "syncing newest codex bin into $STABLE ..."
  rsync -a --checksum --delete --exclude="$MARKER" "$TARGET"/ "$STABLE"/
  printf '%s\n' ai-mcp-containers-managed-codex-v1 > "$STABLE/$MARKER"
  echo "synced: $STABLE now matches $TARGET"
fi

if [[ "$FORCE_RECREATE" == 1 ]] || { [[ "$CONVERTED" == 1 ]] && [[ "$(docker inspect -f '{{.State.Running}}' codex-mcp 2>/dev/null)" == true ]]; }; then
  echo "recreating Codex service so the live mount points at the stable copy..."
  (cd "$COMPOSE_DIR" && ./run-docker.sh --no-deps --force-recreate codex-mcp)
fi
echo "done."
