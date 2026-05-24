#!/usr/bin/env python3
"""
Prune and annotate dotnet_diagnostic severity entries in .editorconfig.

1. Removes entries that match a known baseline severity (idempotent).
2. Adds an inline comment on kept overrides when the baseline is known:
   ``dotnet_diagnostic.CA1001.severity = error # Recommended default: warning``
   ``dotnet_diagnostic.MA0004.severity = none # Meziantou default: warning``

Baselines come from:
  - .NET SDK ``AnalysisMode`` globalconfig (CA / IDE rules), labeled ``Recommended default``
  - Meziantou.Analyzer shipped ``configuration/*.editorconfig``, labeled ``Meziantou default``

Third-party analyzers without shipped defaults (Roslynator, IDisposableAnalyzers,
VSTHRD, etc.) are not annotated. Wrong ``Recommended default: none`` comments on
those rules are removed.

Usage:
  python3 scripts/prune-editorconfig-analysis-defaults.py              # dry-run
  python3 scripts/prune-editorconfig-analysis-defaults.py --write    # apply
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

DIAGNOSTIC_LINE_RE = re.compile(
    r"^(\s*dotnet_diagnostic\.(?P<id>[A-Za-z0-9]+)\.severity\s*=\s*)"
    r"(?P<severity>[^\s#]+)"
    r"(?P<suffix>.*)$",
    re.IGNORECASE,
)

SECTION_SEPARATOR_RE = re.compile(r"^#\s*={5,}\s*$")

# Comments this script adds (standalone or inline).
INLINE_BASELINE_COMMENT_RE = re.compile(
    r"(?:Recommended default|Meziantou default):\s*(?P<severity>\S+)",
    re.IGNORECASE,
)

STANDALONE_BASELINE_COMMENT_RE = re.compile(
    r"^\s*#\s*(?:Recommended default|Meziantou default):\s*\S+\s*$",
    re.IGNORECASE,
)

MEZIANTOU_CONFIG_FILES = {
    "none": "none.editorconfig",
    "default": "default.editorconfig",
    "all-suggestions": "all-suggestions.editorconfig",
    "all-warnings": "all-warnings.editorconfig",
    "all-errors": "all-errors.editorconfig",
}


@dataclass(frozen=True)
class RuleBaseline:
    severity: str
    tag_label: str  # e.g. "Recommended default" or "Meziantou default"


@dataclass
class DefaultCatalog:
    """Known rule baselines and which config files contributed them."""

    rules: dict[str, RuleBaseline]
    sources: list[str]

    def get(self, rule_id: str) -> RuleBaseline | None:
        return self.rules.get(rule_id.upper())


def find_sdk_path(explicit: str | None) -> Path:
    if explicit:
        return Path(explicit).expanduser().resolve()
    version = subprocess.check_output(["dotnet", "--version"], text=True).strip()
    return Path.home() / ".dotnet" / "sdk" / version


def find_repo_root(start: Path) -> Path:
    path = start.resolve()
    for parent in [path, *path.parents]:
        if (parent / "Directory.Build.props").is_file():
            return parent
    return path.parent


def find_nuget_packages_root() -> Path:
    try:
        output = subprocess.check_output(
            ["dotnet", "nuget", "locals", "global-packages", "-l"],
            text=True,
        )
        for line in output.splitlines():
            if line.startswith("global-packages:"):
                return Path(line.split(":", 1)[1].strip())
    except subprocess.CalledProcessError:
        pass
    return Path.home() / ".nuget" / "packages"


def parse_msbuild_property(repo_root: Path, name: str) -> str | None:
    for relative in ("Directory.Build.props", "Directory.Packages.props"):
        path = repo_root / relative
        if not path.is_file():
            continue
        match = re.search(
            rf"<{re.escape(name)}>\s*([^<]+?)\s*</{re.escape(name)}>",
            path.read_text(encoding="utf-8"),
            re.IGNORECASE,
        )
        if match:
            return match.group(1).strip()
    return None


def parse_central_package_version(repo_root: Path, package_id: str) -> str | None:
    path = repo_root / "Directory.Packages.props"
    if not path.is_file():
        return None
    match = re.search(
        rf'<PackageVersion\s+Include="{re.escape(package_id)}"\s+Version="([^"]+)"',
        path.read_text(encoding="utf-8"),
        re.IGNORECASE,
    )
    return match.group(1).strip() if match else None


def globalconfig_paths(
    sdk: Path,
    analysis_level: str,
    analysis_mode: str,
    include_style: bool,
) -> list[Path]:
    analyzers_dir = sdk / "Sdks/Microsoft.NET.Sdk/analyzers/build/config"
    codestyle_dir = sdk / "Sdks/Microsoft.NET.Sdk/codestyle/cs/build/config"
    mode = analysis_mode.lower()

    paths: list[Path] = [
        analyzers_dir / f"analysislevel_{analysis_level}_{mode}.globalconfig",
    ]
    if include_style:
        paths.append(codestyle_dir / f"analysislevelstyle_{mode}.globalconfig")

    missing = [p for p in paths if not p.is_file()]
    if missing:
        msg = "\n".join(f"  - {p}" for p in missing)
        raise FileNotFoundError(
            f"Could not find SDK globalconfig file(s). Check --sdk-path, --analysis-level, "
            f"and --analysis-mode.\n{msg}"
        )
    return paths


def parse_diagnostic_severities(path: Path) -> dict[str, str]:
    severities: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = DIAGNOSTIC_LINE_RE.match(line)
        if match:
            severities[match.group("id").upper()] = match.group("severity").lower()
    return severities


def load_meziantou_baseline(
    nuget_root: Path,
    repo_root: Path,
) -> tuple[dict[str, str], str | None]:
    version = parse_central_package_version(repo_root, "Meziantou.Analyzer")
    if not version:
        return {}, None

    mode = parse_msbuild_property(repo_root, "MeziantouAnalysisMode")
    if mode is None:
        config_name = MEZIANTOU_CONFIG_FILES["default"]
        effective_mode = "Default (per-rule; no MeziantouAnalysisMode set)"
    else:
        config_name = MEZIANTOU_CONFIG_FILES.get(mode.lower())
        if config_name is None:
            return {}, f"unknown MeziantouAnalysisMode '{mode}'"
        effective_mode = mode

    config_path = (
        nuget_root / "meziantou.analyzer" / version / "configuration" / config_name
    )
    if not config_path.is_file():
        return {}, f"Meziantou config not found: {config_path}"

    return parse_diagnostic_severities(config_path), f"{config_path} [{effective_mode}]"


def build_default_catalog(
    sdk: Path,
    analysis_level: str,
    analysis_mode: str,
    include_style: bool,
    nuget_root: Path,
    repo_root: Path,
) -> tuple[DefaultCatalog, list[Path]]:
    config_paths = globalconfig_paths(sdk, analysis_level, analysis_mode, include_style)
    rules: dict[str, RuleBaseline] = {}
    sources: list[str] = []

    for path in config_paths:
        for rule_id, severity in parse_diagnostic_severities(path).items():
            rules[rule_id] = RuleBaseline(severity, "Recommended default")
        sources.append(str(path))

    meziantou_severities, meziantou_source = load_meziantou_baseline(nuget_root, repo_root)
    if meziantou_source:
        sources.append(meziantou_source)
    if meziantou_source and meziantou_source.startswith("unknown"):
        print(f"warning: {meziantou_source}", file=sys.stderr)
    for rule_id, severity in meziantou_severities.items():
        rules[rule_id] = RuleBaseline(severity, "Meziantou default")

    return DefaultCatalog(rules=rules, sources=sources), config_paths


def baseline_tag(baseline: RuleBaseline) -> str:
    return f"{baseline.tag_label}: {baseline.severity}"


def is_standalone_baseline_comment(line: str) -> bool:
    return STANDALONE_BASELINE_COMMENT_RE.match(line) is not None


def is_section_separator(line: str) -> bool:
    stripped = line.strip()
    if not stripped.startswith("#"):
        return False
    if SECTION_SEPARATOR_RE.match(stripped):
        return True
    inner = stripped.lstrip("#").strip()
    if inner and set(inner) <= {"=", " "}:
        return True
    return False


def comment_references_other_rule(comment_line: str, rule_id: str) -> bool:
    match = re.search(r"#\s*((?:CA|IDE|CS|SYSLIB|IL)\d+)\s*:", comment_line, re.IGNORECASE)
    if not match:
        return False
    return match.group(1).upper() != rule_id.upper()


def next_non_blank(lines: list[str], start: int) -> tuple[int | None, str | None]:
    for j in range(start, len(lines)):
        if lines[j].strip():
            return j, lines[j]
    return None, None


def parse_inline_comment(suffix: str) -> tuple[str, str | None]:
    """Return (user_comment_text, baseline severity from our tag, if any)."""
    if not suffix.strip().startswith("#"):
        return "", None

    comment = suffix.strip()[1:].strip()
    match = INLINE_BASELINE_COMMENT_RE.search(comment)
    if not match:
        return comment, None

    baseline_severity = match.group("severity").lower()
    user_comment = INLINE_BASELINE_COMMENT_RE.sub("", comment)
    user_comment = re.sub(r"^[;\s]+|[;\s]+$", "", user_comment)
    return user_comment, baseline_severity


def build_inline_suffix(user_comment: str, baseline: RuleBaseline | None) -> str:
    if baseline is None:
        if user_comment:
            return f"# {user_comment}"
        return ""
    tag = baseline_tag(baseline)
    if user_comment:
        return f"# {user_comment}; {tag}"
    return f"# {tag}"


def annotate_diagnostic_line(
    line: str,
    catalog: DefaultCatalog,
) -> tuple[str, str]:
    """Return (new_line, change_kind): '', 'inserted', 'updated', or 'cleared'."""
    match = DIAGNOSTIC_LINE_RE.match(line)
    if not match:
        return line, ""

    prefix = match.group(1)
    severity = match.group("severity")
    suffix = match.group("suffix")
    rule_id = match.group("id").upper()
    baseline = catalog.get(rule_id)

    user_comment, _existing_baseline = parse_inline_comment(suffix)
    new_suffix = build_inline_suffix(user_comment, baseline)
    new_line = f"{prefix}{severity}{f' {new_suffix}' if new_suffix else ''}"

    if line.rstrip() == new_line.rstrip():
        return line, ""

    if baseline is None:
        if _existing_baseline is not None:
            return new_line, "cleared"
        return line, ""

    if _existing_baseline is None and not suffix.strip():
        return new_line, "inserted"

    return new_line, "updated"


def indices_to_remove(lines: list[str], catalog: DefaultCatalog) -> set[int]:
    remove: set[int] = set()
    for i, line in enumerate(lines):
        match = DIAGNOSTIC_LINE_RE.match(line)
        if not match:
            continue
        rule_id = match.group("id").upper()
        severity = match.group("severity").lower()
        baseline = catalog.get(rule_id)
        if baseline is None or severity != baseline.severity:
            continue

        remove.add(i)
        j = i - 1
        while j >= 0:
            prev = lines[j]
            if prev.strip() == "":
                remove.add(j)
                j -= 1
                continue
            if is_section_separator(prev):
                break
            if is_standalone_baseline_comment(prev):
                remove.add(j)
                j -= 1
                continue
            if prev.strip().startswith("#"):
                if comment_references_other_rule(prev, rule_id):
                    break
                remove.add(j)
                j -= 1
                continue
            break

    return remove


def prune_lines(lines: list[str], remove: set[int]) -> list[str]:
    out = [line for i, line in enumerate(lines) if i not in remove]

    collapsed: list[str] = []
    blank_run = 0
    for line in out:
        if line.strip() == "":
            blank_run += 1
            if blank_run <= 2:
                collapsed.append(line)
        else:
            blank_run = 0
            collapsed.append(line)
    return collapsed


def annotate_kept_diagnostics(
    lines: list[str],
    catalog: DefaultCatalog,
) -> tuple[list[str], int, int, int]:
    result: list[str] = []
    inserted = 0
    updated = 0
    cleared = 0

    i = 0
    while i < len(lines):
        line = lines[i]

        if is_standalone_baseline_comment(line):
            next_idx, next_line = next_non_blank(lines, i + 1)
            if (
                next_idx is not None
                and next_line is not None
                and DIAGNOSTIC_LINE_RE.match(next_line)
            ):
                i += 1
                continue

        match = DIAGNOSTIC_LINE_RE.match(line)
        if not match:
            result.append(line)
            i += 1
            continue

        new_line, change = annotate_diagnostic_line(line, catalog)
        if change == "inserted":
            inserted += 1
        elif change == "updated":
            updated += 1
        elif change == "cleared":
            cleared += 1
        result.append(new_line)
        i += 1

    return result, inserted, updated, cleared


def transform(
    lines: list[str],
    catalog: DefaultCatalog,
) -> tuple[list[str], set[int], int, int, int]:
    remove = indices_to_remove(lines, catalog)
    pruned = prune_lines(lines, remove)
    annotated, inserted, updated, cleared = annotate_kept_diagnostics(pruned, catalog)
    return annotated, remove, inserted, updated, cleared


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--editorconfig",
        type=Path,
        default=Path(".editorconfig"),
        help="Path to .editorconfig to prune (default: .editorconfig)",
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=None,
        help="Repository root (default: parent of scripts/)",
    )
    parser.add_argument(
        "--sdk-path",
        type=str,
        default=None,
        help="Path to dotnet SDK folder (default: ~/.dotnet/sdk/<dotnet --version>)",
    )
    parser.add_argument(
        "--nuget-packages",
        type=Path,
        default=None,
        help="NuGet global-packages folder (default: from dotnet nuget locals)",
    )
    parser.add_argument(
        "--analysis-level",
        type=str,
        default="10",
        help="Effective analysis level major version (default: 10 for net10.0)",
    )
    parser.add_argument(
        "--analysis-mode",
        type=str,
        default="recommended",
        help="Analysis mode name (default: recommended)",
    )
    parser.add_argument(
        "--no-style",
        action="store_true",
        help="Do not include IDE codestyle globalconfig (analysislevelstyle_*)",
    )
    parser.add_argument(
        "--write",
        action="store_true",
        help="Write changes to --editorconfig (default: dry-run only)",
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="List every removed diagnostic rule id",
    )
    args = parser.parse_args()

    editorconfig = args.editorconfig.resolve()
    if not editorconfig.is_file():
        print(f"error: file not found: {editorconfig}", file=sys.stderr)
        return 1

    repo_root = (args.repo_root or find_repo_root(Path(__file__).parent)).resolve()
    nuget_root = (args.nuget_packages or find_nuget_packages_root()).resolve()

    try:
        sdk = find_sdk_path(args.sdk_path)
        catalog, sdk_config_paths = build_default_catalog(
            sdk,
            args.analysis_level.replace(".", "_").split("_")[0],
            args.analysis_mode,
            include_style=not args.no_style,
            nuget_root=nuget_root,
            repo_root=repo_root,
        )
    except (FileNotFoundError, subprocess.CalledProcessError) as ex:
        print(f"error: {ex}", file=sys.stderr)
        return 1

    raw = editorconfig.read_text(encoding="utf-8").splitlines()
    result, remove, inserted, updated, cleared = transform(raw, catalog)

    removed_rules: list[str] = []
    for i in sorted(remove):
        match = DIAGNOSTIC_LINE_RE.match(raw[i])
        if match:
            removed_rules.append(match.group("id").upper())

    changed = remove or inserted or updated or cleared or result != raw

    recommended_count = sum(
        1 for b in catalog.rules.values() if b.tag_label == "Recommended default"
    )
    meziantou_count = sum(
        1 for b in catalog.rules.values() if b.tag_label == "Meziantou default"
    )

    print(f"SDK: {sdk}")
    print(f"Repo: {repo_root}")
    print("Baselines loaded from:")
    for source in catalog.sources:
        print(f"  {source}")
    print(
        f"  ({len(catalog.rules)} rules: {recommended_count} Recommended, "
        f"{meziantou_count} Meziantou)"
    )
    print()
    print(f"Target: {editorconfig}")
    print(f"Lines to remove: {len(remove)} ({len(removed_rules)} redundant diagnostic entries)")
    print(f"Inline default comments to add: {inserted}")
    print(f"Inline default comments to update: {updated}")
    print(f"Incorrect baseline comments to clear: {cleared}")

    if removed_rules:
        if args.verbose:
            for rule_id in sorted(set(removed_rules)):
                print(f"  removed {rule_id}")
        else:
            preview = ", ".join(sorted(set(removed_rules))[:20])
            extra = len(set(removed_rules)) - 20
            if extra > 0:
                preview += f", ... (+{extra} more)"
            print(f"Removed rules: {preview}")
            print("Use --verbose for the full list.")

    if not changed:
        print("\nNo changes (already pruned and annotated).")
        return 0

    if not args.write:
        print("\nDry-run only. Re-run with --write to apply.")
        return 0

    text = "\n".join(result)
    if result and not text.endswith("\n"):
        text += "\n"
    editorconfig.write_text(text, encoding="utf-8", newline="\n")
    print(f"\nWrote {editorconfig} ({len(raw)} -> {len(result)} lines).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
