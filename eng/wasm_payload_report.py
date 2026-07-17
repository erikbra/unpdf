#!/usr/bin/env python3
"""Report and enforce the published browser WebAssembly payload budget."""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path
from typing import Any


FINGERPRINTED_ASSET = re.compile(
    r"^(?P<stem>.+)\.[a-z0-9]{8,16}(?P<extension>\.[^.]+)$"
)
SIDECAR_SUFFIXES = (".br", ".gz")


def logical_name(file_name: str) -> str:
    match = FINGERPRINTED_ASSET.match(file_name)
    if match is None:
        return file_name
    return f"{match.group('stem')}{match.group('extension')}"


def asset_kind(asset_name: str) -> str:
    if asset_name == "dotnet.native.wasm":
        return "native-runtime"
    if asset_name.endswith(".wasm"):
        return "managed-assembly"
    if asset_name.endswith(".js"):
        return "javascript"
    if asset_name.endswith(".dat"):
        return "globalization-data"
    return "other"


def collect_payload(publish_directory: Path) -> dict[str, Any]:
    framework_directory = publish_directory / "_framework"
    if not framework_directory.is_dir():
        raise ValueError(
            f"Published framework directory does not exist: {framework_directory}"
        )

    assets: list[dict[str, Any]] = []
    names_seen: set[str] = set()
    for path in sorted(framework_directory.iterdir()):
        if not path.is_file() or path.name.endswith(SIDECAR_SUFFIXES):
            continue

        brotli_path = path.with_name(f"{path.name}.br")
        if not brotli_path.is_file():
            raise ValueError(f"Published asset has no Brotli sidecar: {path}")

        name = logical_name(path.name)
        if name in names_seen:
            raise ValueError(f"Multiple published assets normalize to {name}")
        names_seen.add(name)
        assets.append(
            {
                "asset": name,
                "publishedPath": f"_framework/{path.name}",
                "kind": asset_kind(name),
                "rawBytes": path.stat().st_size,
                "brotliBytes": brotli_path.stat().st_size,
            }
        )

    if not assets:
        raise ValueError(f"No framework payload assets found in {framework_directory}")

    assets.sort(key=lambda asset: asset["asset"])
    return {
        "schemaVersion": 1,
        "scope": (
            "All uncompressed files in the published _framework directory; "
            "raw and precompressed Brotli transfer sizes are reported separately."
        ),
        "totals": {
            "assetCount": len(assets),
            "rawBytes": sum(asset["rawBytes"] for asset in assets),
            "brotliBytes": sum(asset["brotliBytes"] for asset in assets),
        },
        "assets": assets,
    }


def create_baseline(
    report: dict[str, Any],
    growth_percent: float,
    minimum_growth_bytes: int,
) -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "description": (
            "Ratcheted browser payload baseline. Update this file only when payload "
            "growth is understood and accepted; reduce it when durable savings land."
        ),
        "growthAllowance": {
            "percent": growth_percent,
            "minimumBytes": minimum_growth_bytes,
        },
        "totals": {
            "rawBytes": report["totals"]["rawBytes"],
            "brotliBytes": report["totals"]["brotliBytes"],
        },
        "assets": {
            asset["asset"]: {
                "rawBytes": asset["rawBytes"],
                "brotliBytes": asset["brotliBytes"],
            }
            for asset in report["assets"]
        },
    }


def allowed_size(
    baseline_bytes: int,
    growth_percent: float,
    minimum_growth_bytes: int,
) -> int:
    percentage_limit = math.ceil(baseline_bytes * (1 + growth_percent / 100))
    return max(percentage_limit, baseline_bytes + minimum_growth_bytes)


def compare_with_baseline(
    report: dict[str, Any], baseline: dict[str, Any]
) -> dict[str, Any]:
    if baseline.get("schemaVersion") != 1:
        raise ValueError("Unsupported payload baseline schemaVersion")

    allowance = baseline.get("growthAllowance", {})
    growth_percent = float(allowance.get("percent", 0))
    minimum_growth_bytes = int(allowance.get("minimumBytes", 0))
    if growth_percent < 0 or minimum_growth_bytes < 0:
        raise ValueError("Payload growth allowances cannot be negative")

    violations: list[str] = []
    current_assets = {asset["asset"]: asset for asset in report["assets"]}
    baseline_assets = baseline.get("assets", {})

    for name in sorted(current_assets.keys() - baseline_assets.keys()):
        violations.append(
            f"New payload asset {name} is not explained by the checked-in baseline"
        )

    for name in sorted(current_assets.keys() & baseline_assets.keys()):
        current = current_assets[name]
        expected = baseline_assets[name]
        for metric, label in (
            ("rawBytes", "raw"),
            ("brotliBytes", "Brotli"),
        ):
            maximum = allowed_size(
                int(expected[metric]), growth_percent, minimum_growth_bytes
            )
            if current[metric] > maximum:
                violations.append(
                    f"{name} {label} size is {current[metric]:,} bytes; "
                    f"the ratcheted limit is {maximum:,} bytes"
                )

    for metric, label in (("rawBytes", "Total raw"), ("brotliBytes", "Total Brotli")):
        expected = int(baseline["totals"][metric])
        maximum = allowed_size(expected, growth_percent, minimum_growth_bytes)
        actual = int(report["totals"][metric])
        if actual > maximum:
            violations.append(
                f"{label} payload is {actual:,} bytes; "
                f"the ratcheted limit is {maximum:,} bytes"
            )

    removed_assets = sorted(baseline_assets.keys() - current_assets.keys())
    return {
        "status": "failed" if violations else "passed",
        "growthAllowance": {
            "percent": growth_percent,
            "minimumBytes": minimum_growth_bytes,
        },
        "violations": violations,
        "removedAssets": removed_assets,
        "ratchetSuggested": bool(removed_assets)
        or report["totals"]["rawBytes"] < baseline["totals"]["rawBytes"]
        or report["totals"]["brotliBytes"] < baseline["totals"]["brotliBytes"],
    }


