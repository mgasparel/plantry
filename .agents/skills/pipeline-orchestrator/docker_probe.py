#!/usr/bin/env python3
"""Fail-fast Docker availability probe for the pipeline orchestrator."""
from dataclasses import dataclass
import subprocess

@dataclass(frozen=True)
class DockerProbeResult:
    usable: bool
    reason: str
    detail: str = ""

    @property
    def diagnostic(self):
        prefix = self.reason if self.usable else f"Docker unavailable: {self.reason}"
        return f"{prefix} ({self.detail})" if self.detail else prefix

def probe_docker(runner=subprocess.run, *, timeout_seconds=10):
    try:
        result = runner(("docker", "version", "--format", "{{.Server.Version}}"), capture_output=True, text=True, timeout=timeout_seconds, check=False)
    except FileNotFoundError:
        return DockerProbeResult(False, "docker CLI not found", "install Docker Desktop or add docker to PATH")
    except subprocess.TimeoutExpired:
        return DockerProbeResult(False, "Docker daemon probe timed out", f"after {timeout_seconds:g}s")
    except OSError as error:
        return DockerProbeResult(False, "unable to start docker CLI", str(error))
    if result.returncode != 0:
        return DockerProbeResult(False, "Docker daemon is unavailable", (result.stderr or result.stdout or "command failed").strip())
    version = (result.stdout or "").strip()
    if not version:
        return DockerProbeResult(False, "Docker daemon returned no server version")
    return DockerProbeResult(True, "Docker CLI and daemon are usable", version)

def main():
    result = probe_docker()
    print(result.diagnostic)
    return 0 if result.usable else 1

if __name__ == "__main__":
    raise SystemExit(main())
