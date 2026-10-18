# Contributing to Plantry

## Contributions are not being accepted at this time

Plantry is open source (AGPLv3) and the code is public for transparency, self-hosting, and inspection. However, **outside contributions — pull requests, feature branches, patches — are not being accepted right now**. The project is in an early, fast-moving phase and the development model is not yet set up to review and integrate external work responsibly.

This may change. Check back here, or watch the repository for updates.

### If you found a bug

Please [open an issue](../../issues/new) describing what you observed, what you expected, and how to reproduce it. Bug reports are welcome even while PRs are not.

### If you have a feature idea

Open an issue and describe what you'd like and why. Ideas are valuable even if we cannot act on them immediately.

### If you are self-hosting and stuck

See [docs/Operations/self-hosting.md](docs/Operations/self-hosting.md). The configuration options, upgrade contract, and backup procedure are documented there.

---

## How this project is developed

Plantry uses a **fully agentic workflow** — described in [ADR-018](docs/ADRs/ADR-018.md) and the repo README — where AI coding agents handle day-to-day implementation under human oversight. Issues are managed via [`bd` (beads)](https://github.com/gastownhall/beads), a git-native issue tracker agents and humans share.

The development loop:

1. Work is planned and described as a beads issue.
2. The factory claims one issue, implements it in an isolated branch/worktree, runs the full build and required suites, and opens one PR.
3. A different reviewer checks the acceptance criteria and canonical review gates; an independent QA agent exercises the live candidate on an isolated Aspire stack.
4. Required CI must pass for the same candidate. Auto-merge is enabled only after local checks, review, QA, and CI pass; the issue closes after confirmed merge.

This model means the usual contributor workflow (fork → branch → PR → review) does not fit yet — not because contributions are unwanted in principle, but because the review pipeline has not been built to handle external PRs safely.

---

## License

Plantry is licensed under the [GNU Affero General Public License v3.0](LICENSE). This means:

- You can read, run, and modify the code for your own use.
- If you distribute a modified version (including over a network), you must publish your changes under the same license.
- See [LICENSE](LICENSE) for the full terms.
