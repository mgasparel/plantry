#!/usr/bin/env python3
"""Batch-closure validation gate -- the DETERMINISTIC half of batch planning for
the pipeline-orchestrator (see SKILL.md, "Batch-closure gate").

Two modes, both driven off a single `bd graph --all --json` snapshot:

  check_batch.py <id> [<id> ...]   Validate a NAMED batch (the gate). A human named
                                   a set S to ship as one MR; verify it is
                                   dependency-complete and, if not, refuse it and
                                   surface an add-or-drop decision (with the full
                                   transitive blast radius of each choice).

  check_batch.py --plan            Autonomous planning: list the open connected
                                   components as candidate batches, flagging which
                                   are drainable (every node buildable) vs. blocked
                                   by an unbuildable (needs-spec / parked) node.

Why `bd graph --all --json` is the whole story: it returns the entire OPEN graph,
and bd prunes edges to CLOSED blockers (a closed blocker is on `main`, i.e. already
satisfied). So every edge we see is an OPEN blocker -- exactly the conflict
candidates -- and we never have to special-case "already on main".

Exit codes (validate mode): 0 valid, 2 incomplete (needs a decision), 1 error.
Add `--json` for a structured dump instead of the text report.

Requires the `bd` CLI on PATH. Python 3.8+. No third-party deps.
"""
import json
import subprocess
import sys

# A node the orchestrator cannot build yet -- pulling it into a batch would make
# the batch un-shippable (it can never reach 100% built).
UNBUILDABLE_LABELS = {"needs-spec", "status:parked", "parked"}


# --------------------------------------------------------------------------- #
# bd I/O (the only impure part; everything below operates on plain dicts)      #
# --------------------------------------------------------------------------- #
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


def load_graph():
    """Return (nodes, edges, components) from `bd graph --all --json`.

    nodes:      {id: {status, labels, priority, title}}   -- open issues only
    edges:      [(blocker_id, blocked_id), ...]            -- blocker BLOCKS blocked
    components: [[id, ...], ...]                           -- bd's connected groups
    """
    comps = bd_json("graph", "--all")
    return _parse_graph(comps)


def _parse_graph(comps):
    """Pure: turn `bd graph --all --json` payload into (nodes, edges, components).

    Split out so tests can feed a fixture payload without invoking bd.
    """
    nodes = {}
    edges = []
    components = []
    for comp in comps:
        issue_map = comp.get("IssueMap") or {}
        for iid, rec in issue_map.items():
            nodes[iid] = {
                "status": rec.get("status"),
                "labels": rec.get("labels") or [],
                "priority": rec.get("priority"),
                "title": rec.get("title", ""),
            }
        components.append(list(issue_map.keys()))
        for e in comp.get("Dependencies") or []:
            if e.get("type") != "blocks":
                continue  # relates_to / tracks / parent-child do not gate a build
            blocker, blocked = e.get("depends_on_id"), e.get("issue_id")
            if blocker and blocked:
                edges.append((blocker, blocked))
    # Defensive: keep only edges whose endpoints are both in the open graph.
    edges = [(b, y) for (b, y) in edges if b in nodes and y in nodes]
    return nodes, edges, components


# --------------------------------------------------------------------------- #
# pure graph helpers                                                          #
# --------------------------------------------------------------------------- #
def is_buildable(node):
    return (
        node.get("status") == "open"
        and not (set(node.get("labels") or []) & UNBUILDABLE_LABELS)
    )


def _adj(edges, key_index, val_index):
    """Adjacency map from edges, keyed by one endpoint -> set of the other."""
    adj = {}
    for edge in edges:
        adj.setdefault(edge[key_index], set()).add(edge[val_index])
    return adj


def blockers_map(edges):
    """blocked_id -> set(blocker_ids) -- 'what must land before me'."""
    return _adj(edges, 1, 0)


def dependents_map(edges):
    """blocker_id -> set(blocked_ids) -- 'what waits on me'."""
    return _adj(edges, 0, 1)


def reachable(seeds, adj):
    """Transitive closure of `seeds` over adjacency `adj` (excludes the seeds)."""
    seen = set()
    stack = list(seeds)
    while stack:
        n = stack.pop()
        for m in adj.get(n, ()):
            if m not in seen:
                seen.add(m)
                stack.append(m)
    return seen


