#!/usr/bin/env python3
"""Compile-check and partition every project in the prepared SF110 subset."""

from __future__ import annotations

import csv
import os
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
READY_ROOT = ROOT / "datasets/rq1/sf110/java-ready"
PROJECTS = READY_ROOT / "projects"
REJECTED_PROJECTS = READY_ROOT / "rejected-projects"
OUTPUT = ROOT / "datasets/rq1/sf110/validation/compile-jdk17"
MAVEN = ROOT / "Maven/apache-maven-3.9.16/bin/mvn"
JAVA_HOME = ROOT / "JDK/macos-temurin-17/Contents/Home"


def validate(project: Path) -> tuple[str, str, int, float]:
    import time
    started = time.monotonic()
    environment = os.environ.copy()
    environment["JAVA_HOME"] = str(JAVA_HOME)
    environment["PATH"] = str(JAVA_HOME / "bin") + os.pathsep + environment.get("PATH", "")
    try:
        result = subprocess.run(
            [str(MAVEN), "-B", "-ntp", "-f", str(project / "pom.xml"), "clean", "test-compile"],
            cwd=ROOT, env=environment, text=True, capture_output=True, timeout=300,
        )
        status = "PASS" if result.returncode == 0 else "FAIL"
        output = result.stdout + "\n--- STDERR ---\n" + result.stderr
        code = result.returncode
    except subprocess.TimeoutExpired as error:
        status, code = "TIMEOUT", 124
        output = (error.stdout or "") + "\n--- STDERR ---\n" + (error.stderr or "")
    (OUTPUT / "logs" / f"{project.name}.log").write_text(output, errors="replace")
    return project.name, status, code, time.monotonic() - started


def discover_projects() -> dict[str, Path]:
    """Return the complete prepared cohort, including previously rejected rows."""
    discovered: dict[str, Path] = {}
    for parent in (PROJECTS, REJECTED_PROJECTS):
        if not parent.exists():
            continue
        for pom in parent.glob("*/pom.xml"):
            project = pom.parent
            if project.name in discovered:
                raise RuntimeError(f"Duplicate prepared project: {project.name}")
            discovered[project.name] = project
    return discovered


def partition_projects(results: list[tuple[str, str, int, float]], sources: dict[str, Path]) -> None:
    """Keep buildable projects in projects/ and quarantine the remaining cohort."""
    PROJECTS.mkdir(parents=True, exist_ok=True)
    REJECTED_PROJECTS.mkdir(parents=True, exist_ok=True)
    for project_id, status, _, _ in results:
        source = sources[project_id]
        destination_root = PROJECTS if status == "PASS" else REJECTED_PROJECTS
        destination = destination_root / project_id
        if source == destination:
            continue
        if destination.exists():
            raise RuntimeError(f"Refusing to overwrite existing project directory: {destination}")
        shutil.move(str(source), str(destination))


def main() -> None:
    (OUTPUT / "logs").mkdir(parents=True, exist_ok=True)
    sources = discover_projects()
    projects = [sources[project_id] for project_id in sorted(sources)]
    if not projects:
        raise SystemExit("No prepared SF110 projects were found.")
    results = []
    with ThreadPoolExecutor(max_workers=3) as executor:
        futures = {executor.submit(validate, project): project for project in projects}
        for future in as_completed(futures):
            row = future.result()
            results.append(row)
            print(f"[{len(results)}/{len(projects)}] {row[0]} {row[1]} ({row[3]:.1f}s)", flush=True)
    results.sort()
    with (OUTPUT / "results.csv").open("w", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["project_id", "status", "exit_code", "duration_seconds"])
        writer.writerows((project, status, code, f"{duration:.3f}")
                         for project, status, code, duration in results)
    passed = sum(status == "PASS" for _, status, _, _ in results)
    partition_projects(results, sources)
    incomplete = any(status == "TIMEOUT" for _, status, _, _ in results)
    print(f"compile_pass={passed}/{len(results)} excluded={len(results) - passed}")
    raise SystemExit(2 if incomplete or passed == 0 else 0)


if __name__ == "__main__":
    main()
