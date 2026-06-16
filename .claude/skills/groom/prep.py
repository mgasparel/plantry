#!/usr/bin/env python3
"""Groom data-prep -- the DETERMINISTIC half of the groom skill.

Produces everything the agent needs to START classifying, without doing any
classification itself. Judgment (class:/theme:/flags per issue, rationales)
stays with the agent (see SKILL.md).

What it does:
  - Lists all open issues, filters to those with no `class:` label (untriaged).
  - Surveys existing theme: labels so the agent reuses vocabulary before minting.
  - Flags any unexpected zero-count labels worth cleaning up.
  - Prints a compact row per untriaged issue (id, type, current labels, title).
  - Exits 0 when untriaged>0 (there is work to do); exits 1 when all clean.

Requires the `bd` CLI on PATH. Python 3.8+. No third-party deps.
"""
import json
import subprocess
import sys

# Provenance labels -- informational, never remove or surface as actionable.
PROVENANCE = {"code-review", "needs-human"}

# Labels that are never worth flagging as zero-count noise.
SYSTEM_LABELS = {"needs-spec", "needs-split", "quick-win"}


def bd_json(*args):
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


def has_class(labels):
    return any(l.startswith("class:") for l in (labels or []))


def build():
    issues = bd_json("list", "--status=open")
    all_labels = bd_json("label", "list-all")

    label_counts = {entry["label"]: entry["count"] for entry in all_labels}

    untriaged = [i for i in issues if not has_class(i.get("labels") or [])]

    existing_themes = sorted(l for l in label_counts if l.startswith("theme:"))

    # Zero-count labels outside the known-good sets -- worth a cleanup mention.
    stale = [
        l for l, c in label_counts.items()
        if c == 0
        and not l.startswith("class:")
        and not l.startswith("theme:")
        and l not in PROVENANCE
        and l not in SYSTEM_LABELS
    ]

    rows = []
    for i in untriaged:
        labels = i.get("labels") or []
        actionable = [l for l in labels if l not in PROVENANCE]
        rows.append({
            "id":     i["id"],
            "type":   i.get("issue_type", "?"),
            "labels": actionable,
            "title":  i["title"],
        })

    return {
        "total_open":      len(issues),
        "untriaged_count": len(untriaged),
        "existing_themes": existing_themes,
        "stale_labels":    stale,
        "untriaged_rows":  rows,
    }


def render_text(data):
    L = []
    n = data["untriaged_count"]
    t = data["total_open"]

    if n == 0:
        L.append(f"GATE: CLEAN -- all {t} open issues are triaged. Nothing to groom.")
        return "\n".join(L)

    L.append(f"GATE: {n} of {t} open issues UNTRIAGED -- classify these")
    L.append("")

    L.append("EXISTING theme: LABELS (reuse before minting new ones)")
    for th in data["existing_themes"]:
        L.append(f"  {th}")
    L.append("")

    if data["stale_labels"]:
        L.append("STALE LABELS (zero open issues -- consider deleting)")
        for l in sorted(data["stale_labels"]):
            L.append(f"  {l}")
        L.append("")

    L.append("UNTRIAGED ISSUES -- run `bd show <id>` for descriptions before classifying")
    L.append(f"{'id':<18} {'type':<10} {'current labels':<36} title")
    L.append("-" * 100)
    for r in data["untriaged_rows"]:
        labels_str = ", ".join(r["labels"]) if r["labels"] else "(none)"
        L.append(f"{r['id']:<18} {r['type']:<10} {labels_str:<36} {r['title'][:52]}")

    return "\n".join(L)


def main():
    data = build()
    print(render_text(data))
    sys.exit(1 if data["untriaged_count"] == 0 else 0)


if __name__ == "__main__":
    main()
