#!/usr/bin/env python3
"""
parse-report.py — Stryker mutation report parser for Plantry

Usage:
  python parse-report.py                    # auto-find newest report under StrykerOutput/
  python parse-report.py <path-to-json>     # use a specific mutation-report.json

Output: Structured markdown consumed by the stryker-report skill.
Exit 0 = success; exit 1 = no report found or parse error.
"""

import io
import json
import os
import sys
import glob
from pathlib import Path
from collections import defaultdict

# Force UTF-8 on Windows where stdout defaults to cp1252
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

STATUSES = ("Killed", "Survived", "NoCoverage", "Timeout", "Ignored", "CompileError")
SCORE_STATUSES = {"Killed", "Survived", "Timeout"}  # denominator


def find_latest_report(root: Path) -> Path | None:
    pattern = str(root / "StrykerOutput" / "*" / "reports" / "mutation-report.json")
    reports = glob.glob(pattern)
    if not reports:
        return None
    # Directory name is a timestamp (YYYY-MM-DD.HH-mm-ss) — lexicographic = chronological
    reports.sort(key=lambda p: Path(p).parent.parent.name, reverse=True)
    return Path(reports[0])


def relative(path: str, root: str) -> str:
    p = path.replace("\\", "/")
    r = root.replace("\\", "/").rstrip("/") + "/"
    return p[len(r):] if p.startswith(r) else p


def is_migration(rel_path: str) -> bool:
    return "/Migrations/" in rel_path or "\\Migrations\\" in rel_path


def score_str(killed: int, total: int) -> str:
    if total == 0:
        return "N/A"
    return f"{killed / total * 100:.0f}%"


def threshold_label(score: float | None, high: int, low: int) -> str:
    if score is None:
        return "N/A"
    if score >= high:
        return f"ABOVE HIGH ({high}%)"
    if score >= low:
        return f"ABOVE LOW ({low}%) — needs work"
    return f"BELOW LOW ({low}%) — FAILING"


STRYKER_PLACEHOLDER = "Stryker was here!"
NOCOV_LINE_LIMIT = 15


def group_mutants(mutants: list[dict]) -> tuple[list[dict], int]:
    """
    Group survived mutants that share the same mutatorName + line.
    Also separates out "Stryker was here!" placeholder string mutations —
    these land on static/const string fields and have no meaningful test to
    write; return them as a count only.
    Returns (grouped_real, placeholder_count).
    """
    placeholder_count = 0
    seen: dict[tuple, dict] = {}
    grouped = []
    for m in mutants:
        if m.get("replacement") == f'"{STRYKER_PLACEHOLDER}"':
            placeholder_count += 1
            continue
        key = (m["mutatorName"], m["location"]["start"]["line"])
        if key in seen:
            seen[key]["_count"] = seen[key].get("_count", 1) + 1
        else:
            m = dict(m)
            m["_count"] = 1
            seen[key] = m
            grouped.append(m)
    return grouped, placeholder_count


