"""Sync ingested coverage into Repowise's persisted health metrics.

Why this exists (plantry-5l7o): `repowise coverage add` persists per-file
coverage rows into .repowise/wiki.db with correct repo-relative paths, but
`repowise health` only folds coverage passed via `--coverage <file>` — it
never reads those rows back, so a bare health run persists NULL coverage and
re-fires false `untested_hotspot` criticals. Passing the raw cobertura files
to `--coverage` doesn't work either: coverlet's `filename` attrs are relative
to `<source>` (code/src/), and the `--coverage` path applies no suffix
resolution.

This script bridges the gap: it exports the already-resolved coverage rows
from wiki.db into the repowise-coverage-v1 JSON format (keyed by
repo-relative POSIX path, exactly what `--coverage` expects) and re-runs
`repowise health` with it, which recomputes AND persists coverage-aware
metrics — the same rows MCP `get_health` serves.

Usage (from the repo root, after `repowise coverage add <reports>`):

    python tools/sync-repowise-coverage.py

Drop this script once repowise's `health` command reads ingested coverage
rows on its own.
"""

from __future__ import annotations

import json
import sqlite3
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DB_PATH = REPO_ROOT / ".repowise" / "wiki.db"


def main() -> int:
    if not DB_PATH.exists():
        print(f"error: {DB_PATH} not found — run `repowise init` first", file=sys.stderr)
        return 1

    db = sqlite3.connect(DB_PATH)
    rows = db.execute(
        "select file_path, line_coverage_pct, branch_coverage_pct,"
        " covered_lines_json, total_coverable_lines from coverage_files"
    ).fetchall()
    db.close()

    if not rows:
        print(
            "error: no coverage ingested — run `repowise coverage add <report>` first",
            file=sys.stderr,
        )
        return 1

    files: dict[str, dict] = {}
    for path, line_pct, branch_pct, covered_json, total in rows:
        entry: dict = {"line_coverage_pct": line_pct}
        if branch_pct is not None:
            entry["branch_coverage_pct"] = branch_pct
        if covered_json:
            try:
                entry["covered_lines"] = json.loads(covered_json)
            except ValueError:
                pass
        if total is not None:
            entry["total_coverable_lines"] = total
        files[path] = entry

    payload = {"format": "repowise-coverage-v1", "files": files}

    with tempfile.NamedTemporaryFile(
        "w", suffix=".repowise-coverage.json", delete=False, encoding="utf-8"
    ) as fh:
        json.dump(payload, fh)
        json_path = fh.name

    print(f"exported {len(files)} files from wiki.db -> {json_path}")
    try:
        # Table format with no --file/--module filter is required: that is the
        # only mode in which `repowise health` persists metrics back to wiki.db.
        result = subprocess.run(
            ["repowise", "health", "--repo", "code", "--coverage", json_path],
            cwd=REPO_ROOT,
        )
        return result.returncode
    finally:
        Path(json_path).unlink(missing_ok=True)


if __name__ == "__main__":
    sys.exit(main())
