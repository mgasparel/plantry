#!/usr/bin/env python3
"""Verify durable evidence for the Plantry.Market coverage denominator fix."""
from __future__ import annotations
import argparse, json, re, sys, xml.etree.ElementTree as ET
from pathlib import Path
EXPECTED_BASELINE = (2282, 2817)
EXPECTED_POST = (1456, 1519)
GENERATED_RE = re.compile(r"(?:^|[\\/])obj(?:[\\/]).*System\.Text\.RegularExpressions\.Generator(?:[\\/]).*\.g\.cs$", re.I)
DEAL_RE = re.compile(r"(?:^|[\\/])src[\\/]Plantry\.Market[\\/]Domain[\\/]Deals[\\/]DealNormalizer\.cs$", re.I)
def percent(pair):
    covered, total = pair
    return int(covered * 1000 / total) / 10
def read_summary(path: Path):
    data = json.loads(path.read_text(encoding="utf-8"))
    node = data.get("summary", data)
    if isinstance(node, list): node = node[0]
    covered = int(node.get("covered", node.get("coveredLines", -1)))
    total = int(node.get("total", node.get("totalLines", -1)))
    if covered < 0 or total <= 0: raise ValueError(f"summary has no covered/total line counts: {path}")
    return covered, total
def filenames(path: Path):
    root = ET.parse(path).getroot()
    return [node.attrib.get("filename", "") for node in root.findall(".//class")]
def verify(baseline, post, baseline_cov: Path, post_cov: Path):
    assert baseline == EXPECTED_BASELINE, f"baseline changed: {baseline}"
    assert post == EXPECTED_POST, f"post-fix changed: {post}"
    assert percent(baseline) == 81.0 and percent(post) == 95.8
    before, after = filenames(baseline_cov), filenames(post_cov)
    assert any(GENERATED_RE.search(name) for name in before), "baseline lacks actual generated regex path"
    assert not any(GENERATED_RE.search(name) for name in after), "generated regex row remains after exclusion"
    assert any(DEAL_RE.search(name.replace("\\", "/")) for name in before), "baseline omitted DealNormalizer.cs"
    assert any(DEAL_RE.search(name.replace("\\", "/")) for name in after), "post-fix omitted DealNormalizer.cs"
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline-summary", type=Path)
    parser.add_argument("--post-summary", type=Path)
    parser.add_argument("--baseline-cobertura", type=Path)
    parser.add_argument("--post-cobertura", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            b, p = Path(d)/"before.xml", Path(d)/"after.xml"
            b.write_text('<coverage><class filename="obj/Debug/net10.0/System.Text.RegularExpressions.Generator/RegexGenerator.g.cs"/><class filename="src/Plantry.Market/Domain/Deals/DealNormalizer.cs"/></coverage>')
            p.write_text('<coverage><class filename="src/Plantry.Market/Domain/Deals/DealNormalizer.cs"/></coverage>')
            verify(EXPECTED_BASELINE, EXPECTED_POST, b, p)
    elif all((args.baseline_summary, args.post_summary, args.baseline_cobertura, args.post_cobertura)):
        verify(read_summary(args.baseline_summary), read_summary(args.post_summary), args.baseline_cobertura, args.post_cobertura)
    else: parser.error("provide --self-test or all four coverage paths")
    print("Market coverage evidence verified: 81.0% (2282/2817) -> 95.8% (1456/1519); generated regex excluded; DealNormalizer retained.")
if __name__ == "__main__": sys.exit(main())