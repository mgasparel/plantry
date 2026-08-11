#!/usr/bin/env python3
"""Verify the generated-regex coverage denominator correction."""
from __future__ import annotations

import argparse
import json
import re
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

RUNSETTINGS = Path(__file__).resolve().parents[1] / "coverage.runsettings"
GENERATED_PATTERN = "**/obj/**/System.Text.RegularExpressions.Generator/**/*.g.cs"
GENERATED_RE = re.compile(
    r"(?:^|[\\/])obj(?:[\\/]).*System\.Text\.RegularExpressions\.Generator"
    r"(?:[\\/]).*\.g\.cs$",
    re.IGNORECASE,
)
DEAL_RE = re.compile(
    r"(?:^|[\\/])(?:src[\\/])?Plantry\.Market[\\/]Domain[\\/]Deals[\\/]"
    r"DealNormalizer\.cs$",
    re.IGNORECASE,
)


def normalise(path: str) -> str:
    return path.replace("\\", "/").lstrip("./")


def is_generated(path: str) -> bool:
    return bool(GENERATED_RE.search(normalise(path)))


def is_deal_normalizer(path: str) -> bool:
    return bool(DEAL_RE.search(normalise(path)))


def read_exclusions() -> list[str]:
    root = ET.parse(RUNSETTINGS).getroot()
    node = root.find(".//ExcludeByFile")
    if node is None or not node.text:
        raise ValueError(f"{RUNSETTINGS} has no ExcludeByFile entries")
    return [entry.strip() for entry in node.text.split(",") if entry.strip()]


def glob_matches(pattern: str, path: str) -> bool:
    pattern = normalise(pattern)
    path = normalise(path)
    regex = re.escape(pattern).replace(r"\*\*/", r"(?:.*/)?")
    regex = regex.replace(r"\*", r"[^/]*").replace(r"\?", r"[^/]")
    return re.fullmatch(regex, path, re.IGNORECASE) is not None


def validate_runsettings() -> None:
    exclusions = read_exclusions()
    if GENERATED_PATTERN not in exclusions:
        raise ValueError(f"runsettings is missing the narrow generated-regex exclusion: {GENERATED_PATTERN}")
    if any("DealNormalizer.cs" in entry or "Plantry.Market" in entry for entry in exclusions):
        raise ValueError("runsettings contains a broad or handwritten Market exclusion")
    generated_path = "src/Plantry.Market/obj/Debug/net10.0/System.Text.RegularExpressions.Generator/RegexGenerator.g.cs"
    handwritten_path = "src/Plantry.Market/Domain/Deals/DealNormalizer.cs"
    if not glob_matches(GENERATED_PATTERN, generated_path):
        raise ValueError("generated-regex exclusion does not match a representative generated path")
    if glob_matches(GENERATED_PATTERN, handwritten_path):
        raise ValueError("generated-regex exclusion matches handwritten DealNormalizer.cs")


def class_rows(path: Path) -> list[ET.Element]:
    return list(ET.parse(path).getroot().findall(".//class"))


def covered_lines(row: ET.Element) -> int:
    lines = row.findall(".//line")
    return sum(1 for line in lines if int(line.attrib.get("hits", "0")) > 0)


def validate_cobertura(paths: list[Path]) -> None:
    if not paths:
        raise ValueError("no Cobertura files matched")
    generated_rows = []
    deal_rows = []
    market_packages = 0
    for path in paths:
        root = ET.parse(path).getroot()
        for package in root.findall(".//package"):
            if package.attrib.get("name") == "Plantry.Market":
                market_packages += 1
        for row in class_rows(path):
            filename = row.attrib.get("filename", "")
            if is_generated(filename):
                generated_rows.append(f"{path}: {filename}")
            if is_deal_normalizer(filename):
                deal_rows.append((path, row))
    if generated_rows:
        raise ValueError("generated regex rows remain in coverage output:\n" + "\n".join(generated_rows[:5]))
    if not deal_rows or not any(covered_lines(row) > 0 for _, row in deal_rows):
        raise ValueError("handwritten DealNormalizer.cs is missing or has no covered lines")
    if market_packages == 0:
        raise ValueError("Plantry.Market package is absent from coverage output")


def read_market_summary(path: Path) -> tuple[float, int, int]:
    data = json.loads(path.read_text(encoding="utf-8"))
    assemblies = data.get("coverage", {}).get("assemblies", [])
    market = next((item for item in assemblies if item.get("name") == "Plantry.Market"), None)
    if market is None:
        raise ValueError(f"Plantry.Market is absent from ReportGenerator summary: {path}")
    return float(market["coverage"]), int(market["coveredlines"]), int(market["coverablelines"])


def validate_fixtures() -> None:
    evidence = Path(__file__).with_name("market-coverage-evidence")
    baseline = class_rows(evidence / "baseline.cobertura.xml")
    post = class_rows(evidence / "post.cobertura.xml")
    if not any(is_generated(row.attrib.get("filename", "")) for row in baseline):
        raise ValueError("baseline fixture does not document the generated row")
    if any(is_generated(row.attrib.get("filename", "")) for row in post):
        raise ValueError("post fixture still contains a generated row")
    if not any(is_deal_normalizer(row.attrib.get("filename", "")) for row in baseline + post):
        raise ValueError("fixtures do not retain DealNormalizer.cs")
    for name in ("baseline-Summary.json", "post-Summary.json"):
        json.loads((evidence / name).read_text(encoding="utf-8"))


def self_test() -> None:
    validate_runsettings()
    validate_fixtures()
    assert is_generated("obj/Debug/net10.0/System.Text.RegularExpressions.Generator/RegexGenerator.g.cs")
    assert not is_generated("src/Plantry.Market/Domain/Deals/DealNormalizer.cs")
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "coverage.cobertura.xml"
        path.write_text(
            '<coverage><packages><package name="Plantry.Market"><classes>'
            '<class filename="src/Plantry.Market/Domain/Deals/DealNormalizer.cs">'
            '<methods><method><lines><line hits="1"/></lines></method></methods></class>'
            '</classes></package></packages></coverage>',
            encoding="utf-8",
        )
        validate_cobertura([path])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--summary", type=Path, help="ReportGenerator Summary.json")
    parser.add_argument("--cobertura", type=Path, action="append", help="Cobertura file or glob (repeatable)")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        if args.self_test:
            self_test()
            print("Market coverage denominator self-test passed.")
            return 0
        if args.summary is None or not args.cobertura:
            parser.error("provide --summary and at least one --cobertura, or use --self-test")
        paths = []
        for candidate in args.cobertura:
            paths.extend(sorted(Path().glob(str(candidate))) if any(char in str(candidate) for char in "*?[") else [candidate])
        validate_runsettings()
        validate_cobertura(paths)
        coverage, covered, total = read_market_summary(args.summary)
        print(f"Market coverage denominator verified: {coverage:.1f}% ({covered}/{total}); generated regex excluded; DealNormalizer retained.")
        return 0
    except (AssertionError, OSError, ET.ParseError, ValueError, json.JSONDecodeError) as error:
        print(f"Market coverage verification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
