#!/usr/bin/env python3
"""
Build the solution and group compiler/analyzer warnings by diagnostic code.

Output is structured for AI-assisted fixes: one warning type per commit, with
large groups split by project when they exceed --split-threshold.

Usage:
  python3 scripts/group-build-warnings.py
  python3 scripts/group-build-warnings.py --from-log build.log
  python3 scripts/group-build-warnings.py --configuration Debug --split-threshold 10
  python3 scripts/group-build-warnings.py --exclude CS1591

Outputs (under artifacts/warnings/ by default):
  build.log              Parsed build output (console or MSBuild file log)
  msbuild.log            Raw MSBuild file log from dotnet build
  summary.json           Full structured report
  summary.md             Overview and suggested commit batches
  by-type/{CODE}.json    One file per warning code
  by-type/{CODE}.md      AI-ready markdown for that code
  by-type/{CODE}/by-project/{Project}.md   Per-project splits for large groups
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from collections import defaultdict
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from pathlib import Path


MSBUILD_PREFIX_RE = re.compile(r"^\s*\d+>")


WARNING_LINE_RE = re.compile(
    r"^(?P<file>[^\s(]+)\((?P<line>\d+)(?:,(?P<column>\d+))?\):\s+"
    r"warning\s+(?P<code>[A-Z]+\d+):\s+"
    r"(?P<message>.+?)(?:\s+\[(?P<project>[^\]]+)\])?$"
)

DEFAULT_SPLIT_THRESHOLD = 15


@dataclass(frozen=True)
class Warning:
    code: str
    file: str
    line: int
    column: int | None
    message: str
    project: str

    @property
    def location(self) -> str:
        if self.column is None:
            return f"{self.file}:{self.line}"
        return f"{self.file}:{self.line}:{self.column}"

    @property
    def dedupe_key(self) -> tuple[str, str, int, int | None, str]:
        return (self.code, self.file, self.line, self.column, self.message)


@dataclass
class WarningGroup:
    code: str
    warnings: list[Warning] = field(default_factory=list)

    @property
    def count(self) -> int:
        return len(self.warnings)

    def by_project(self) -> dict[str, list[Warning]]:
        grouped: dict[str, list[Warning]] = defaultdict(list)
        for warning in self.warnings:
            grouped[warning.project].append(warning)
        return dict(sorted(grouped.items(), key=lambda item: (-len(item[1]), item[0])))

    def sample_message(self) -> str:
        return self.warnings[0].message if self.warnings else ""


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def normalize_path(path: str, root: Path) -> str:
    resolved = Path(path).resolve()
    try:
        return resolved.relative_to(root).as_posix()
    except ValueError:
        return path.replace("\\", "/")


def project_name(project_path: str) -> str:
    return Path(project_path).stem


def parse_warning_line(line: str, root: Path) -> Warning | None:
    line = MSBUILD_PREFIX_RE.sub("", line.strip())
    match = WARNING_LINE_RE.match(line)
    if match is None:
        return None

    groups = match.groupdict()
    return Warning(
        code=groups["code"],
        file=normalize_path(groups["file"], root),
        line=int(groups["line"]),
        column=int(groups["column"]) if groups["column"] else None,
        message=groups["message"].strip(),
        project=project_name(groups["project"]) if groups["project"] else "unknown",
    )


def parse_build_log(text: str, root: Path) -> list[Warning]:
    seen: set[tuple[str, str, int, int | None, str]] = set()
    warnings: list[Warning] = []

    for line in text.splitlines():
        warning = parse_warning_line(line, root)
        if warning is None:
            continue
        if warning.dedupe_key in seen:
            continue
        seen.add(warning.dedupe_key)
        warnings.append(warning)

    return warnings


def run_build(
    root: Path,
    *,
    configuration: str,
    no_restore: bool,
    incremental: bool,
    msbuild_log: Path,
) -> tuple[str, int]:
    solution = root / "Ratatoskr.slnx"
    if not solution.exists():
        raise SystemExit(f"Solution not found: {solution}")

    command = [
        "dotnet",
        "build",
        str(solution),
        "--configuration",
        configuration,
        "-consoleLoggerParameters:NoSummary;ForceNoAlign",
        f"/flp:logfile={msbuild_log};verbosity=normal",
    ]
    if not incremental:
        command.append("--no-incremental")
    if no_restore:
        command.append("--no-restore")

    msbuild_log.parent.mkdir(parents=True, exist_ok=True)
    if msbuild_log.exists():
        msbuild_log.unlink()

    result = subprocess.run(
        command,
        cwd=root,
        capture_output=True,
        text=True,
        check=False,
    )
    output = result.stdout + result.stderr
    if msbuild_log.exists():
        file_log = msbuild_log.read_text(encoding="utf-8", errors="replace")
        if file_log.count("warning ") > output.count("warning "):
            output = file_log
    return output, result.returncode


FULL_CODE_RE = re.compile(r"^[A-Z]+\d+$")


def parse_excludes(values: list[str]) -> tuple[list[str], list[str]]:
    codes: list[str] = []
    prefixes: list[str] = []
    for value in values:
        item = value.upper()
        if FULL_CODE_RE.match(item):
            codes.append(item)
        else:
            prefixes.append(item)
    return codes, prefixes


def should_exclude(code: str, exclude_prefixes: list[str], exclude_codes: list[str]) -> bool:
    if code in exclude_codes:
        return True
    return any(code.startswith(prefix) for prefix in exclude_prefixes)


def group_warnings(warnings: list[Warning]) -> dict[str, WarningGroup]:
    groups: dict[str, WarningGroup] = {}
    for warning in warnings:
        if warning.code not in groups:
            groups[warning.code] = WarningGroup(code=warning.code)
        groups[warning.code].warnings.append(warning)

    for group in groups.values():
        group.warnings.sort(key=lambda w: (w.file, w.line, w.column or 0, w.message))

    return dict(sorted(groups.items(), key=lambda item: (-item[1].count, item[0])))


def format_warning_line(warning: Warning) -> str:
    return f"  {warning.location} [{warning.project}] - {warning.message}"


def write_group_markdown(
    path: Path,
    group: WarningGroup,
    *,
    title_suffix: str = "",
    warnings: list[Warning] | None = None,
) -> None:
    items = warnings if warnings is not None else group.warnings
    lines = [
        f"# {group.code}{title_suffix}",
        "",
        f"**Count:** {len(items)}",
        "",
        "**Sample message:**",
        f"> {group.sample_message()}",
        "",
        "## Locations",
        "",
    ]
    lines.extend(format_warning_line(w) for w in items)
    lines.append("")
    path.write_text("\n".join(lines), encoding="utf-8")


def write_outputs(
    *,
    root: Path,
    output_dir: Path,
    warnings: list[Warning],
    groups: dict[str, WarningGroup],
    split_threshold: int,
    build_exit_code: int | None,
    configuration: str,
    source: str,
) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    by_type_dir = output_dir / "by-type"
    by_type_dir.mkdir(parents=True, exist_ok=True)

    suggested_batches: list[dict[str, object]] = []

    for code, group in groups.items():
        group_json = {
            "code": code,
            "count": group.count,
            "sample_message": group.sample_message(),
            "warnings": [asdict(w) for w in group.warnings],
            "by_project": {
                project: [asdict(w) for w in project_warnings]
                for project, project_warnings in group.by_project().items()
            },
        }

        json_path = by_type_dir / f"{code}.json"
        json_path.write_text(json.dumps(group_json, indent=2), encoding="utf-8")
        write_group_markdown(by_type_dir / f"{code}.md", group)

        if group.count > split_threshold:
            project_dir = by_type_dir / code / "by-project"
            project_dir.mkdir(parents=True, exist_ok=True)
            batch: dict[str, object] = {
                "code": code,
                "count": group.count,
                "strategy": "split_by_project",
                "commits": [],
            }
            for project, project_warnings in group.by_project().items():
                project_md = project_dir / f"{project}.md"
                write_group_markdown(
                    project_md,
                    group,
                    title_suffix=f" in {project}",
                    warnings=project_warnings,
                )
                batch["commits"].append(
                    {
                        "project": project,
                        "count": len(project_warnings),
                        "prompt_file": project_md.relative_to(root).as_posix(),
                        "suggested_commit_message": f"fix({project}): resolve {code} warnings",
                    }
                )
            suggested_batches.append(batch)
        else:
            suggested_batches.append(
                {
                    "code": code,
                    "count": group.count,
                    "strategy": "single_commit",
                    "prompt_file": (by_type_dir / f"{code}.md").relative_to(root).as_posix(),
                    "suggested_commit_message": f"fix: resolve {code} warnings",
                }
            )

    summary = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "source": source,
        "configuration": configuration,
        "build_exit_code": build_exit_code,
        "total_warnings": len(warnings),
        "warning_codes": len(groups),
        "split_threshold": split_threshold,
        "suggested_batches": suggested_batches,
        "counts_by_code": {code: group.count for code, group in groups.items()},
        "counts_by_project": dict(
            sorted(
                (
                    (project, sum(1 for w in warnings if w.project == project))
                    for project in {w.project for w in warnings}
                ),
                key=lambda item: (-item[1], item[0]),
            )
        ),
    }
    (output_dir / "summary.json").write_text(
        json.dumps(summary, indent=2),
        encoding="utf-8",
    )

    md_lines = [
        "# Build warnings summary",
        "",
        f"- Generated: {summary['generated_at']}",
        f"- Source: {source}",
        f"- Configuration: {configuration}",
        f"- Total warnings: {len(warnings)} across {len(groups)} codes",
        f"- Split threshold: {split_threshold} (groups above this are split by project)",
        "",
        "## Counts by code",
        "",
        "| Code | Count | Strategy |",
        "| --- | ---: | --- |",
    ]

    for batch in suggested_batches:
        code = str(batch["code"])
        count = int(batch["count"])
        if batch["strategy"] == "single_commit":
            prompt = batch["prompt_file"]
            md_lines.append(f"| {code} | {count} | single commit -> `{prompt}` |")
        else:
            md_lines.append(f"| {code} | {count} | split by project (see below) |")

    md_lines.extend(["", "## Suggested fix batches", ""])

    for index, batch in enumerate(suggested_batches, start=1):
        code = batch["code"]
        count = batch["count"]
        md_lines.append(f"### Batch {index}: {code} ({count} warnings)")
        if batch["strategy"] == "single_commit":
            md_lines.append(f"- Commit: `{batch['suggested_commit_message']}`")
            md_lines.append(f"- Prompt file: `{batch['prompt_file']}`")
        else:
            md_lines.append("- Strategy: one commit per project")
            for commit in batch["commits"]:
                md_lines.append(
                    f"  - `{commit['suggested_commit_message']}` "
                    f"({commit['count']} warnings) -> `{commit['prompt_file']}`"
                )
        md_lines.append("")

    md_lines.extend(["## All warnings by code", ""])

    for code, group in groups.items():
        md_lines.append(f"=== {code} ({group.count} warnings) ===")
        md_lines.append(f"Sample: {group.sample_message()}")
        md_lines.extend(format_warning_line(w) for w in group.warnings)
        md_lines.append("")

    (output_dir / "summary.md").write_text("\n".join(md_lines), encoding="utf-8")


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Build the solution and group warnings by diagnostic code.",
    )
    parser.add_argument(
        "--from-log",
        metavar="PATH",
        help="Parse an existing build log instead of running dotnet build.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=None,
        help="Output directory (default: artifacts/warnings).",
    )
    parser.add_argument(
        "--configuration",
        default="Release",
        help="dotnet build configuration (default: Release).",
    )
    parser.add_argument(
        "--incremental",
        action="store_true",
        help="Allow incremental build (faster, but may report 0 warnings if nothing recompiled).",
    )
    parser.add_argument(
        "--no-restore",
        action="store_true",
        help="Pass --no-restore to dotnet build.",
    )
    parser.add_argument(
        "--split-threshold",
        type=int,
        default=DEFAULT_SPLIT_THRESHOLD,
        help=f"Split warning groups by project when count exceeds this (default: {DEFAULT_SPLIT_THRESHOLD}).",
    )
    parser.add_argument(
        "--exclude",
        action="append",
        default=[],
        metavar="CODE_OR_PREFIX",
        help="Exclude warning codes or prefixes (e.g. CS1591 or CS). Repeatable.",
    )
    parser.add_argument(
        "--keep-going-on-build-failure",
        action="store_true",
        help="Return exit code 0 even when dotnet build failed (warnings are still parsed).",
    )
    return parser


def main() -> int:
    parser = build_arg_parser()
    args = parser.parse_args()
    root = repo_root()
    output_dir = args.output_dir or (root / "artifacts" / "warnings")

    exclude_codes, exclude_prefixes = parse_excludes(args.exclude)

    build_exit_code: int | None = None
    if args.from_log:
        log_path = Path(args.from_log)
        if not log_path.is_absolute():
            log_path = root / log_path
        build_output = log_path.read_text(encoding="utf-8", errors="replace")
        source = log_path.relative_to(root).as_posix()
    else:
        msbuild_log = output_dir / "msbuild.log"
        build_output, build_exit_code = run_build(
            root,
            configuration=args.configuration,
            no_restore=args.no_restore,
            incremental=args.incremental,
            msbuild_log=msbuild_log,
        )
        output_dir.mkdir(parents=True, exist_ok=True)
        (output_dir / "build.log").write_text(build_output, encoding="utf-8")
        source = "dotnet build"

    all_warnings = parse_build_log(build_output, root)
    warnings = [
        w
        for w in all_warnings
        if not should_exclude(w.code, exclude_prefixes, exclude_codes)
    ]
    groups = group_warnings(warnings)

    write_outputs(
        root=root,
        output_dir=output_dir,
        warnings=warnings,
        groups=groups,
        split_threshold=args.split_threshold,
        build_exit_code=build_exit_code,
        configuration=args.configuration,
        source=source,
    )

    print(f"Parsed {len(all_warnings)} warnings ({len(warnings)} after exclusions).")
    print(f"Grouped into {len(groups)} codes.")
    print(f"Wrote report to {output_dir.relative_to(root).as_posix()}/")
    print(f"  summary.md")
    print(f"  summary.json")
    print(f"  by-type/{{CODE}}.md")

    if build_exit_code not in (None, 0):
        if args.keep_going_on_build_failure:
            print(
                f"dotnet build failed with exit code {build_exit_code}; "
                "warnings report was still generated.",
                file=sys.stderr,
            )
            return 0
        return build_exit_code
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
