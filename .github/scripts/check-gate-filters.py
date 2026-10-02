#!/usr/bin/env python3
"""Check that the CI Gate's path filters match the workflows they wait for.

ci-gate.yml waits for a path-filtered workflow only when the PR matches the
gate's own copy of that workflow's pull_request paths. If the two drift, the
gate waits for a check that never runs (and hangs the PR) or skips one that
runs and fails (#368). Each filter lists its workflow's own file, so this
finds the pairs itself and checks that:

- every filter lists exactly one .github/workflows/*.yml file, which exists;
- the filter is identical to that workflow's on.pull_request.paths, entry for
  entry and in the same order;
- every workflow with on.pull_request.paths has a filter.

Usage: python3 .github/scripts/check-gate-filters.py [REPO_ROOT]
REPO_ROOT defaults to the repo this script is in. YAML is read with PyYAML,
or with yq (preinstalled on GitHub's ubuntu runners) when PyYAML is missing.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

GATE = ".github/workflows/ci-gate.yml"
FILTER_STEP_ID = "changes"
WORKFLOW_FILE = re.compile(r"^\.github/workflows/[^/]+\.ya?ml$")


def load_yaml_text(text):
    try:
        import yaml
    except ImportError:
        yaml = None
    if yaml is not None:
        return yaml.safe_load(text)
    if shutil.which("yq") is None:
        sys.exit("error: needs PyYAML or yq to read YAML")
    with tempfile.NamedTemporaryFile(
        "w", encoding="utf-8", suffix=".yml", delete=False
    ) as tmp:
        tmp.write(text)
    try:
        out = subprocess.run(
            ["yq", "-o=json", ".", tmp.name],
            capture_output=True, text=True, check=True,
        ).stdout
    finally:
        os.unlink(tmp.name)
    return json.loads(out)


def triggers(workflow):
    # PyYAML reads the key `on` as the boolean True (YAML 1.1); yq keeps "on".
    return workflow.get("on", workflow.get(True)) or {}


def pr_paths(workflow):
    on = triggers(workflow)
    if not isinstance(on, dict) or not isinstance(on.get("pull_request"), dict):
        return None
    return on["pull_request"].get("paths")


def flatten(entries):
    # dorny/paths-filter flattens lists that YAML anchors nest.
    flat = []
    for entry in entries or []:
        flat.extend(flatten(entry) if isinstance(entry, list) else [entry])
    return flat


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parents[2])
    errors = []

    def error(message):
        errors.append(message)
        if os.environ.get("GITHUB_ACTIONS") == "true":
            print(f"::error file={GATE},title=CI Gate filters::{message}")
        else:
            print(f"error: {message}")

    gate = load_yaml_text((root / GATE).read_text(encoding="utf-8"))
    steps = [s for s in gate["jobs"]["gate"]["steps"] if s.get("id") == FILTER_STEP_ID]
    if len(steps) != 1:
        sys.exit(f"error: {GATE} has no single step with id '{FILTER_STEP_ID}'")
    filters = load_yaml_text(steps[0]["with"]["filters"])

    covered = set()
    for name, entries in filters.items():
        entries = flatten(entries)
        own = [e for e in entries if isinstance(e, str) and WORKFLOW_FILE.match(e)]
        if len(own) != 1:
            error(f"filter '{name}' must list exactly one workflow file, lists {own or 'none'}")
            continue
        path = root / own[0]
        if not path.is_file():
            error(f"filter '{name}' lists {own[0]}, which doesn't exist")
            continue
        covered.add(own[0])
        paths = pr_paths(load_yaml_text(path.read_text(encoding="utf-8")))
        if paths is None:
            error(f"filter '{name}': {own[0]} has no on.pull_request.paths to match")
        elif entries != paths:
            error(
                f"filter '{name}' differs from on.pull_request.paths in {own[0]}: "
                f"gate has {entries}, workflow has {paths}"
            )
        else:
            print(f"ok: {name} = {own[0]} ({len(paths)} paths)")

    for path in sorted((root / ".github/workflows").glob("*.y*ml")):
        rel = path.relative_to(root).as_posix()
        if rel in covered or rel == GATE:
            continue
        if pr_paths(load_yaml_text(path.read_text(encoding="utf-8"))) is not None:
            error(f"{rel} runs on pull_request with paths but has no filter in {GATE}")

    if errors:
        print(f"{len(errors)} problem(s). Keep each filter in {GATE} identical to its "
              "workflow's on.pull_request.paths.")
        return 1
    print(f"All {len(covered)} gate filters match their workflows.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
