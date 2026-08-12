import io
import subprocess
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch

import docker_probe

class DockerProbeTests(unittest.TestCase):
    def result(self, returncode=0, stdout="27.0.1\n", stderr=""):
        return type("Completed", (), {"returncode": returncode, "stdout": stdout, "stderr": stderr})()

    def test_success(self):
        r = docker_probe.probe_docker(lambda *a, **k: self.result())
        self.assertTrue(r.usable)

    def test_nonzero(self):
        r = docker_probe.probe_docker(lambda *a, **k: self.result(1, stderr="daemon stopped"))
        self.assertFalse(r.usable); self.assertIn("daemon stopped", r.diagnostic)

    def test_missing_cli(self):
        def run(*a, **k): raise FileNotFoundError
        self.assertFalse(docker_probe.probe_docker(run).usable)

    def test_timeout(self):
        def run(*a, **k): raise subprocess.TimeoutExpired("docker", 10)
        self.assertIn("timed out", docker_probe.probe_docker(run).diagnostic)

    def test_empty_version(self):
        self.assertFalse(docker_probe.probe_docker(lambda *a, **k: self.result(stdout="")).usable)

    @patch.object(docker_probe, "probe_docker")
    def test_cli_exit_and_output_success(self, probe):
        probe.return_value = docker_probe.DockerProbeResult(True, "Docker CLI and daemon are usable", "27")
        output = io.StringIO()
        with redirect_stdout(output): code = docker_probe.main()
        self.assertEqual(code, 0); self.assertIn("usable", output.getvalue())

    @patch.object(docker_probe, "probe_docker")
    def test_cli_exit_and_output_failure(self, probe):
        probe.return_value = docker_probe.DockerProbeResult(False, "Docker daemon is unavailable", "stopped")
        output = io.StringIO()
        with redirect_stdout(output): code = docker_probe.main()
        self.assertEqual(code, 1); self.assertIn("Docker unavailable", output.getvalue())

if __name__ == "__main__": unittest.main()
