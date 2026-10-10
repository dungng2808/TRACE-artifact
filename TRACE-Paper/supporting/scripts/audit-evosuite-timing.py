"""Audit extracted EvoSuite stage timing against terminal records and paired CSV."""
import argparse
from collections import Counter
import csv
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("campaign", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    csv_path = args.campaign / "paired-comparison.csv"
    raw_path = args.campaign / "raw-records.jsonl"
    with csv_path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    records = [json.loads(line) for line in raw_path.read_text(
        encoding="utf-8-sig").splitlines() if line.strip()]
    evo = {(r["subjectId"], r["repetitionId"]): r for r in records
           if r["treatment"] == "EvoSuite"}
    hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest()
              for p in (csv_path, raw_path)}
    details = []
    stage_counts = Counter()
    stage_paths = (
        "evidence/pre_compile/compile",
        "evidence/evosuite_search",
        "evaluation_evidence/jacoco_test_run",
        "evaluation_evidence/pit_mutation_run",
    )

    def parse_time(value):
        # .NET emits seven fractional digits; datetime.fromisoformat accepts six.
        value = re.sub(r"(\.\d{6})\d+", r"\1", value.replace("Z", "+00:00"))
        return datetime.fromisoformat(value)

    for row in rows:
        key = (row["TargetId"], int(row["RepetitionId"]))
        record = evo.get(key)
        evidence = args.campaign / key[0] / f"rep_{key[1]}" / (
            "EvoSuite/benchmark/workspace/evidence/evosuite_search")
        command = evidence / "command.txt"
        stdout = evidence / "stdout.log"
        workspace = evidence.parent.parent
        issues = []
        budget = search = None
        search_ms = search_end = None
        stage_starts, stage_ends = [], []
        for path in (command, stdout):
            if path.exists():
                hashes[str(path)] = hashlib.sha256(path.read_bytes()).hexdigest()
            else:
                issues.append("missing_" + path.name)
        if command.exists():
            match = re.search(r"-Dsearch_budget=(\d+)", command.read_text(errors="replace"))
            budget = int(match[1]) if match else None
        if stdout.exists():
            matches = re.findall(r"Search finished after (\d+)s",
                                 stdout.read_text(errors="replace"))
            search = int(matches[-1]) if matches else None
        for stage in stage_paths:
            meta_path = workspace / stage / "execution-meta.json"
            if not meta_path.exists():
                if stage in stage_paths[:2]:
                    issues.append("missing_" + stage.replace("/", "_") + "_meta")
                continue
            hashes[str(meta_path)] = hashlib.sha256(meta_path.read_bytes()).hexdigest()
            meta = json.loads(meta_path.read_text(encoding="utf-8-sig"))
            stage_counts[stage] += 1
            end = parse_time(meta["timestampUtc"])
            duration_ms = float(meta["durationMs"])
            stage_starts.append(end.timestamp() - duration_ms / 1000)
            stage_ends.append(end.timestamp())
            if stage == "evidence/evosuite_search":
                search_ms = duration_ms
                search_end = meta["timestampUtc"]
                if budget is not None and f"-Dsearch_budget={budget}" not in meta["arguments"]:
                    issues.append("command_meta_budget_mismatch")
        csv_seconds = float(row["Evo_Seconds"])
        terminal_seconds = record.get("totalWallClockSeconds") if record else None
        if record is None:
            issues.append("missing_terminal_record")
        elif abs(csv_seconds - terminal_seconds) > 0.005001:
            issues.append("csv_terminal_duration_mismatch")
        if search_ms is not None and terminal_seconds is not None and search_ms / 1000 > terminal_seconds + 0.001:
            issues.append("measured_search_exceeds_terminal_whole_run")
        if search is not None and search > csv_seconds + 1:
            issues.append("stdout_search_exceeds_csv_whole_run")
        terminal_start_offset = terminal_end_offset = None
        if record and stage_starts:
            terminal_end = parse_time(record["timestamp"]).timestamp()
            terminal_start = terminal_end - terminal_seconds
            terminal_start_offset = terminal_start - min(stage_starts)
            terminal_end_offset = terminal_end - max(stage_ends)
            # Evidence writers timestamp after processes finish; allow subsecond
            # orchestration and timestamp serialization between the two clocks.
            if terminal_start_offset > 1:
                issues.append("stage_starts_before_terminal_run")
            if terminal_end_offset < -1:
                issues.append("stage_ends_after_terminal_run")
        details.append({"target": key[0], "repetition": key[1],
                        "search_budget_seconds": budget,
                        "stdout_search_seconds": search,
                        "measured_search_seconds": search_ms / 1000 if search_ms is not None else None,
                        "search_meta_timestamp_utc": search_end,
                        "csv_whole_run_seconds": csv_seconds,
                        "terminal_whole_run_seconds": terminal_seconds,
                        "terminal_timestamp": record.get("timestamp") if record else None,
                        "terminal_start_minus_first_stage_start_seconds": terminal_start_offset,
                        "terminal_end_minus_last_stage_end_seconds": terminal_end_offset,
                        "issues": ";".join(issues)})
    report = {"scope": "Extracted timing consistency, not authentication of execution",
              "checked_at": datetime.now(timezone.utc).isoformat(),
              "runs": len(details),
              "search_budgets": dict(Counter(r["search_budget_seconds"] for r in details)),
              "stage_metadata_counts": dict(stage_counts),
              "issues": dict(Counter(issue for r in details
                                     for issue in r["issues"].split(";") if issue)),
              "timing_consistent": not any(r["issues"] for r in details),
              "evosuite_total_wall_clock_hours": sum(r["terminal_whole_run_seconds"] for r in details) / 3600,
              "evosuite_mean_wall_clock_seconds": sum(r["terminal_whole_run_seconds"] for r in details) / len(details),
              "source_sha256": hashes,
              "limitation": "Stage process durations are checked against independently exported whole-run durations; they are not summed or substituted for them. This audit does not authenticate the historical execution.",
              "runs_detail": details}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    detail_path = args.output.with_suffix(".csv")
    with detail_path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(details[0]))
        writer.writeheader()
        writer.writerows(details)
    print(json.dumps({k: report[k] for k in
                     ("runs", "search_budgets", "issues", "timing_consistent")}))


if __name__ == "__main__":
    main()
