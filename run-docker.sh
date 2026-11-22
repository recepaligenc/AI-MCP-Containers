#!/usr/bin/env bash
# Start the two global provider services after validating security configuration.
# Never copy auth.json: independently refreshed copies invalidate each other.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
node scripts/check-config.mjs
exec node scripts/compose-options.mjs up -d "$@"
