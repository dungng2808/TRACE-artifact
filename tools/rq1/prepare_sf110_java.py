#!/usr/bin/env python3
"""Prepare a reproducible SF110 subset containing 5 pilot + 150 official methods.

The project set is the union of projects reached by the deterministic RQ1
method sampler on the complete, parse-clean SF110 candidate pool. Production
sources are copied byte-for-byte; only a Maven evaluation adapter is added.
"""

from __future__ import annotations

import csv
import hashlib
import json
import re
import shutil
from pathlib import Path
from xml.sax.saxutils import escape


ROOT = Path(__file__).resolve().parents[2]
SOURCE_ROOT = ROOT / "datasets/rq1/sf110/SF110-20130704-src"
ANALYSIS_ROOT = ROOT / "datasets/rq1/sf110/analysis"
READY_ROOT = ROOT / "datasets/rq1/sf110/java-ready"
SEED = 4_902_026
PILOT_COUNT = 5
OFFICIAL_COUNT = 150
MAX_PER_PROJECT = 3
RESERVE_PROJECT_COUNT = 20
STRATA = ("LOW", "MEDIUM", "HIGH")


def stable_score(seed: int, key: str) -> str:
    return hashlib.sha256(f"{seed}|{key}".encode()).hexdigest()


def select(candidates: list[dict], count: int, seed: int, excluded: set[str]) -> list[dict]:
    queues = {
        stratum: sorted(
            (c for c in candidates
             if c["complexityStratum"] == stratum and c["candidateKey"] not in excluded),
            key=lambda c: stable_score(seed, c["candidateKey"]),
        )
        for stratum in STRATA
    }
    positions = {stratum: 0 for stratum in STRATA}
    project_counts: dict[str, int] = {}
    selected: list[dict] = []
    while len(selected) < count:
        made_progress = False
        for stratum in STRATA:
            queue = queues[stratum]
            while positions[stratum] < len(queue):
                candidate = queue[positions[stratum]]
                positions[stratum] += 1
                project_id = candidate["projectId"]
                if project_counts.get(project_id, 0) >= MAX_PER_PROJECT:
                    continue
                selected.append(candidate)
                project_counts[project_id] = project_counts.get(project_id, 0) + 1
                made_progress = True
                break
            if len(selected) == count:
                break
        if not made_progress:
            break
    return selected


def parse_error_projects() -> set[str]:
    projects: set[str] = set()
    error_file = ANALYSIS_ROOT / "candidate-parse-errors.txt"
    if not error_file.exists():
        return projects
    for line in error_file.read_text(errors="replace").splitlines():
        if "\t" in line and not line[:1].isspace():
            projects.add(line.split("\t", 1)[0])
    return projects


def load_candidates() -> list[dict]:
    excluded_projects = parse_error_projects()
    candidates: list[dict] = []
    with (ANALYSIS_ROOT / "candidate-pool.raw.jsonl").open() as stream:
        for line in stream:
            candidate = json.loads(line)
            if candidate["eligible"] and candidate["projectId"] not in excluded_projects:
                candidates.append(candidate)
    return candidates


def dependency_xml(project: Path) -> str:
    dependencies: list[str] = []
    for index, jar in enumerate(sorted(
            list((project / "lib").glob("**/*.jar"))
            + list((project / "test-lib").glob("**/*.jar")))):
        relative = jar.relative_to(project).as_posix()
        name = re.sub(r"[^A-Za-z0-9_.-]", "-", jar.stem).strip("-.") or f"jar-{index}"
        dependencies.append(f"""
    <dependency>
      <groupId>sf110.local</groupId><artifactId>{escape(name)}-{index}</artifactId><version>1</version>
      <scope>system</scope><systemPath>${{project.basedir}}/{escape(relative)}</systemPath>
    </dependency>""")
    return "".join(dependencies)


