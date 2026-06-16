#!/usr/bin/env python3
"""Triage data-prep — the DETERMINISTIC half of the triage skill.

Gathers and groups the beads backlog so the agent doesn't re-parse a 60KB+ JSON
dump with throwaway code every run. This script does NO judgment: it does not
rank, write rationales, judge quick-wins, or propose priorities. Those stay with
the agent (see SKILL.md steps 3-7).

What it does (skill steps 1-2 + tally for step 5):
  - GATE: counts open issues with no `class:` label (untriaged). Exit 2 if any.
  - Joins `bd list --status=open` + `bd ready` + `bd blocked` into one row each.
  - Splits into the DO-NOW pool (class:bug + class:ux) and the PARKED pool
    (class:improvement + class:tech-debt), grouped by `theme:`.
  - Tallies the leak budget.

Usage:
  python prep.py          # compact text rendering (default; feed to the agent)
  python prep.py --json    # structured JSON for programmatic use

Requires the `bd` CLI on PATH. Python 3.8+. No third-party deps.
"""
import json
import subprocess
import sys

LEAK_CLASSES = {"class:bug", "class:ux"}
PARKED_CLASSES = {"class:improvement", "class:tech-debt"}


def bd_json(*args):
    """Run a bd command with --json and return the parsed array."""
    try:
        out = subprocess.run(
            ["bd", *args, "--json"],
            capture_output=True, text=True, check=True,
        ).stdout
    except FileNotFoundError:
        sys.exit("error: `bd` CLI not found on PATH")
    except subprocess.CalledProcessError as e:
        sys.exit(f"error: `bd {' '.join(args)}` failed:\n{e.stderr}")
    return json.loads(out or "[]")


def short_class(labels):
    for l in labels:
        if l.startswith("class:"):
            return l.split(":", 1)[1]
    return None


def theme(labels):
    for l in labels:
        if l.startswith("theme:"):
            return l.split(":", 1)[1]
    return "(no theme)"


def build():
    issues = bd_json("list", "--status=open")
    ready_ids = {i["id"] for i in bd_json("ready")}
    blocked = {i["id"]: i for i in bd_json("blocked")}

    untriaged = [i for i in issues if not short_class(i.get("labels") or [])]

    rows = []
    for i in issues:
        labels = i.get("labels") or []
        cls = short_class(labels)
        if not cls:
            continue
        rows.append({
            "id": i["id"],
            "title": i["title"],
            "cls": cls,
            "theme": theme(labels),
            "priority": i.get("priority"),
            "ready": i["id"] in ready_ids,
            "blocked_by": blocked.get(i["id"], {}).get("blocked_by") or [],
            "quick_win": "quick-win" in labels,
            "needs_spec": "needs-spec" in labels,
            "needs_split": "needs-split" in labels,
        })

    leaks = [r for r in rows if f"class:{r['cls']}" in LEAK_CLASSES]
    parked = [r for r in rows if f"class:{r['cls']}" in PARKED_CLASSES]
    other = [r for r in rows
             if r not in leaks and r not in parked]  # unexpected class values

    budget = {
        "open_leaks": len(leaks),
        "bugs": sum(1 for r in leaks if r["cls"] == "bug"),
        "ux": sum(1 for r in leaks if r["cls"] == "ux"),
        "improvements": sum(1 for r in parked if r["cls"] == "improvement"),
        "tech_debt": sum(1 for r in parked if r["cls"] == "tech-debt"),
    }

    return {
        "total_open": len(issues),
        "untriaged": [{"id": u["id"], "title": u["title"]} for u in untriaged],
        "budget": budget,
        "leaks": leaks,
        "parked": parked,
        "other": other,
    }


def group_by_theme(rows):
    g = {}
    for r in rows:
        g.setdefault(r["theme"], []).append(r)
    return dict(sorted(g.items(), key=lambda kv: (-len(kv[1]), kv[0])))


def flags(r):
    out = []
    if r["ready"]:
        out.append("ready")
    if r["blocked_by"]:
        out.append("blocked by " + ",".join(r["blocked_by"]))
    if r["quick_win"]:
        out.append("quick-win")
    if r["needs_spec"]:
        out.append("[!] needs-spec (define first)")
    if r["needs_split"]:
        out.append("[!] needs-split (split first)")
    return " | ".join(out)


def render_text(data):
    L = []
    u = data["untriaged"]
    if u:
        L.append(f"GATE: FAIL {len(u)} of {data['total_open']} open issues UNTRIAGED "
                 "-- run `groom` first, do NOT report:")
        for x in u:
            L.append(f"  - {x['id']}  {x['title'][:72]}")
        L.append("")
    else:
        L.append(f"GATE: OK all {data['total_open']} open issues carry a class: label")
        L.append("")

    b = data["budget"]
    L.append(f"LEAK BUDGET: {b['open_leaks']} open "
             f"({b['bugs']} bugs / {b['ux']} ux) -- drive to 0 for MVP")
    L.append(f"PARKED (on purpose): {b['improvements']} improvements / "
             f"{b['tech_debt']} tech-debt")
    L.append("")

    L.append("--- DO-NOW POOL (bugs + ux), grouped by theme -- RANK THESE YOURSELF ---")
    for th, rows in group_by_theme(data["leaks"]).items():
        L.append(f"theme:{th}  ({len(rows)} leaks)")
        for r in rows:
            f = flags(r)
            L.append(f"  {r['id']} [{r['cls']}] P{r['priority']} {r['title'][:64]}"
                     + (f"   | {f}" if f else ""))
    if not data["leaks"]:
        L.append("  (none — leak budget is zero)")
    L.append("")

    L.append("--- PARKED POOL (improvement + tech-debt), grouped by theme ---")
    for th, rows in group_by_theme(data["parked"]).items():
        L.append(f"theme:{th}")
        for r in rows:
            f = flags(r)
            L.append(f"  {r['id']} [{r['cls']}] P{r['priority']} {r['title'][:64]}"
                     + (f"   | {f}" if f else ""))
    if not data["parked"]:
        L.append("  (none)")

    if data["other"]:
        L.append("")
        L.append("--- UNEXPECTED class: values (neither leak nor parked) ---")
        for r in data["other"]:
            L.append(f"  {r['id']} [class:{r['cls']}] {r['title'][:64]}")

    return "\n".join(L)


def main():
    data = build()
    if "--json" in sys.argv[1:]:
        print(json.dumps(data, indent=2))
    else:
        print(render_text(data))
    sys.exit(2 if data["untriaged"] else 0)


if __name__ == "__main__":
    main()
