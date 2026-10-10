#!/usr/bin/env python3
"""Sanitize reconstructed Defects4J candidates before expensive preflight.

Unsupported historical build layouts are excluded with an audit entry.  When a
Defects4J revision moves its test root, the buggy workspace receives the same
clean TestAutoGen skeleton path as the fixed workspace; the repair executor
later copies the repaired source to this exact path for the buggy oracle.
"""
from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--audit", type=Path, required=True)
    args = parser.parse_args()
    candidate = args.candidate.resolve()
    root = candidate.parent
    payload = json.loads(candidate.read_text(encoding="utf-8"))
    kept, audit = [], []
    for task in payload["tasks"]:
        fixed = (root / task["repositoryPath"]).resolve()
        buggy = (root / task["buggyRepositoryPath"]).resolve()
        build = task["buildSystem"].lower()
        build_names = ["pom.xml"] if build == "maven" else ["build.gradle", "build.gradle.kts"]
        if not any((fixed / name).is_file() and (buggy / name).is_file() for name in build_names):
            audit.append({"task_id": task["taskId"], "action": "EXCLUDED", "detail": f"Unsupported or inconsistent {build} build layout."})
            continue
        fixed_test = fixed / task["testFilePath"]
        buggy_test = buggy / task["testFilePath"]
        if not fixed_test.is_file():
            audit.append({"task_id": task["taskId"], "action": "EXCLUDED", "detail": "Fixed TestAutoGen fixture is missing."})
            continue
        if not buggy_test.is_file():
            package = task["testClassName"].rsplit(".", 1)[0] if "." in task["testClassName"] else ""
            lines = ["/* Added by RQ2 Defects4J campaign sanitizer. */"]
            if package: lines.extend([f"package {package};", ""])
            lines.extend(["import junit.framework.TestCase;", "", "public class TestAutoGen extends TestCase {", "}", ""])
            buggy_test.parent.mkdir(parents=True, exist_ok=True)
            buggy_test.write_text("\n".join(lines), encoding="utf-8")
            audit.append({"task_id": task["taskId"], "action": "SKELETON_SYNCED", "detail": "Created clean buggy TestAutoGen fixture at fixed-relative path."})
        else:
            audit.append({"task_id": task["taskId"], "action": "KEPT", "detail": "Build layout and paired fixture paths are present."})
        kept.append(task)
    payload["tasks"] = kept
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    with args.audit.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=["task_id", "action", "detail"])
        writer.writeheader(); writer.writerows(audit)
    print(f"Sanitized {len(kept)}/{len(audit)} task(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