def main() -> int:
    root = Path(__file__).parent.parent.parent.parent  # skills/../../../ = code/

    if len(sys.argv) > 1:
        report_path = Path(sys.argv[1])
    else:
        report_path = find_latest_report(root)
        if report_path is None:
            print("ERROR: No mutation-report.json found under StrykerOutput/.")
            print("Run `.\\stryker-run.ps1` or `dotnet stryker --project <Name>` first.")
            return 1

    if not report_path.exists():
        print(f"ERROR: Report not found: {report_path}")
        return 1

    with open(report_path, encoding="utf-8") as f:
        data = json.load(f)

    project_root = data.get("projectRoot", str(root))
    thresholds = data.get("thresholds", {"high": 80, "low": 60})
    high_t, low_t = thresholds["high"], thresholds["low"]

    timestamp = report_path.parent.parent.name  # e.g. 2026-06-10.11-54-53
    rel_report = relative(str(report_path), str(root))

    # ── Aggregate ────────────────────────────────────────────────────────────
    totals: dict[str, int] = {s: 0 for s in STATUSES}
    file_stats: list[dict] = []
    migration_files: list[str] = []
    config_issues: list[str] = []

    for fpath, fdata in data["files"].items():
        rel = relative(fpath, project_root)
        counts = {s: 0 for s in STATUSES}
        for m in fdata["mutants"]:
            counts[m["status"]] = counts.get(m["status"], 0) + 1
            totals[m["status"]] = totals.get(m["status"], 0) + 1

        total_scored = counts["Killed"] + counts["Survived"] + counts["Timeout"]
        raw_score = (counts["Killed"] / total_scored * 100) if total_scored > 0 else None

        entry = {
            "file": rel,
            "path": fpath,
            "source": fdata.get("source", ""),
            "mutants": fdata["mutants"],
            "counts": counts,
            "total_scored": total_scored,
            "raw_score": raw_score,
            "migration": is_migration(rel),
        }
        file_stats.append(entry)
        if is_migration(rel):
            migration_files.append(rel)

    file_stats.sort(key=lambda x: x["counts"]["Survived"], reverse=True)

    global_scored = totals["Killed"] + totals["Survived"] + totals["Timeout"]
    global_score = (totals["Killed"] / global_scored * 100) if global_scored > 0 else None

    # ── Header ───────────────────────────────────────────────────────────────
    print(f"## Mutation Report — {rel_report}  ({timestamp})")
    print()

    score_display = f"{global_score:.1f}%" if global_score is not None else "N/A"
    print(f"**Overall score: {score_display}**  "
          f"(K={totals['Killed']} / S={totals['Survived']} / "
          f"TO={totals['Timeout']} / NC={totals['NoCoverage']} / "
          f"IE={totals['Ignored']} / CE={totals['CompileError']})")
    print(f"**Thresholds:** high={high_t}% / low={low_t}%  "
          f"→  **{threshold_label(global_score, high_t, low_t)}**")
    print()

    # ── File table ───────────────────────────────────────────────────────────
    print("### By file (sorted by survived ↓)")
    print()
    print("| Score | Killed | Survived | NoCov | File |")
    print("|-------|--------|----------|-------|------|")
    for f in file_stats:
        c = f["counts"]
        ss = score_str(c["Killed"], f["total_scored"])
        label = f["file"]
        if f["migration"]:
            label += "  _(migration — skip)_"
        print(f"| {ss:>5} | {c['Killed']:>6} | {c['Survived']:>8} | {c['NoCoverage']:>5} | {label} |")
    print()

    # ── Per-file survived detail ──────────────────────────────────────────────
    actionable = [f for f in file_stats if f["counts"]["Survived"] > 0 and not f["migration"]]
    if actionable:
        print("### Survived mutants by file")
        print()
        for f in actionable:
            survived = [m for m in f["mutants"] if m["status"] == "Survived"]
            grouped, placeholder_count = group_mutants(survived)
            c = f["counts"]
            ss = score_str(c["Killed"], f["total_scored"])
            print(f"#### `{f['file']}`  ({len(survived)} survived, score {ss})")
            print()
            for m in grouped:
                line = m["location"]["start"]["line"]
                col = m["location"]["start"]["column"]
                count_suffix = f" ×{m['_count']}" if m["_count"] > 1 else ""
                print(f"- **L{line}:{col}** `{m['mutatorName']}`{count_suffix}  "
                      f"→ `{m['replacement']}`"
                      + (f"  _(covered by {len(m['coveredBy'])} tests, killed by none)_"
                         if m["coveredBy"] else "  _(no test coverage)_"))
            if placeholder_count:
                print(f"- _{placeholder_count} `String mutation` on static string "
                      f"field(s) — low priority, no behavior to verify_")
            print()

    # ── NoCoverage detail ─────────────────────────────────────────────────────
    no_cov_files = [f for f in file_stats if f["counts"]["NoCoverage"] > 0 and not f["migration"]]
    if no_cov_files:
        print("### Files with uncovered code (NoCoverage > 0)")
        print()
        for f in no_cov_files:
            nc_mutants = [m for m in f["mutants"] if m["status"] == "NoCoverage"]
            count = f["counts"]["NoCoverage"]
            lines = sorted({m["location"]["start"]["line"] for m in nc_mutants})
            if count > NOCOV_LINE_LIMIT:
                print(f"- `{f['file']}` — {count} mutants uncovered "
                      f"(no unit tests reach this file)")
            else:
                line_str = ", ".join(f"L{l}" for l in lines)
                print(f"- `{f['file']}` — {count} mutants uncovered at {line_str}")
        print()

    # ── Config advisories ─────────────────────────────────────────────────────
    advisories: list[str] = []

    if migration_files:
        advisories.append(
            f"**Migration files are being mutated** ({len(migration_files)} files). "
            "Add `!**/Migrations/**` to the `mutate` exclusion list in `stryker-config.json`."
        )

    if totals["CompileError"] > 0:
        advisories.append(
            f"**{totals['CompileError']} CompileError mutants** — Stryker generated invalid code. "
            "Usually a Stryker version or config issue; not actionable as test gaps."
        )

    if totals["Timeout"] > 0:
        to_files = [f["file"] for f in file_stats if f["counts"]["Timeout"] > 0]
        advisories.append(
            f"**{totals['Timeout']} Timeout mutants** in: "
            + ", ".join(f"`{p}`" for p in to_files)
            + ". Check for missing timeout config in `stryker-config.json`."
        )

    if advisories:
        print("### Config advisories")
        print()
        for a in advisories:
            print(f"- {a}")
        print()

    # ── Summary stats for Claude ──────────────────────────────────────────────
    print("---")
    print(f"_Files with survived mutants (actionable): {len(actionable)}_")
    print(f"_Total survived to kill: {totals['Survived']}_")
    if migration_files:
        print(f"_Migration files (skip): {len(migration_files)}_")

    return 0


if __name__ == "__main__":
    sys.exit(main())
