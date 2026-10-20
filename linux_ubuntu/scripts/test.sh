#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
export PYTHONPATH="$project_dir/src${PYTHONPATH:+:$PYTHONPATH}"
export QT_QPA_PLATFORM="${QT_QPA_PLATFORM:-offscreen}"
"$project_dir/.venv/bin/python" -m unittest discover -s "$project_dir/tests" -v
"$project_dir/.venv/bin/python" "$project_dir/main.py" --smoke-test "$project_dir/artifacts/ui-smoke"
