# CI/CD status

This file used to describe a phased rollout. The current process is implemented and is documented in [agentic-workflow.md](../Engineering/agentic-workflow.md), [ADR-016](../ADRs/ADR-016.md), and the GitHub Actions workflows.

## Pull request gate

`.github/workflows/ci.yml` builds the full solution, then runs Unit, Integration, Web, Architecture, Aspire E2E, and island JavaScript tests serially. Every required suite must execute at least one test. Integration and Aspire E2E use Docker-backed infrastructure and do not run concurrently. The factory explicitly requires a successful `fast` job even if branch protection is absent. The job name stays stable for protection settings even though it now runs the full suite. Repository protection must be inspected rather than assumed.

Each issue has one `issue/<ticket-id>` branch and one PR. The factory enables auto-merge only after current-candidate local verification, independent review, independent live QA, and required CI all pass. The issue closes only after a confirmed merge. Epics organize dependencies and never create delivery batches.

## Release and deployment

`.github/workflows/release.yml` repeats the full build and suite gate for a version tag before publishing images and deploying. Production deployment remains release-gated and smoke-checks `/alive`.

## Verification boundaries

The factory uses behavioral test adequacy review. Test-count targets, coverage collection, percentage thresholds, floors, ratchets, denominator enforcement, and mutation testing are not delivery gates. Review, QA, and CI evidence must name the candidate commit; any repair or base update requires fresh evidence for the resulting candidate.