# --------------------------------------------------------------------------- #
# validate mode                                                              #
# --------------------------------------------------------------------------- #
def validate_batch(nodes, edges, requested):
    """Validate that the named set `requested` is a shippable, complete batch.

    Returns a structured result (see keys below). Pure -- test with fixtures.
    """
    seen = set()
    ordered_req = [x for x in requested if not (x in seen or seen.add(x))]

    excluded = [x for x in ordered_req if x not in nodes]  # closed or unknown
    members = [x for x in ordered_req if x in nodes]
    member_set = set(members)

    result = {
        "status": "empty" if not members else None,
        "requested": ordered_req,
        "excluded_not_open": excluded,
        "members": members,
        "build_order": [],
        "missing": {},
        "add_set": [],
        "add_has_unbuildable": False,
        "drop_options": {},
    }
    if not members:
        return result

    blockers = blockers_map(edges)
    dependents = dependents_map(edges)

    # A member's OPEN blocker that is not itself in the batch = a missing dep.
    missing = {}
    for y in members:
        gap = sorted(x for x in blockers.get(y, ()) if x not in member_set)
        if gap:
            missing[y] = gap

    if not missing:
        order, cycle = _priority_topo(nodes, members, edges)
        result["status"] = "valid"
        result["build_order"] = order
        result["has_cycle"] = cycle
        return result

    result["status"] = "incomplete"
    result["missing"] = missing

    # ADD direction: the full completion set = every missing blocker plus its own
    # transitive open-blocker closure, minus what's already in the batch.
    seeds = set().union(*missing.values())
    add_ids = (seeds | reachable(seeds, blockers)) - member_set
    add_set = []
    for aid in sorted(add_ids, key=lambda i: (nodes[i]["priority"], i)):
        buildable = is_buildable(nodes[aid])
        add_set.append({
            "id": aid,
            "title": nodes[aid]["title"],
            "buildable": buildable,
            "labels": nodes[aid]["labels"],
        })
    result["add_set"] = add_set
    result["add_has_unbuildable"] = any(not a["buildable"] for a in add_set)

    # DROP direction: dropping member Y also drops every batch member that
    # transitively depends on Y.
    for y in missing:
        cascade = {y} | (reachable({y}, dependents) & member_set)
        result["drop_options"][y] = sorted(cascade)

    return result


def _priority_topo(nodes, members, edges):
    """topo_order with tie-break by the real bd priority of each node."""
    member_set = set(members)
    sub = [(b, y) for (b, y) in edges if b in member_set and y in member_set]
    indeg = {m: 0 for m in members}
    outs = {m: [] for m in members}
    for b, y in sub:
        indeg[y] += 1
        outs[b].append(y)

    def rank(mid):
        return (nodes[mid]["priority"], mid)

    ready = sorted([m for m in members if indeg[m] == 0], key=rank)
    order = []
    while ready:
        n = ready.pop(0)
        order.append(n)
        for m in outs[n]:
            indeg[m] -= 1
            if indeg[m] == 0:
                ready.append(m)
        ready.sort(key=rank)
    return order, (len(order) != len(members))


# --------------------------------------------------------------------------- #
# plan mode (autonomous "just go")                                           #
# --------------------------------------------------------------------------- #
def plan_components(nodes, edges, components):
    """Classify each open connected component as a candidate batch.

    A component is always internally dependency-complete (closed blockers are
    pruned, so every open blocker of a member is a sibling). It is *drainable*
    iff no member is unbuildable. Otherwise only the buildable prefix -- members
    with no unbuildable ancestor -- can ship this round.
    """
    blockers = blockers_map(edges)
    out = []
    for comp in components:
        comp_set = set(comp)
        unbuildable = [c for c in comp if not is_buildable(nodes[c])]
        # A member is shippable now iff none of its transitive blockers is unbuildable.
        buildable_now = []
        blocked_now = []
        for c in comp:
            ancestors = reachable({c}, blockers) & comp_set
            if is_buildable(nodes[c]) and all(is_buildable(nodes[a]) for a in ancestors):
                buildable_now.append(c)
            else:
                blocked_now.append(c)
        order, _ = _priority_topo(nodes, buildable_now, edges)
        out.append({
            "ids": comp,
            "size": len(comp),
            "drainable": not unbuildable,
            "unbuildable": sorted(unbuildable),
            "shippable_now": order,
            "blocked_now": sorted(blocked_now),
            "top_priority": min((nodes[c]["priority"] for c in comp), default=99),
        })
    out.sort(key=lambda c: (0 if c["drainable"] else 1, c["top_priority"], -c["size"]))
    return out


