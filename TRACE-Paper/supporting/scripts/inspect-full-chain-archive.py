#!/usr/bin/env python3
"""Read-only budget/accounting audit; never extract or execute campaign files."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("paired_csv", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    with zipfile.ZipFile(args.archive) as archive:
        roots = {n.split("/")[0] for n in archive.namelist()
                 if not n.startswith("__MACOSX/")}
        if len(roots) != 1:
            raise ValueError("Expected one non-metadata campaign root")
        root = roots.pop() + "/"
        commands = [n for n in archive.namelist() if n.startswith(root)
                    and n.endswith("/EvoSuite/benchmark/workspace/evidence/evosuite_search/command.txt")]
        budgets, exit_codes = Counter(), Counter()
        command_timeouts, command_keys = [], set()
        for name in commands:
            command = archive.read(name).decode("utf-8-sig")
            match = re.search(r"-Dsearch_budget=(\d+)", command)
            budgets[match.group(1) if match else "missing"] += 1
            exit_match = re.search(r"ExitCode:\s*(-?\d+)", command)
            exit_codes[exit_match.group(1) if exit_match else "missing"] += 1
            parts = name[len(root):].split("/")
            key = (parts[0], int(parts[1].removeprefix("rep_")))
            command_keys.add(key)
            if "TimedOut: True" in command:
                command_timeouts.append(key)
        raw_bytes = archive.read(root + "raw-records.jsonl")
        records = [json.loads(line) for line in raw_bytes.decode("utf-8-sig").splitlines()
                   if line.strip()]
        evo = [r for r in records if r["treatment"] == "EvoSuite"]
        evo_keys = {(r["subjectId"], r["repetitionId"]) for r in evo}
        archive_csv_sha = hashlib.sha256(archive.read(root + "paired-comparison.csv")).hexdigest()
        external_csv_sha = hashlib.sha256(args.paired_csv.read_bytes()).hexdigest()
        report = {
            "scope": "Archived commands and terminal accounting, not an experimental rerun or a full raw-evidence audit",
            "archive": args.archive.name,
            "archive_size_bytes": args.archive.stat().st_size,
            "archive_root": root,
            "zip_member_count_including_macos_metadata": len(archive.infolist()),
            "archived_paired_csv_sha256": archive_csv_sha,
            "external_paired_csv_sha256": external_csv_sha,
            "paired_csv_byte_identical": archive_csv_sha == external_csv_sha,
            "raw_records_sha256": hashlib.sha256(raw_bytes).hexdigest(),
            "raw_record_count": len(records),
            "treatment_counts": dict(Counter(r["treatment"] for r in records)),
            "evosuite_command_count": len(commands),
            "evosuite_unique_command_keys": len(command_keys),
            "evosuite_command_keys_match_terminal_keys": command_keys == evo_keys,
            "evosuite_search_budget_seconds": dict(budgets),
            "evosuite_exit_codes": dict(exit_codes),
            "evosuite_command_timed_out_keys": sorted(command_timeouts),
            "evosuite_verification_pass_counts": dict(Counter(r["totalVerificationPasses"] for r in evo)),
            "evosuite_recorded_token_sum": sum(r["totalTokensUsed"] for r in evo),
            "evosuite_recorded_provider_request_sum": sum(r["totalProviderRequests"] for r in evo),
            "evosuite_terminal_120_second_timeouts": [
                {k: r[k] for k in ("subjectId", "repetitionId", "terminalReason")}
                for r in evo if "120s" in r["terminalReason"]],
            "limitations": [
                "The archive hash is not calculated; size identifies this local input only.",
                "Current adapter source is explanatory evidence, not a verified historical source revision.",
                "Missing provider-usage totals cannot be recovered from zero-valued terminal fields.",
                "The command audit does not establish construct-neutral verification or equal realized budgets."]
        }
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
