#!/usr/bin/env python3
"""Freeze a runnable manifest from a candidate manifest and preflight readiness CSV."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from collections import defaultdict
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--readiness", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--target", type=int, default=0, help="Freeze exactly this many READY independent tasks; default retains legacy 10-20 pilot gate.")
    parser.add_argument("--seed", type=int, default=20260814)
    args = parser.parse_args()

    candidate = args.candidate.resolve()
    readiness = args.readiness.resolve()
    output = args.output.resolve()
    if output.exists():
        raise FileExistsError(f"Refusing to overwrite frozen manifest: {output}")

    with readiness.open("r", encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    ready_ids = {row["task_id"] for row in rows if row["ready"].upper() == "TRUE"}
    document = json.loads(candidate.read_text(encoding="utf-8"))
    tasks = [task for task in document["tasks"] if task["taskId"] in ready_ids]
    if len(tasks) != len(ready_ids):
        raise RuntimeError("Readiness CSV and candidate manifest task identities do not match.")
    if args.target:
        if args.target <= 0 or len(tasks) < args.target:
            raise RuntimeError(f"Campaign gate failed: requested {args.target} READY tasks, observed {len(tasks)}.")
        selected, selected_ids, project_counts = [], set(), defaultdict(int)
        while len(selected) < args.target:
            next_task = min(
                (task for task in tasks if task["taskId"] not in selected_ids),
                key=lambda task: (
                    project_counts[task["projectId"]],
                    hashlib.sha256(f"{args.seed}|{task['projectId']}|{task['independenceUnitId']}".encode("utf-8")).hexdigest()))
            selected.append(next_task)
            selected_ids.add(next_task["taskId"])
            project_counts[next_task["projectId"]] += 1
        tasks = selected
    projects = {task["projectId"] for task in tasks}
    if not args.target and not 10 <= len(tasks) <= 20:
        raise RuntimeError(f"Pilot gate failed: expected 10-20 ready tasks, observed {len(tasks)}.")
    if len(projects) < 5:
        raise RuntimeError(f"Pilot gate failed: expected at least 5 projects, observed {len(projects)}.")
    study = document["studyDesign"]
    study["studyId"] = "RQ2-LIBRO-DEFECTS4J-READY-CAMPAIGN" if args.target else "RQ2-LIBRO-GHRB-READY-PILOT"
    study["samplingFrame"] += " Frozen to tasks passing preflight."
    study["powerPlan"]["plannedIndependentUnits"] = len(tasks)
    document["tasks"] = tasks
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, ensure_ascii=False, indent=2), encoding="utf-8")
    provenance = {
        "contractVersion": "RQ2-READY-MANIFEST-1",
        "candidateManifest": str(candidate),
        "candidateManifestSha256": sha256(candidate),
        "readinessCsv": str(readiness),
        "readinessCsvSha256": sha256(readiness),
        "readyManifest": str(output),
        "readyManifestSha256": sha256(output),
        "readyTasks": len(tasks),
        "independentUnits": len({task["independenceUnitId"] for task in tasks}),
        "projects": len(projects),
        "projectIds": sorted(projects),
        "selectionSeed": args.seed if args.target else None,
    }
    provenance_path = output.with_name(output.stem + ".provenance.json")
    provenance_path.write_text(json.dumps(provenance, indent=2), encoding="utf-8")
    print(f"Ready manifest frozen: {len(tasks)} tasks, {len(projects)} projects.")
    print(output)
    print(provenance_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
