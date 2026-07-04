#!/usr/bin/env python3
"""Fixture-driven tests for check_batch.py -- no `bd`, no network.

Run:  python -m unittest test_check_batch      (from the skill directory)
"""
import unittest

import check_batch as cb


def graph(nodes, edges):
    """Build (nodes, edges) test inputs.

    nodes: {id: labels_list}  (status defaults to 'open', priority to 2)
    edges: [(blocker, blocked), ...]
    """
    ns = {
        i: {"status": "open", "labels": labels, "priority": 2, "title": i}
        for i, labels in nodes.items()
    }
    return ns, list(edges)


class ParseGraphTests(unittest.TestCase):
    def test_parses_issuemap_and_blocks_edges_only(self):
        payload = [{
            "IssueMap": {
                "A": {"status": "open", "priority": 1, "title": "A", "labels": ["x"]},
                "B": {"status": "open", "priority": 2, "title": "B"},
            },
            "Dependencies": [
                {"depends_on_id": "A", "issue_id": "B", "type": "blocks"},
                {"depends_on_id": "A", "issue_id": "B", "type": "relates_to"},
            ],
        }]
        nodes, edges, comps = cb._parse_graph(payload)
        self.assertEqual(set(nodes), {"A", "B"})
        self.assertEqual(nodes["A"]["labels"], ["x"])
        self.assertEqual(nodes["B"]["labels"], [])          # missing -> []
        self.assertEqual(edges, [("A", "B")])                # relates_to dropped
        self.assertEqual(comps, [["A", "B"]])

    def test_drops_edges_to_pruned_closed_blocker(self):
        # bd prunes closed nodes; a dangling edge endpoint must be ignored.
        payload = [{
            "IssueMap": {"B": {"status": "open", "title": "B", "priority": 2}},
            "Dependencies": [{"depends_on_id": "CLOSED", "issue_id": "B", "type": "blocks"}],
        }]
        nodes, edges, _ = cb._parse_graph(payload)
        self.assertEqual(edges, [])


class ValidateTests(unittest.TestCase):
    def test_complete_chain_is_valid_in_topo_order(self):
        # A blocks B blocks C, all requested.
        nodes, edges = graph({"A": [], "B": [], "C": []}, [("A", "B"), ("B", "C")])
        r = cb.validate_batch(nodes, edges, ["C", "A", "B"])
        self.assertEqual(r["status"], "valid")
        self.assertEqual(r["build_order"], ["A", "B", "C"])

    def test_independent_islands_both_present_is_valid(self):
        nodes, edges = graph({"A": [], "B": [], "C": [], "D": []},
                             [("A", "B"), ("C", "D")])
        r = cb.validate_batch(nodes, edges, ["A", "B", "C", "D"])
        self.assertEqual(r["status"], "valid")

    def test_missing_direct_dep_is_incomplete(self):
        # D blocks B; user asked for A,B,C but not D.
        nodes, edges = graph({"A": [], "B": [], "C": [], "D": []},
                             [("D", "B")])
        r = cb.validate_batch(nodes, edges, ["A", "B", "C"])
        self.assertEqual(r["status"], "incomplete")
        self.assertEqual(r["missing"], {"B": ["D"]})
        self.assertEqual([a["id"] for a in r["add_set"]], ["D"])

    def test_add_cascade_is_transitive(self):
        # E blocks D blocks B; asking for just B must offer to add BOTH D and E.
        nodes, edges = graph({"B": [], "D": [], "E": []},
                             [("E", "D"), ("D", "B")])
        r = cb.validate_batch(nodes, edges, ["B"])
        self.assertEqual(r["status"], "incomplete")
        self.assertEqual([a["id"] for a in r["add_set"]], ["D", "E"])

    def test_drop_cascade_is_transitive(self):
        # A blocks B blocks C blocks G; A missing. Dropping B drops C and G too.
        nodes, edges = graph({"A": [], "B": [], "C": [], "G": []},
                             [("A", "B"), ("B", "C"), ("C", "G")])
        r = cb.validate_batch(nodes, edges, ["B", "C", "G"])
        self.assertEqual(r["status"], "incomplete")
        self.assertEqual(r["drop_options"]["B"], ["B", "C", "G"])

    def test_unbuildable_dep_flagged(self):
        # D blocks B, and D needs-spec: "add D" is not directly actionable.
        nodes, edges = graph({"B": [], "D": ["needs-spec"]}, [("D", "B")])
        r = cb.validate_batch(nodes, edges, ["B"])
        self.assertTrue(r["add_has_unbuildable"])
        self.assertFalse(r["add_set"][0]["buildable"])

    def test_closed_blocker_absent_from_graph_is_satisfied(self):
        # A is on main (closed) so bd pruned it: B has no open blocker -> valid.
        nodes, edges = graph({"B": [], "C": []}, [])
        r = cb.validate_batch(nodes, edges, ["B", "C"])
        self.assertEqual(r["status"], "valid")

    def test_named_closed_or_unknown_is_excluded(self):
        nodes, edges = graph({"A": []}, [])
        r = cb.validate_batch(nodes, edges, ["A", "GHOST"])
        self.assertEqual(r["excluded_not_open"], ["GHOST"])
        self.assertEqual(r["members"], ["A"])
        self.assertEqual(r["status"], "valid")

    def test_empty_when_no_open_members(self):
        nodes, edges = graph({"A": []}, [])
        r = cb.validate_batch(nodes, edges, ["GHOST"])
        self.assertEqual(r["status"], "empty")


class PlanTests(unittest.TestCase):
    def test_fully_buildable_component_is_drainable(self):
        nodes, edges = graph({"A": [], "B": []}, [("A", "B")])
        plan = cb.plan_components(nodes, edges, [["A", "B"]])
        self.assertTrue(plan[0]["drainable"])
        self.assertEqual(plan[0]["shippable_now"], ["A", "B"])

    def test_component_with_unbuildable_node_is_blocked_with_prefix(self):
        # A buildable blocks B; B needs-spec blocks C. Only A ships now.
        nodes, edges = graph({"A": [], "B": ["needs-spec"], "C": []},
                             [("A", "B"), ("B", "C")])
        plan = cb.plan_components(nodes, edges, [["A", "B", "C"]])
        c = plan[0]
        self.assertFalse(c["drainable"])
        self.assertEqual(c["unbuildable"], ["B"])
        self.assertEqual(c["shippable_now"], ["A"])   # C is behind unbuildable B
        self.assertEqual(c["blocked_now"], ["B", "C"])

    def test_drainable_sorted_before_blocked(self):
        nodes, edges = graph(
            {"A": [], "P": ["parked"]},
            [],
        )
        plan = cb.plan_components(nodes, edges, [["P"], ["A"]])
        self.assertTrue(plan[0]["drainable"])   # A's component first
        self.assertEqual(plan[0]["ids"], ["A"])


if __name__ == "__main__":
    unittest.main()