def pom_xml(project_id: str, project: Path) -> str:
    artifact = re.sub(r"[^A-Za-z0-9_.-]", "-", project_id)
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<project xmlns="http://maven.apache.org/POM/4.0.0"
         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
         xsi:schemaLocation="http://maven.apache.org/POM/4.0.0 https://maven.apache.org/xsd/maven-4.0.0.xsd">
  <modelVersion>4.0.0</modelVersion>
  <groupId>rq1.sf110</groupId><artifactId>{artifact}</artifactId><version>1.0</version>
  <properties>
    <project.build.sourceEncoding>ISO-8859-1</project.build.sourceEncoding>
    <maven.compiler.release>8</maven.compiler.release>
  </properties>
  <dependencies>{dependency_xml(project)}
    <dependency><groupId>junit</groupId><artifactId>junit</artifactId><version>4.13.2</version><scope>test</scope></dependency>
    <dependency><groupId>org.junit.jupiter</groupId><artifactId>junit-jupiter</artifactId><version>5.10.2</version><scope>test</scope></dependency>
    <dependency><groupId>org.mockito</groupId><artifactId>mockito-core</artifactId><version>5.12.0</version><scope>test</scope></dependency>
  </dependencies>
  <build><plugins>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-compiler-plugin</artifactId><version>3.15.0</version></plugin>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-surefire-plugin</artifactId><version>3.5.5</version></plugin>
    <plugin><groupId>org.jacoco</groupId><artifactId>jacoco-maven-plugin</artifactId><version>0.8.15</version></plugin>
    <plugin><groupId>org.pitest</groupId><artifactId>pitest-maven</artifactId><version>1.25.3</version>
      <dependencies><dependency><groupId>org.pitest</groupId><artifactId>pitest-junit5-plugin</artifactId><version>1.2.3</version></dependency></dependencies>
    </plugin>
  </plugins></build>
</project>
"""


def main() -> None:
    candidates = load_candidates()
    pilot = select(candidates, PILOT_COUNT, SEED + 1, set())
    pilot_keys = {candidate["candidateKey"] for candidate in pilot}
    official = select(candidates, OFFICIAL_COUNT, SEED, pilot_keys)
    if len(pilot) != PILOT_COUNT or len(official) != OFFICIAL_COUNT:
        raise SystemExit(f"Insufficient pool: pilot={len(pilot)}, official={len(official)}")

    primary_projects = {c["projectId"] for c in pilot + official}
    eligible_counts: dict[str, int] = {}
    for candidate in candidates:
        project_id = candidate["projectId"]
        eligible_counts[project_id] = eligible_counts.get(project_id, 0) + 1
    reserve_projects = sorted(
        (project_id for project_id, count in eligible_counts.items()
         if count >= MAX_PER_PROJECT and project_id not in primary_projects),
        key=lambda project_id: stable_score(SEED + 2, project_id),
    )[:RESERVE_PROJECT_COUNT]
    selected_projects = sorted(primary_projects | set(reserve_projects))
    staging = READY_ROOT.with_name("java-ready.tmp")
    if staging.exists():
        shutil.rmtree(staging)
    projects_root = staging / "projects"
    projects_root.mkdir(parents=True)
    ignored = shutil.ignore_patterns("evosuite-tests", "evosuite-files", "build", "target", ".git")
    for project_id in selected_projects:
        source = SOURCE_ROOT / project_id
        destination = projects_root / project_id
        shutil.copytree(source, destination, ignore=ignored)
        (destination / "pom.xml").write_text(pom_xml(project_id, destination))

    with (staging / "prepared-projects.csv").open("w", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["project_id", "repository_path", "java_file_count"])
        for project_id in selected_projects:
            project = projects_root / project_id
            writer.writerow([project_id, f"projects/{project_id}", len(list(project.glob("src/main/java/**/*.java")))])
    (staging / "REVISION").write_text("SF110-20130704\n")
    (staging / "prepared-projects.txt").write_text("\n".join(selected_projects) + "\n")

    if READY_ROOT.exists():
        backup = READY_ROOT.with_name("java-ready.previous")
        if backup.exists():
            shutil.rmtree(backup)
        READY_ROOT.rename(backup)
    staging.rename(READY_ROOT)
    print(f"eligible_pool={len(candidates)} pilot={len(pilot)} official={len(official)} "
          f"primary_projects={len(primary_projects)} reserve_projects={len(reserve_projects)} "
          f"prepared_projects={len(selected_projects)}")


if __name__ == "__main__":
    main()
