#!/usr/bin/env bash
# Fails when a NuGet package (direct or transitive) has a High/Critical advisory that is not in
# .github/vulnerability-allowlist.txt (one GHSA id per line, with the reason and a review date).
set -euo pipefail
cd "$(dirname "$0")/../.."
ALLOW=.github/vulnerability-allowlist.txt
out="$(dotnet list package --vulnerable --include-transitive 2>&1)"
echo "$out"
bad="$(echo "$out" | grep -E '\b(High|Critical)\b' | grep -oE 'GHSA-[0-9a-z-]+' | sort -u \
  | grep -vxF -f <(grep -oE '^GHSA-[0-9a-z-]+' "$ALLOW" 2>/dev/null || true) || true)"
if [[ -n "$bad" ]]; then
  echo "::error::High/Critical NuGet advisories: $bad (update the package or add a reasoned entry to $ALLOW)"
  exit 1
fi
