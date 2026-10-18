# Agentic engineering workflow

Plantry delivers one Beads ticket at a time through one branch, one worktree, and one pull request. The current agent-facing procedures live in `.claude/skills/factory-*`; `.claude/agents/factory-*` defines the independent implementation, review, and QA roles. This document describes their shared contract. ADR-018 records the project principle, and ADR-016 records CI and release decisions.

## Ticket and state model

Write one issue for each independently mergeable change. An epic groups work and records dependencies only; it never becomes a branch, batch, or release unit. Do not create rollups, staged or sealed states, integration branches, or batch closures.

`factory-run` accepts either one named issue or continuous ready-ticket processing. Both use the same serial procedure. Before claiming new work, inspect Beads for an unfinished `FACTORY STATE v1` comment and resume that ticket first. State comments record the current phase, issue, branch, worktree, base and candidate commits, PR, gate results, and next action. A gate result is evidence only for the exact candidate it names.

## Delivery sequence

1. Select one ready, non-epic issue with an owner-approved specification (spec:ready). Exclude needs-spec, needs-split, needs-human, parked, and unresolved decisions. Claim it atomically and record its initial state in Beads. Named tickets meet the same readiness requirements; spec approval alone does not start implementation.
2. Dispatch the independent implementer to the ticket's `issue/<ticket-id>` worktree. Build the full solution and run Unit, Integration, Web, Architecture, Aspire E2E, and island JavaScript tests serially. Record warnings and executed, passed, skipped, and failed counts. Zero executed tests in a required suite fails. Check test project naming and placement, xUnit and `Microsoft.NET.Test.Sdk` references, and a tracked issue plus reason on every skipped test.
3. Push the issue branch and open one PR for the issue after local verification passes. Keep the same PR through repairs.
4. Dispatch a fresh, read-only reviewer against the exact base and candidate commits. The reviewer verifies each acceptance criterion and applies every gate in `.claude/review-criteria.md`.
5. Dispatch a different, independent QA agent against the same candidate. It boots the isolated Aspire stack, verifies resource health and migrations, exercises actual application entry points, confirms persistence and relevant negative paths, inspects browser/network/log telemetry, and stops only its own stack.
6. Wait for the required full CI check. CI may finish while review or QA is running. Enable automatic merge only after local verification, review PASS, QA PASS, and CI all apply to the current candidate and base/integration state.
7. Return review, QA, and code-related CI findings to the implementer. A repair, rebase, conflict resolution, or base refresh that changes the candidate invalidates prior evidence. Repeat full local verification, push the new candidate to the existing issue branch, and rerun independent review, independent QA, and required CI on the resulting candidate, updating the same PR.
8. Confirm the PR is merged, record the merged commit and evidence in Beads, then close the issue, run `bd dolt push`, and clean up only its worktree. Verify the Beads sync before selecting another ticket. Before ending or handing off any run, sync Beads even when a FAIL or BLOCKED ticket remains open. A blocked ticket leaves the PR unmerged and stops continuous processing until it has an actionable resolution.

## Verification policy

Run the full solution build and all required test suites for every ticket. The .NET suites run one after another; Integration and Aspire E2E must never overlap. The full per-PR CI job repeats the build and suites. Coverage collection, coverage percentages, floors, ratchets, denominator rules, mutation testing, and test-count targets are not delivery gates.

Review test value by behavior: acceptance criteria and relevant invariants, plausible regressions and negative paths, meaningful outcome assertions, correct test level, and signal versus brittleness or redundancy. Existing tests may be sufficient. A blocking test finding names the unprotected behavior, a plausible defect the current tests would miss, and a specific test scenario and outcome assertion.

QA results are PASS, FAIL, or BLOCKED. Missing infrastructure, credentials, or access to an acceptance scenario is BLOCKED, never PASS. Documentation and tooling changes still require an isolated smoke boot plus direct validation of the changed artifact.

For Aspire details and safeguards that protect the primary stack, use [worktree verification](worktree-verification.md). The current `FakeDataSeeder` seeds catalog products, recipes, inventory, demo price observations, meal plans and attendees, and Deals review data. Verify the current seeder and scenario before relying on a fixture detail.
