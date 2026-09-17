#!/usr/bin/env bash
#
# contract-001 · G-10 — no credential in the repository.
#
# Runs in CI on every push and locally with the same command. Two passes:
#
#   1. Pattern scan over tracked files, for the shapes a secret takes regardless of where
#      it was pasted: private key blocks, cloud access keys, bearer tokens, connection
#      strings carrying a password.
#   2. Configuration scan over every appsettings*.json, for credential keys that hold a
#      value. Development secrets belong in user-secrets; deployed secrets come from the
#      environment. A value committed here is a value in everyone's clone and in history.
#
# Exit 0 = clean. Exit 1 = a finding, printed with its file and line.

set -uo pipefail

cd "$(dirname "$0")/.." || exit 1

findings=0

report() {
  findings=$((findings + 1))
  printf '  %s\n' "$1"
}

# Tracked files only: what is in the repository is what matters, and this keeps the scan
# off build output and local scratch files.
mapfile -t tracked < <(git ls-files)

# ── Pass 1: secret shapes ──────────────────────────────────────────────────────
# Each entry is "description|extended regex".
patterns=(
  'private key block|-----BEGIN [A-Z ]*PRIVATE KEY-----'
  'AWS access key id|AKIA[0-9A-Z]{16}'
  'GitHub token|gh[pousr]_[A-Za-z0-9]{36,}'
  'Slack token|xox[baprs]-[A-Za-z0-9-]{10,}'
  'Google API key|AIza[0-9A-Za-z_-]{35}'
  'JSON Web Token|eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
  'connection string password|(Password|Pwd)=[^;\"'"'"'[:space:]]+'
  'Azure storage key|DefaultEndpointsProtocol=.*AccountKey=[A-Za-z0-9+/=]{20,}'
)

echo "Secret scan: ${#tracked[@]} tracked files"

for entry in "${patterns[@]}"; do
  description="${entry%%|*}"
  regex="${entry#*|}"
  # This script names the patterns it looks for, so it would match itself.
  while IFS= read -r hit; do
    [ -z "$hit" ] && continue
    case "$hit" in
      scripts/secret-scan.sh:*) continue ;;
    esac
    report "$description -> $hit"
  done < <(grep -nIE "$regex" -- "${tracked[@]}" 2>/dev/null | cut -c1-200)
done

# ── Pass 2: credential keys holding a value in configuration ───────────────────
config_files=()
for f in "${tracked[@]}"; do
  case "$f" in
    *appsettings*.json) config_files+=("$f") ;;
  esac
done

echo "Configuration scan: ${#config_files[@]} appsettings files"

for f in "${config_files[@]}"; do
  # "Key": "something" where something is not empty. Matches ApiKey, ClientSecret,
  # Password, Token and anything ending in Key/Secret/Password/Token.
  while IFS= read -r hit; do
    [ -z "$hit" ] && continue
    report "credential with a committed value -> $f:$hit"
  done < <(grep -nIE '"[A-Za-z]*(ApiKey|Secret|Password|Token|ClientSecret)"[[:space:]]*:[[:space:]]*"[^"]+"' "$f" | cut -c1-200)
done

echo
if [ "$findings" -gt 0 ]; then
  echo "FAIL: $findings finding(s). Move the value to user-secrets (development) or the"
  echo "environment (deployed), and rotate it — a committed secret is in the history too."
  exit 1
fi

echo "PASS: no credential found in tracked files."
