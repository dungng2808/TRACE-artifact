#!/usr/bin/env python3
"""Reconstruct a deterministic LIBRO/GHRB RQ2 pilot manifest on Windows.

The script consumes the frozen Phase 7.8 sampling frame. It creates detached
Git worktrees for the fixed and buggy revisions, synchronizes the fixed test
tree to the buggy revision as LIBRO does, injects the recorded generated test,
and emits a structurally runnable benchmark manifest plus reconstruction audit.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path


PROJECTS = {
    "assertj/assertj-core": ("assertj-core", "", "assertj"),
    "checkstyle/checkstyle": ("checkstyle", "", "checkstyle"),
    "FasterXML/jackson-databind": ("jackson-databind", "", "jackson.databind"),
    "google/gson": ("gson", "gson", "gson"),
    "Hakky54/sslcontext-kickstart": (
        "sslcontext-kickstart",
        "sslcontext-kickstart",
        "altindag.ssl",
    ),
    "jhy/jsoup": ("jsoup", "", "jsoup"),
}


def run(*args: str, cwd: Path | None = None) -> str:
    process = subprocess.run(
        list(args), cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace"
    )
    if process.returncode:
        raise RuntimeError(
            f"Command failed ({process.returncode}): {' '.join(args)}\n"
            f"{process.stdout[-2000:]}\n{process.stderr[-2000:]}"
        )
    return process.stdout.strip()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--frame", required=True, type=Path)
    parser.add_argument("--libro-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    return parser.parse_args()


def selected_rows(frame: Path) -> list[dict[str, str]]:
    with frame.open("r", encoding="utf-8-sig", newline="") as stream:
        rows = [row for row in csv.DictReader(stream) if row["selected"].upper() == "TRUE"]
    return sorted(rows, key=lambda row: int(row["selection_order"]))


def java_tree(root: Path):
    return root.rglob("*.java") if root.exists() else []


def resolve_imports(common, repo_module: Path, source_dir: str, generated: str, project_key: str):
    tree = common.parse_method(generated)
    relevant = (
        common.javalang.tree.VariableDeclaration,
        common.javalang.tree.MemberReference,
        common.javalang.tree.MethodInvocation,
        common.javalang.tree.ReferenceType,
        common.javalang.tree.ClassCreator,
    )
    exception_nodes = (
        common.javalang.tree.CatchClauseParameter,
        common.javalang.tree.MethodDeclaration,
    )
    stubs: set[str] = set()
    assertions: set[str] = set()
    for _, node in tree:
        if isinstance(node, exception_nodes):
            exception_types = node.types if isinstance(
                node, common.javalang.tree.CatchClauseParameter
            ) else node.throws
            for exception_type in exception_types or []:
                stubs.add(exception_type)
        if isinstance(node, relevant):
            if isinstance(node, common.javalang.tree.VariableDeclaration):
                stubs.add(node.type.name)
            elif isinstance(node, common.javalang.tree.ReferenceType):
                stubs.add(node.name)
            elif isinstance(node, common.javalang.tree.ClassCreator):
                stubs.add(node.type.name)
            elif isinstance(node, common.javalang.tree.MethodInvocation) and (
                "assert" in node.member or node.member == "fail"
            ):
                assertions.add(node.member)
            else:
                qualifier = getattr(node, "qualifier", None)
                if qualifier and qualifier[0].isupper():
                    stubs.add(qualifier.split(".")[0])

    source_root = repo_module / source_dir
    all_java = list(java_tree(repo_module))
    classpaths: list[str] = []
    for stub in sorted(stubs):
        matches = list(source_root.rglob(f"{stub}.java")) if source_root.exists() else []
        if len(matches) == 1:
            relative = matches[0].relative_to(source_root).with_suffix("")
            classpaths.append(".".join(relative.parts))
            continue
        pattern = re.compile(rf"^\s*import\s+(?:static\s+)?([^;]*\.{re.escape(stub)})\s*;", re.M)
        imported: list[str] = []
        for java_file in all_java:
            try:
                imported.extend(pattern.findall(java_file.read_text(encoding="utf-8", errors="ignore")))
            except OSError:
                continue
        if imported:
            classpaths.append(Counter(imported).most_common(1)[0][0])
        elif matches:
            relative = matches[0].relative_to(source_root).with_suffix("")
            classpaths.append(".".join(relative.parts))

    assertion_imports: list[str] = []
    for assertion in sorted(assertions):
        pattern = re.compile(
            rf"^\s*import\s+(static\s+[^;]*\.{re.escape(assertion)})\s*;", re.M
        )
        imported: list[str] = []
        for java_file in all_java:
            try:
                imported.extend(pattern.findall(java_file.read_text(encoding="utf-8", errors="ignore")))
            except OSError:
                continue
        if imported:
            assertion_imports.append(Counter(imported).most_common(1)[0][0])
        elif project_key in {"jsoup", "altindag.ssl", "checkstyle"}:
            assertion_imports.append(f"static org.junit.jupiter.api.Assertions.{assertion}")
        else:
            assertion_imports.append(f"static org.junit.Assert.{assertion}")
    return classpaths, assertion_imports, sorted(stubs)


def inject(common, repo_root: Path, module: str, row: dict[str, str], generated: str) -> str:
    repo_module = repo_root / module if module else repo_root
    test_prefix = Path("src/test/java")
    class_name = row["test_class_name"]
    relative_test = test_prefix.joinpath(*class_name.split(".")).with_suffix(".java")
    target = repo_module / relative_test
    if not target.is_file():
        raise FileNotFoundError(f"Recorded injection target does not exist: {target}")

    generated = generated.replace("Assert.fail", "fail").replace("Assert.assert", "assert")
    classpaths, assertion_imports, stubs = resolve_imports(
        common, repo_module, "src/main/java", generated, PROJECTS[row["project_id"]][2]
    )
    original = target.read_text(encoding="utf-8", errors="ignore")
    unhandled = common.derive_unhandled_imports(
        original, classpaths + assertion_imports, stubs
    )
    updated, updated_generated = common.inject_with_imports(
        class_name, original.splitlines(keepends=True), generated, unhandled
    )
    method = common.parse_method(updated_generated).name
    if method != row["failing_test_method"]:
        raise RuntimeError(
            f"Injected method mismatch: expected {row['failing_test_method']}, got {method}"
        )
    target.write_text(updated, encoding="utf-8", newline="")
    return target.relative_to(repo_root).as_posix()


def add_worktree(source: Path, target: Path, revision: str) -> None:
    if target.exists():
        raise FileExistsError(f"Refusing to overwrite existing reconstruction: {target}")
    target.parent.mkdir(parents=True, exist_ok=True)
    run("git", "worktree", "add", "--detach", str(target), revision, cwd=source)
    actual = run("git", "rev-parse", "HEAD", cwd=target)
    expected = run("git", "rev-parse", revision, cwd=source)
    if actual != expected:
        raise RuntimeError(f"Revision mismatch for {target}: {actual} != {expected}")


def focal_sources(source: Path, buggy: str, fixed: str) -> list[str]:
    changed = run("git", "diff", "--name-only", buggy, fixed, cwd=source).splitlines()
    return sorted(
        path.replace("\\", "/")
        for path in changed
        if path.endswith(".java") and "/src/main/java/" in "/" + path.replace("\\", "/")
    )


def scenario(libro_root: Path, row: dict[str, str]) -> str:
    report = libro_root / row["bug_report_path"]
    data = json.loads(report.read_text(encoding="utf-8"))
    title = str(data.get("title", "")).strip()
    description = str(data.get("description_text", data.get("description", ""))).strip()
    return f"{title}\n\n{description}".strip()


def task_from_row(row: dict[str, str], fixed_dir: Path, buggy_dir: Path, test_file: str,
                  sources: list[str], libro_root: Path, output: Path) -> dict:
    bug_number = row["bug_id"].rsplit("-", 1)[-1]
    relative_fixed = fixed_dir.relative_to(output).as_posix()
    relative_buggy = buggy_dir.relative_to(output).as_posix()
    return {
        "taskId": f"GHRB-{row['bug_id'].upper().replace('_', '-').replace('/', '-')}-{row['candidate_id'].rsplit('_n', 1)[-1]}",
        "projectId": row["project_id"],
        "datasetId": "LIBRO-GHRB",
        "artifactId": row["candidate_id"],
        "independenceUnitId": f"{row['project_id']}#{bug_number}",
        "samplingStratum": row["initial_failure_category"],
        "sourceUri": row["pull_request_url"],
        "fixedRevision": row["fixed_revision"],
        "buggyRevision": row["buggy_revision"],
        "repositoryPath": relative_fixed,
        "buggyRepositoryPath": relative_buggy,
        "scenarioId": f"{row['bug_id']}-scenario",
        "testId": row["candidate_id"],
        "failingTestMethod": row["failing_test_method"],
        "scenarioSpecification": scenario(libro_root, row),
        "traceabilityLink": f"{row['bug_id']}-scenario -> {row['candidate_id']}",
        "initialEvidence": (
            f"LIBRO recorded {row['initial_failure_category']} for {row['candidate_id']} "
            "on the fixed revision before repair."
        ),
        "testFilePath": test_file,
        "testClassName": row["test_class_name"],
        "modulePath": PROJECTS[row["project_id"]][1],
        "buildSystem": "maven",
        "focalSourceFiles": sources,
        "previouslyVerifiedTests": [],
        "coverageReportPath": "target/site/jacoco/jacoco.xml",
        "mutationReportPath": "target/pit-reports/mutations.xml",
    }


def main() -> int:
    args = parse_args()
    frame = args.frame.resolve()
    libro_root = args.libro_root.resolve()
    output = args.output.resolve()
    if output.exists():
        raise FileExistsError(f"Output already exists; choose a new directory: {output}")
    output.mkdir(parents=True)

    deps = libro_root / ".python-deps"
    scripts = libro_root / "scripts"
    sys.path[:0] = [str(deps), str(scripts)]
    import common  # pylint: disable=import-outside-toplevel

    rows = selected_rows(frame)
    repositories = libro_root / "data/GHRB/repos"
    generated_root = libro_root / "data/GHRB/gen_tests"
    tasks: list[dict] = []
    audit: list[dict] = []
    for row in rows:
        order = int(row["selection_order"])
        repository_dir, module, _ = PROJECTS[row["project_id"]]
        source = repositories / repository_dir
        # Keep worktree paths deliberately short. AssertJ and Checkstyle contain
        # tracked files whose relative names are close to the Windows MAX_PATH
        # boundary, so descriptive directory names can make a valid checkout fail.
        fixed_dir = output / "r" / f"{order:02d}f"
        buggy_dir = output / "r" / f"{order:02d}b"
        status = "ERROR"
        detail = ""
        try:
            if not source.joinpath(".git").exists():
                raise FileNotFoundError(f"Source Git repository not found: {source}")
            sources = focal_sources(source, row["buggy_revision"], row["fixed_revision"])
            if not sources:
                raise RuntimeError("No changed production Java source was found between revisions.")
            add_worktree(source, buggy_dir, row["buggy_revision"])
            add_worktree(source, fixed_dir, row["fixed_revision"])

            test_tree = Path(module) / "src/test/java" if module else Path("src/test/java")
            run("git", "checkout", row["buggy_revision"], "--", test_tree.as_posix(), cwd=fixed_dir)
            generated_path = generated_root / f"{row['candidate_id']}.txt"
            generated = generated_path.read_text(encoding="utf-8", errors="strict")
            fixed_test = inject(common, fixed_dir, module, row, generated)
            buggy_test_path = buggy_dir / fixed_test
            if not buggy_test_path.is_file():
                raise FileNotFoundError(
                    f"Matching clean test class is missing from buggy revision: {buggy_test_path}"
                )
            tasks.append(task_from_row(row, fixed_dir, buggy_dir, fixed_test, sources, libro_root, output))
            status = "RECONSTRUCTED"
            detail = (
                "Revisions verified; generated test injected into the fixed workspace; "
                "matching buggy test class retained clean for the post-repair bug oracle."
            )
        except Exception as exception:  # keep a complete, auditable reconstruction ledger
            detail = f"{type(exception).__name__}: {exception}"
        audit.append(
            {
                "selection_order": order,
                "bug_id": row["bug_id"],
                "project_id": row["project_id"],
                "candidate_id": row["candidate_id"],
                "status": status,
                "detail": detail,
            }
        )
        print(f"[{order:02d}/{len(rows):02d}] {row['bug_id']}: {status}")

    manifest = {
        "studyDesign": {
            "studyId": "RQ2-LIBRO-GHRB-EXPANDED-PILOT",
            "samplingFrame": "Deterministic Phase 7.8 sample of reconstructable LIBRO/GHRB generated-test failures.",
            "selectionMethod": "One failed generated test per independent bug unit, balanced across projects with seed 20260814.",
            "selectionSeed": 20260814,
            "inclusionCriteria": [
                "Verified LIBRO/GHRB bug with fixed and buggy revisions.",
                "Generated test failed on the fixed revision before repair.",
                "Repository, generated test, injection target, and focal source are reconstructable.",
                "The same artifact is evaluated under all three RQ2 treatments.",
            ],
            "exclusionCriteria": [
                "Revision or generated-test provenance is missing.",
                "No changed production Java source can be identified.",
                "Recorded injection target cannot be reconstructed.",
                "Fixed failure or buggy build baseline does not pass preflight.",
            ],
            "powerPlan": {
                "familyWiseAlpha": 0.05,
                "targetPower": 0.8,
                "alternativeWinProbability": 0.7,
                "expectedDiscordantUnitRate": 0.4,
                "comparisonCount": 2,
                "minimumProjects": 5,
                "plannedIndependentUnits": len(tasks),
                "provisional": True,
            },
            "outcomePlan": {
                "contractVersion": "RQ2-OUTCOME-PLAN-1",
                "successNonInferiorityMargin": 0.05,
                "coverageNonInferiorityMargin": 0.05,
                "mutationNonInferiorityMargin": 0.05,
                "provisional": True,
            },
        },
        "tasks": tasks,
    }
    manifest_path = output / "benchmark.candidate.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    audit_path = output / "reconstruction-audit.csv"
    with audit_path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(audit[0]))
        writer.writeheader()
        writer.writerows(audit)
    provenance = {
        "contractVersion": "RQ2-PILOT-RECONSTRUCTION-1",
        "samplingFrame": str(frame),
        "samplingFrameSha256": sha256(frame),
        "selectedRows": len(rows),
        "reconstructedTasks": len(tasks),
        "manifestSha256": sha256(manifest_path),
        "auditSha256": sha256(audit_path),
    }
    (output / "reconstruction-provenance.json").write_text(
        json.dumps(provenance, indent=2), encoding="utf-8"
    )
    print(f"Reconstructed {len(tasks)}/{len(rows)} task(s).")
    print(f"Candidate manifest: {manifest_path}")
    print(f"Audit: {audit_path}")
    return 0 if len(tasks) == len(rows) else 2


if __name__ == "__main__":
    raise SystemExit(main())