# --------------------------------------------------------------------------- #
# rendering                                                                  #
# --------------------------------------------------------------------------- #
def _label(nodes, iid):
    t = nodes[iid]["title"] if iid in nodes else ""
    return f"{iid} ({t[:48]})" if t else iid


def render_validate(nodes, r):
    L = []
    if r["status"] == "empty":
        L.append("GATE: ERROR -- none of the named issues are open.")
        if r["excluded_not_open"]:
            L.append("  Not open (closed or unknown): " + ", ".join(r["excluded_not_open"]))
        return "\n".join(L)

    n = len(r["members"])
    if r["excluded_not_open"]:
        L.append("NOTE: excluded (not open -- closed or unknown): "
                 + ", ".join(r["excluded_not_open"]))
        L.append("")

    if r["status"] == "valid":
        L.append(f"GATE: VALID -- batch of {n} is dependency-complete; ship as one MR.")
        L.append("  Build order: " + "  ->  ".join(r["build_order"]))
        if r.get("has_cycle"):
            L.append("  WARNING: a dependency cycle was detected; order is best-effort.")
        return "\n".join(L)

    # incomplete
    L.append(f"GATE: INCOMPLETE -- batch of {n} is missing dependencies; do NOT build yet.")
    L.append("")
    L.append("MISSING (each batch member and the open blocker it needs):")
    for y, xs in r["missing"].items():
        needs = ", ".join(_label(nodes, x) for x in xs)
        L.append(f"  {_label(nodes, y)}")
        L.append(f"      depends on -> {needs}")
    L.append("")

    L.append("RESOLVE -- pick a direction (each shows its full blast radius):")
    add = r["add_set"]
    add_ids = ", ".join(a["id"] for a in add)
    L.append(f"  * ADD the missing deps ({len(add)}): {add_ids}")
    for a in add:
        if not a["buildable"]:
            bad = ", ".join(sorted(set(a["labels"]) & UNBUILDABLE_LABELS)) or "not buildable"
            L.append(f"      - {_label(nodes, a['id'])}  [NOT BUILDABLE: {bad}]")
    if r["add_has_unbuildable"]:
        L.append("      NOTE: some deps are not buildable (needs-spec/parked) -- spec them")
        L.append("            first, or drop the dependent(s) below.")
    L.append("  * OR DROP a dependent (cascades to everything that depends on it):")
    for y, cascade in r["drop_options"].items():
        also = [c for c in cascade if c != y]
        tail = f"  (also drops: {', '.join(also)})" if also else ""
        L.append(f"      - drop {_label(nodes, y)}{tail}")
    return "\n".join(L)


def render_plan(nodes, plan):
    L = []
    drainable = [c for c in plan if c["drainable"]]
    blocked = [c for c in plan if not c["drainable"]]
    L.append(f"PLAN: {len(plan)} open component(s) -- "
             f"{len(drainable)} drainable, {len(blocked)} blocked by an unbuildable node.")
    L.append("")
    for c in plan:
        head = "DRAINABLE" if c["drainable"] else "BLOCKED"
        L.append(f"[{head}] batch of {c['size']} (top P{c['top_priority']}): "
                 + ", ".join(c["ids"]))
        if c["drainable"] and c["size"] > 1:
            L.append("    build order: " + "  ->  ".join(c["shippable_now"]))
        if not c["drainable"]:
            L.append("    unbuildable: " + ", ".join(_label(nodes, i) for i in c["unbuildable"]))
            if c["shippable_now"]:
                L.append("    buildable prefix (shippable now): "
                         + ", ".join(c["shippable_now"]))
            else:
                L.append("    nothing shippable this round -- skip until unblocked.")
    return "\n".join(L)


# --------------------------------------------------------------------------- #
# CLI                                                                        #
# --------------------------------------------------------------------------- #
def main(argv):
    args = [a for a in argv if a != "--json"]
    as_json = "--json" in argv

    if args and args[0] == "--plan":
        nodes, edges, components = load_graph()
        plan = plan_components(nodes, edges, components)
        print(json.dumps(plan, indent=2) if as_json else render_plan(nodes, plan))
        return 0

    if not args:
        sys.exit("usage: check_batch.py <id> [<id> ...] | --plan   [--json]")

    nodes, edges, _ = load_graph()
    r = validate_batch(nodes, edges, args)
    print(json.dumps(r, indent=2) if as_json else render_validate(nodes, r))
    return {"valid": 0, "incomplete": 2, "empty": 1}[r["status"]]


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
