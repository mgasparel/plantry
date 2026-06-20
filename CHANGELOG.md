# Changelog

All notable changes to Plantry are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions correspond to semver image tags published to GHCR (`plantry-web:<version>`).

---

## Versioning and support matrix

### Tag convention

| Tag | Meaning |
|-----|---------|
| `:1.4.0` | Exact release — immutable, never overwritten |
| `:1.4` | Floating minor — tracks the latest patch of `1.4.x` |
| `:1` | Floating major — tracks the latest release of `1.x.y` |
| `:latest` | Identical to the highest stable release; **not safe to pin for production** |

Self-hosters should pin `:1` or `:1.4` to receive patch fixes automatically. Pin the exact tag (`:1.4.0`) only when you want explicit control over every upgrade. See [docs/Operations/self-hosting.md](docs/Operations/self-hosting.md) for the full upgrade contract.

### Support matrix

| Major version | Status | Notes |
|---------------|--------|-------|
| (pre-release) | No stability guarantees | Breaking changes may appear at any version |

Once `1.0.0` is tagged, the policy will be:

- **Current major** (`1.x.y`): security fixes and critical bug fixes are backported.
- **Previous major**: receives security fixes only for 6 months after the next major tag.
- **Older majors**: unsupported; upgrade.

A breaking schema migration is signalled by a major version bump. There is **no safe rollback** after a migration runs — back up before every upgrade. See [self-hosting.md](docs/Operations/self-hosting.md#upgrade-contract) for the recovery procedure.

---

## Unreleased

### Added

- OSS publish surface: `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md`, `CHANGELOG.md`, and public-PR runner policy (`docs/Operations/public-pr-runner-policy.md`). (plantry-wzz6.11)
