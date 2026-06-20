# Public PR Runner Policy

> This policy governs how GitHub Actions workflows run when a pull request originates
> from a fork (i.e. a public contributor, not a project maintainer). It is part of
> the OSS publish surface defined in [cicd-rollout-plan.md](cicd-rollout-plan.md) Phase 5.

## The risk

When a fork opens a PR, GitHub Actions runs workflow code from the fork's branch on
your runners. If those runners have access to repository secrets (deploy keys, GHCR
credentials, SSH keys), a malicious or compromised fork can exfiltrate them.

The mitigation is to never run fork PRs on runners that have access to secrets, and
never pass secrets into jobs triggered by `pull_request` events from forks.

## Current state

Plantry's CI workflows (`ci.yml`) trigger on:

```yaml
on:
  push:
    branches: [main, "slice/**"]
  pull_request:
    branches: [main]
```

The `pull_request` trigger is safe by default for fork PRs: GitHub **does not pass
repository secrets** to `pull_request` jobs from forks. The workflow runs, but any
`${{ secrets.FOO }}` resolves to an empty string — it does not fail, it just has no
credential.

The CD workflow (`cd.yml`) does not trigger on `pull_request` at all — it only runs
on `push` to `main`. So no deploy credentials are reachable from a fork PR.

## Policy

### What already holds (no action needed)

- `pull_request` events from forks run without secrets. This is GitHub's default and
  we rely on it — do not add `pull_request_target` triggers, which bypass this protection.
- The CD workflow (`cd.yml`) is push-only and is never triggered by a PR event.
- `CODEOWNERS` (`.github/CODEOWNERS`) requires explicit approval from `@mgasparel`
  before any PR can merge, giving a human review gate independent of CI.

### Do not use `pull_request_target`

The `pull_request_target` trigger runs with secrets from the *base* repository, even
for fork PRs. **Never use it for jobs that run untrusted code** (build, test, script
steps). The safe pattern — if you ever need `pull_request_target` for something like
posting a comment after a trusted CI result — is to split the workflow into two jobs:
one triggered by `pull_request` (runs untrusted code, no secrets) and one triggered
by `workflow_run` on the first (runs with secrets, posts the result). We do not
currently need this and should not add `pull_request_target` without deliberately
implementing the split.

### Do not use self-hosted runners for fork PRs

If self-hosted runners are ever added (for performance or hardware access), they must
not be used by `pull_request` jobs. Self-hosted runners persist a local environment
between jobs — a malicious fork can plant files that affect later jobs run by other
users. Use GitHub-hosted runners for any job that may be triggered by a fork.

In GitHub Actions: scope self-hosted runners to jobs that only trigger on `push`
(always from a maintainer) or on `pull_request` with `if: github.event.pull_request.head.repo.full_name == github.repository` (first-party PRs only).

### Secret hygiene for new workflows

When adding a new workflow step that requires a secret:

1. Place it in a job that is **not** reachable from a fork `pull_request` trigger.
2. Prefer `push`-only or `workflow_dispatch` triggers for secret-bearing jobs.
3. If a secret must flow into a `pull_request` job (unusual), verify the trigger is
   `pull_request` (not `pull_request_target`) and confirm secrets are blocked by
   checking `github.event.pull_request.head.repo.full_name != github.repository`.

## Summary

| Scenario | Risk | Mitigation |
|----------|------|------------|
| Fork PR triggers `ci.yml` | Untrusted code runs in CI | GitHub blocks secrets on `pull_request` from forks — already safe |
| Fork PR triggers `cd.yml` | Deploy credentials exfiltrated | `cd.yml` has no `pull_request` trigger — not reachable |
| Self-hosted runner used for `pull_request` | Environment contamination | Policy: never; use GitHub-hosted runners for PR jobs |
| `pull_request_target` added | Secrets available to fork code | Policy: never without the `pull_request` + `workflow_run` split |