def format_bytes(value: int) -> str:
    if value >= 1024 * 1024:
        return f"{value / (1024 * 1024):.2f} MiB"
    if value >= 1024:
        return f"{value / 1024:.2f} KiB"
    return f"{value} B"


def markdown_report(report: dict[str, Any]) -> str:
    totals = report["totals"]
    budget = report.get("budget")
    lines = [
        "# Browser WASM payload",
        "",
        (
            f"Published framework payload: **{format_bytes(totals['rawBytes'])} raw** / "
            f"**{format_bytes(totals['brotliBytes'])} Brotli** across "
            f"**{totals['assetCount']} assets**."
        ),
        "",
    ]
    if budget is not None:
        lines.extend(
            [
                f"Ratcheted baseline: **{budget['status'].upper()}**.",
                "",
            ]
        )
        if budget["ratchetSuggested"] and not budget["violations"]:
            lines.extend(
                [
                    (
                        "The payload is below the checked-in baseline; consider "
                        "ratcheting the baseline down."
                    ),
                    "",
                ]
            )
        for violation in budget["violations"]:
            lines.append(f"- ❌ {violation}")
        if budget["violations"]:
            lines.append("")

    lines.extend(
        [
            "| Asset | Kind | Raw | Brotli |",
            "| --- | --- | ---: | ---: |",
        ]
    )
    for asset in sorted(
        report["assets"], key=lambda item: item["brotliBytes"], reverse=True
    ):
        lines.append(
            f"| `{asset['asset']}` | {asset['kind']} | "
            f"{format_bytes(asset['rawBytes'])} | "
            f"{format_bytes(asset['brotliBytes'])} |"
        )
    lines.append("")
    return "\n".join(lines)


def write_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--publish-directory",
        required=True,
        type=Path,
        help="Published application wwwroot directory",
    )
    parser.add_argument(
        "--baseline",
        required=True,
        type=Path,
        help="Checked-in ratcheted payload baseline",
    )
    parser.add_argument("--json", type=Path, help="Machine-readable report path")
    parser.add_argument("--markdown", type=Path, help="Human-readable report path")
    parser.add_argument(
        "--update-baseline",
        action="store_true",
        help="Replace the baseline with the current payload instead of checking it",
    )
    parser.add_argument(
        "--growth-percent",
        default=1.0,
        type=float,
        help="Allowed percentage growth when updating the baseline (default: 1)",
    )
    parser.add_argument(
        "--minimum-growth-bytes",
        default=1024,
        type=int,
        help="Minimum per-comparison noise allowance (default: 1024)",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        report = collect_payload(args.publish_directory)
        if args.update_baseline:
            if args.growth_percent < 0 or args.minimum_growth_bytes < 0:
                raise ValueError("Payload growth allowances cannot be negative")
            baseline = create_baseline(
                report, args.growth_percent, args.minimum_growth_bytes
            )
            write_json(args.baseline, baseline)
            report["budget"] = {
                "status": "baseline-updated",
                "growthAllowance": baseline["growthAllowance"],
                "violations": [],
                "removedAssets": [],
                "ratchetSuggested": False,
            }
        else:
            if not args.baseline.is_file():
                raise ValueError(f"Payload baseline does not exist: {args.baseline}")
            baseline = json.loads(args.baseline.read_text(encoding="utf-8"))
            report["budget"] = compare_with_baseline(report, baseline)

        if args.json is not None:
            write_json(args.json, report)
        markdown = markdown_report(report)
        if args.markdown is not None:
            args.markdown.parent.mkdir(parents=True, exist_ok=True)
            args.markdown.write_text(markdown, encoding="utf-8")
        print(markdown, end="")

        for violation in report["budget"]["violations"]:
            print(f"error: {violation}", file=sys.stderr)
        return 1 if report["budget"]["violations"] else 0
    except (KeyError, OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
