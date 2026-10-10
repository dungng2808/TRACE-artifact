#!/usr/bin/env python3
"""Build a real, auditable Defects4J campaign candidate manifest on Windows.

The script deliberately selects one LIBRO generated test per Defects4J bug,
checks out fixed/buggy Git revisions from the locally staged official bare
repositories, and materializes LIBRO's TestAutoGen fixture in both workspaces.
It only selects Maven/Gradle revisions because these are the build systems the
RQ2 workbench can execute and measure today.  Preflight remains the authority
that decides which reconstructed candidates are actually READY.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import re
import shutil
import subprocess
from collections import Counter, defaultdict
from pathlib import Path


REPOS = {
    "Chart": "jfreechart.git", "Cli": "commons-cli.git", "Closure": "closure-compiler.git",
    "Codec": "commons-codec.git", "Collections": "commons-collections.git",
    "Compress": "commons-compress.git", "Csv": "commons-csv.git", "Gson": "gson.git",
    "JacksonCore": "jackson-core.git", "JacksonDatabind": "jackson-databind.git",
    "JacksonXml": "jackson-dataformat-xml.git", "Jsoup": "jsoup.git",
    "JxPath": "commons-jxpath.git", "Lang": "commons-lang.git", "Math": "commons-math.git",
    "Mockito": "mockito.git", "Time": "joda-time.git",
}


def run(*args: str, cwd: Path | None = None) -> str:
    process = subprocess.run(args, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if process.returncode:
        raise RuntimeError(f"Command failed ({process.returncode}): {' '.join(args)}\n{process.stdout[-1000:]}\n{process.stderr[-1000:]}")
    return process.stdout


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--libro-root", type=Path, required=True)
    parser.add_argument("--defects4j-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--java-home", type=Path, required=True, help="Portable JDK used only by generated Defects4J tasks.")
    parser.add_argument("--target", type=int, default=110, help="Oversampled reconstruction target; preflight will filter it.")
    parser.add_argument("--seed", type=int, default=20260814)
    parser.add_argument("--projects", default="", help="Optional comma-separated Defects4J projects for a bounded pilot.")
    return parser.parse_args()


def stable(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def project_layout(project: str, bug: int) -> tuple[str, str]:
    source = {
        "Chart": "source", "Closure": "src", "JxPath": "src/java", "Mockito": "src", "Time": "src/main/java",
        "Collections": "src/main/java", "Compress": "src/main/java", "Csv": "src/main/java", "Gson": "gson/src/main/java",
        "JacksonCore": "src/main/java", "JacksonDatabind": "src/main/java", "JacksonXml": "src/main/java", "Jsoup": "src/main/java",
    }.get(project)
    tests = {
        "Chart": "tests", "Closure": "test", "JxPath": "src/test", "Mockito": "test", "Time": "src/test/java",
        "Collections": "src/test/java", "Compress": "src/test/java", "Csv": "src/test/java", "Gson": "gson/src/test/java",
        "JacksonCore": "src/test/java", "JacksonDatabind": "src/test/java", "JacksonXml": "src/test/java", "Jsoup": "src/test/java",
    }.get(project)
    if project == "Lang": source, tests = ("src/main/java", "src/test/java") if bug <= 35 else ("src/java", "src/test")
    if project == "Math": source, tests = ("src/main/java", "src/test/java") if bug <= 84 else ("src/java", "src/test")
    if project == "Cli": source, tests = ("src/java", "src/test") if bug <= 29 else ("src/main/java", "src/test/java")
    if project == "Codec": source, tests = ("src/java", "src/test") if bug <= 10 else ("src/main/java", "src/test/java")
    if not source or not tests:
        raise ValueError(f"Unsupported Defects4J layout: {project}-{bug}")
    return source, tests


def active_bugs(root: Path) -> dict[str, dict]:
    result: dict[str, dict] = {}
    for directory in (root / "framework" / "projects").iterdir():
        file = directory / "active-bugs.csv"
        if not file.is_file() or directory.name not in REPOS:
            continue
        with file.open(newline="", encoding="utf-8") as stream:
            for row in csv.DictReader(stream):
                if not re.fullmatch(r"[0-9a-fA-F]{40}", row["revision.id.buggy"].strip()) or not re.fullmatch(r"[0-9a-fA-F]{40}", row["revision.id.fixed"].strip()):
                    continue
                key = f"{directory.name}_{row['bug.id']}"
                result[key] = {"project": directory.name, "bug": int(row["bug.id"]), "buggy": row["revision.id.buggy"], "fixed": row["revision.id.fixed"], "url": row["report.url"]}
    return result


SUPPORTED_PROJECTS = {
    "Cli", "Codec", "Collections", "Compress", "Csv", "Gson", "JacksonCore",
    "JacksonDatabind", "JacksonXml", "Jsoup", "Lang", "Math", "Mockito", "Time"
}


def expected_build_system(project: str) -> str:
    return "gradle" if project == "Mockito" else "maven"


def choose_candidates(libro: Path, d4j: Path, target: int, seed: int, allowed_projects: set[str]) -> list[dict]:
    results = json.loads((libro / "results" / "example2_n50.json").read_text(encoding="utf-8"))
    metadata = active_bugs(d4j)
    candidates: list[dict] = []
    for bug_id, tests in results.items():
        meta = metadata.get(bug_id)
        if not meta or not isinstance(tests, dict): continue
        for artifact, result in tests.items():
            fixed = result.get("fixed") if isinstance(result, dict) else None
            if not isinstance(fixed, dict) or result.get("success") is not False: continue
            if not (fixed.get("compile_error") or fixed.get("runtime_error") or fixed.get("autogen_failed")): continue
            test_path = libro / "data" / "Defects4J" / "gen_tests" / artifact
            if not test_path.is_file(): continue
            if meta["project"] not in SUPPORTED_PROJECTS or (allowed_projects and meta["project"] not in allowed_projects): continue
            system = expected_build_system(meta["project"])
            category = "fixed-compile-error" if fixed.get("compile_error") else "fixed-runtime-error" if fixed.get("runtime_error") else "fixed-test-failure"
            candidates.append({**meta, "bug_id": bug_id, "artifact": artifact, "test_path": test_path, "build_system": system, "category": category})
            break

    selected, counts = [], defaultdict(int)
    while candidates and len(selected) < target:
        candidate = min(candidates, key=lambda row: (counts[row["project"]], stable(f"{seed}|{row['project']}|{row['bug_id']}")))
        selected.append(candidate)
        counts[candidate["project"]] += 1
        candidates.remove(candidate)
    return selected


def method_name(content: str) -> str:
    match = re.search(r"\b(?:public|protected|private)?\s*(?:static\s+)?(?:void|[A-Za-z_$][\w.$<>\[\]]*)\s+(test[\w$]+)\s*\(", content)
    if not match: raise ValueError("Generated test method could not be identified.")
    return match.group(1)


def imports_and_package(source_root: Path, test_root: Path, snippet: str, project: str) -> tuple[str, list[str]]:
    names = set(re.findall(r"\b[A-Z][A-Za-z0-9_$]*\b", snippet))
    imports: list[str] = []
    for name in sorted(names):
        matches = list(source_root.rglob(name + ".java")) if source_root.exists() else []
        if len(matches) == 1:
            relative = matches[0].relative_to(source_root).with_suffix("")
            imports.append(".".join(relative.parts))
    preferred = project.lower().replace("jackson", "jackson")
    packages = [".".join(item.split(".")[:-1]) for item in imports if preferred in item.lower()]
    existing = [package for package in packages if (test_root / Path(*package.split("."))).is_dir()]
    package = Counter(existing).most_common(1)[0][0] if existing else ""
    return package, sorted(set(imports))


def fixture(source_root: Path, test_root: Path, snippet: str, project: str, skeleton: bool) -> tuple[Path, str, str, str]:
    package, imports = imports_and_package(source_root, test_root, snippet, project)
    method = method_name(snippet)
    lines = ["/* Added by RQ2 Defects4J campaign intake. */"]
    if package: lines.extend([f"package {package};", ""])
    lines.extend(["import junit.framework.TestCase;"])
    lines.extend(f"import {item};" for item in imports if not package or not item.startswith(package + "."))
    lines.extend(["", "public class TestAutoGen extends TestCase {", "}" if skeleton else snippet.strip(), "}"])
    if skeleton: lines[-2:] = ["}"]
    target = test_root / Path(*package.split(".")) / "TestAutoGen.java" if package else test_root / "TestAutoGen.java"
    return target, "\n".join(lines) + "\n", method, package


def focal_files(root: Path, source_root: Path, project_root: Path, project: str, bug: int) -> list[str]:
    file = root / "framework" / "projects" / project / "modified_classes" / f"{bug}.src"
    if not file.is_file(): return []
    result = []
    for class_name in file.read_text(encoding="utf-8", errors="ignore").splitlines():
        path = source_root / Path(*class_name.strip().split(".")).with_suffix(".java")
        if path.is_file(): result.append(path.relative_to(project_root).as_posix())
    return result


def checkout(repo: Path, path: Path, revision: str) -> None:
    run("git", f"--git-dir={repo}", "worktree", "add", "--detach", str(path), revision)


def main() -> int:
    args = parse_args()
    libro, d4j, output, java_home = args.libro_root.resolve(), args.defects4j_root.resolve(), args.output.resolve(), args.java_home.resolve()
    if output.exists(): raise FileExistsError(f"Refusing to overwrite output: {output}")
    if not (java_home / "bin" / "java.exe").is_file(): raise FileNotFoundError(f"Java executable not found under {java_home}")
    allowed_projects = {item.strip() for item in args.projects.split(",") if item.strip()}
    selected = choose_candidates(libro, d4j, args.target, args.seed, allowed_projects)
    if len(selected) < args.target: raise RuntimeError(f"Only {len(selected)} Maven/Gradle candidates are available.")
    output.mkdir(parents=True)
    tasks, audit = [], []
    for index, row in enumerate(selected, 1):
        fixed, buggy = output / "r" / f"{index:03d}f", output / "r" / f"{index:03d}b"
        detail, status = "", "ERROR"
        try:
            repository = d4j / "project_repos" / REPOS[row["project"]]
            checkout(repository, buggy, row["buggy"]); checkout(repository, fixed, row["fixed"])
            source_rel, test_rel = project_layout(row["project"], row["bug"])
            fixed_source, fixed_tests = fixed / source_rel, fixed / test_rel
            buggy_source, buggy_tests = buggy / source_rel, buggy / test_rel
            snippet = row["test_path"].read_text(encoding="utf-8", errors="strict")
            fixed_file, fixed_content, method, package = fixture(fixed_source, fixed_tests, snippet, row["project"], False)
            buggy_file, buggy_content, _, _ = fixture(buggy_source, buggy_tests, snippet, row["project"], True)
            fixed_file.parent.mkdir(parents=True, exist_ok=True); buggy_file.parent.mkdir(parents=True, exist_ok=True)
            fixed_file.write_text(fixed_content, encoding="utf-8"); buggy_file.write_text(buggy_content, encoding="utf-8")
            report = json.loads((libro / "data" / "Defects4J" / "bug_report" / f"{row['project']}-{row['bug']}.json").read_text(encoding="utf-8"))
            focal = focal_files(d4j, fixed_source, fixed, row["project"], row["bug"])
            if not focal: raise RuntimeError("No focal production source could be resolved from Defects4J metadata.")
            test_rel_path = fixed_file.relative_to(fixed).as_posix()
            test_class = (package + "." if package else "") + "TestAutoGen"
            tasks.append({"taskId": f"D4J-{row['project'].upper()}-{row['bug']}-{row['artifact'].rsplit('_n', 1)[-1].replace('.txt','')}", "projectId": row["project"], "datasetId": "LIBRO-DEFECTS4J", "artifactId": row["artifact"], "independenceUnitId": f"{row['project']}#{row['bug']}", "samplingStratum": row["category"], "sourceUri": row["url"], "fixedRevision": row["fixed"], "buggyRevision": row["buggy"], "repositoryPath": fixed.relative_to(output).as_posix(), "buggyRepositoryPath": buggy.relative_to(output).as_posix(), "scenarioId": f"{row['project']}-{row['bug']}-scenario", "testId": row["artifact"], "failingTestMethod": method, "scenarioSpecification": (str(report.get("title", "")) + "\n\n" + str(report.get("description", ""))).strip(), "traceabilityLink": f"{row['project']}-{row['bug']}-scenario -> {row['artifact']}", "initialEvidence": f"LIBRO recorded {row['category']} for {row['artifact']} on the fixed revision.", "testFilePath": test_rel_path, "testClassName": test_class, "modulePath": "", "buildSystem": row["build_system"], "focalSourceFiles": focal, "previouslyVerifiedTests": [], "coverageReportPath": "target/site/jacoco/jacoco.xml", "mutationReportPath": "target/pit-reports/mutations.xml", "javaHome": str(java_home)})
            status, detail = "RECONSTRUCTED", "Git revisions checked out; TestAutoGen fixture materialized in fixed and clean buggy workspaces."
        except Exception as exc:
            detail = f"{type(exc).__name__}: {exc}"
        audit.append({"selection_order": index, "bug_id": row["bug_id"], "project": row["project"], "artifact": row["artifact"], "build_system": row["build_system"], "status": status, "detail": detail})
        print(f"[{index}/{len(selected)}] {row['bug_id']}: {status}")
    manifest = {"studyDesign": {"studyId": "RQ2-LIBRO-DEFECTS4J-100-CANDIDATE", "samplingFrame": "Deterministic one-generated-test-per-Defects4J-bug reconstruction frame.", "selectionMethod": f"Balanced Maven/Gradle selection with seed {args.seed}; preflight decides READY.", "selectionSeed": args.seed, "inclusionCriteria": ["LIBRO generated test failed on fixed revision.", "Fixed/buggy Defects4J revisions and a focal source are reconstructed."], "exclusionCriteria": ["Generated test cannot be materialized.", "Build or buggy baseline fails preflight."], "powerPlan": {"familyWiseAlpha": 0.05, "targetPower": 0.8, "alternativeWinProbability": 0.7, "expectedDiscordantUnitRate": 0.4, "comparisonCount": 2, "minimumProjects": 5, "plannedIndependentUnits": len(tasks), "provisional": True}, "outcomePlan": {"contractVersion": "RQ2-OUTCOME-PLAN-1", "successNonInferiorityMargin": 0.05, "coverageNonInferiorityMargin": 0.05, "mutationNonInferiorityMargin": 0.05, "provisional": True}}, "tasks": tasks}
    (output / "benchmark.candidate.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    with (output / "reconstruction-audit.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(audit[0])); writer.writeheader(); writer.writerows(audit)
    print(f"Reconstructed {len(tasks)}/{len(selected)} candidates. Manifest: {output / 'benchmark.candidate.json'}")
    return 0 if len(tasks) == len(selected) else 2


if __name__ == "__main__": raise SystemExit(main())
