#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ ! -x "$project_dir/.venv/bin/python" ]]; then
  printf 'Create the environment first: bash "%s/scripts/setup.sh"\n' "$project_dir" >&2
  exit 1
fi
exec "$project_dir/.venv/bin/python" "$project_dir/main.py" "$@"
