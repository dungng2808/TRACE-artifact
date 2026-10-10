"""Recalculate manuscript summaries from external workspace exports, never edit inputs.

Requires numpy and scipy for paired tests. This checks exported records, not raw
Java/PIT execution or the historical provenance of the campaigns.
"""
import argparse
import csv
import hashlib
import json
from collections import Counter
from decimal import Decimal
from pathlib import Path

def holm(pvalues):
    order = np.argsort(pvalues)
    adjusted = np.empty(len(order))
    adjusted[order] = np.minimum(
        1, np.maximum.accumulate(np.array(pvalues)[order] * np.arange(len(order), 0, -1))
    )
    return adjusted.tolist()


def mean(values):
    return sum(values) / len(values) if values else None


def signed_test(differences):
    delta = np.array(differences)
    nonzero = delta[delta != 0]
    if not len(nonzero):
        return {"n": len(delta), "p": 1.0, "r_rb": 0.0}
    ranks = rankdata(abs(nonzero))
    return {
        "n": len(delta),
        "p": float(wilcoxon(delta, method="approx", correction=False).pvalue),
        "r_rb": float((sum(ranks[nonzero > 0]) - sum(ranks[nonzero < 0])) / sum(ranks)),
    }


def binary_test(differences):
    wins = sum(v > 0 for v in differences)
    losses = sum(v < 0 for v in differences)
    return {"wins": wins, "ties": len(differences) - wins - losses, "losses": losses,
            "p": float(binomtest(wins, wins + losses, 0.5).pvalue) if wins + losses else 1.0}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--rq3-only-update", action="store_true",
                        help="Refresh RQ3 in an existing report without unavailable RQ1/RQ2 inputs")
    parser.add_argument("--rq3-root", type=Path,
                        help="Root used to record relative RQ3 paths and hashes")
    parser.add_argument("--rq3-metrics", type=Path, nargs="+",
                        help="Explicit RQ3 metric reports; excludes every unlisted run")
    args = parser.parse_args()
    hashes = {}

    def refresh_rq3(report, report_hashes):
        root = (args.rq3_root or args.workspace).resolve()
        paths = args.rq3_metrics
        if paths is None:
            parser.error("Pass the three displayed reports with --rq3-metrics; never glob in excluded GLM")
        report["rq3"] = []
        for key in list(report_hashes):
            if key.startswith("results/rq3/") or key.startswith("HeThong2_ThucNghiem_TongHop/"):
                del report_hashes[key]
        for path in paths:
            path = path.resolve()
            relative = path.relative_to(root).as_posix()
            report_hashes[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            assert data["evaluation_cases"] == 73
            report["rq3"].append({"path": relative,
                                  "evaluation_cases": data["evaluation_cases"],
                                  "metrics": data["metrics"]})
        report["input_sha256"] = report_hashes

    if args.rq3_only_update:
        if args.output is None or not args.output.is_file() or args.rq3_metrics is None:
            parser.error("--rq3-only-update requires an existing --output and --rq3-metrics")
        output = json.loads(args.output.read_text(encoding="utf-8"))
        refresh_rq3(output, output["input_sha256"])
        args.output.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n",
                               encoding="utf-8")
        print(f"Updated RQ3 in {args.output}; RQ1/RQ2 retained as historical")
        return

    global np, scipy, binomtest, rankdata, wilcoxon
    import numpy as np
    import scipy
    from scipy.stats import binomtest, rankdata, wilcoxon

    def read(relative):
        path = args.workspace / relative
        hashes[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
        with path.open(encoding="utf-8-sig", newline="") as stream:
            return list(csv.DictReader(stream))

    output = {"scope": "Export consistency; not raw-execution replication",
              "scipy": scipy.__version__, "numpy": np.__version__, "rq1": {}}
    for filename in ["runs-gemini-3.7-flash.csv", "runs-deepseek-v4-flash-thinking.csv"]:
        rows = read("results/rq1/csv/" + filename)
        assert len(rows) == 300
        groups = {a: {r["subject_id"]: r for r in rows if r["approach"] == a}
                  for a in ["DIRECT", "PLANNED"]}
        assert len(groups["DIRECT"]) == len(groups["PLANNED"]) == 150
        assert groups["DIRECT"].keys() == groups["PLANNED"].keys()
        stats, tests = {}, {}
        continuous = ["e2e_target_satisfaction", "e2e_branch_coverage", "e2e_mutation_score"]
        for arm, records in groups.items():
            stats[arm] = {"compile": sum(r["compile_success"] == "true" for r in records.values()),
                          "execute": sum(r["execution_success"] == "true" for r in records.values())}
            for key in continuous + ["total_tokens", "wall_clock_seconds"]:
                values = [float(r[key]) for r in records.values() if r[key].strip()]
                stats[arm][key] = {"n": len(values), "mean": mean(values)}
        for key in ["compile_success", "execution_success"] + continuous:
            if key in continuous:
                differences = [float(Decimal(groups["PLANNED"][i][key]) - Decimal(r[key]))
                               for i, r in groups["DIRECT"].items()
                               if r[key].strip() and groups["PLANNED"][i][key].strip()]
                tests[key] = signed_test(differences)
            else:
                tests[key] = binary_test([int(groups["PLANNED"][i][key] == "true") - int(r[key] == "true")
                                          for i, r in groups["DIRECT"].items()])
        for result, p in zip(tests.values(), holm([r["p"] for r in tests.values()])):
            result["p_holm"] = p
        conditional = {}
        for key in continuous:
            differences = [float(Decimal(groups["PLANNED"][i][key]) - Decimal(r[key]))
                           for i, r in groups["DIRECT"].items()
                           if r["execution_success"] == "true"
                           and groups["PLANNED"][i]["execution_success"] == "true"
                           and r[key].strip() and groups["PLANNED"][i][key].strip()]
            conditional[key] = {**signed_test(differences), "mean_delta": mean(differences)}
        output["rq1"][filename] = {"summary": stats, "tests": tests,
                                     "conditional_unadjusted": conditional}

    base = "results/rq2/raw/"
    rows = read(base + "rq2-postdev-v2-surefire-r1/analysis/batch-rq2-20260820T113236Z-dc439caaa-runs.csv")
    rows += read(base + "rq2-postdev-v2-surefire-r1-replacement/analysis/batch-rq2-20260825T165335Z-6598109b5-runs.csv")
    assert len(rows) == 300
    groups = {a: {r["task_id"]: r for r in rows if r["strategy"] == a} for a in ["TGSLR", "FTMR", "FCR"]}
    assert all(len(g) == 100 and g.keys() == groups["TGSLR"].keys() for g in groups.values())
    stats, tests = {}, []
    for arm, records in groups.items():
        successful = [r for r in records.values() if r["status"] == "Success"]
        stats[arm] = {"success": len(successful),
                      "compilation_stage": dict(Counter(r["compilation_stage"] for r in records.values())),
                      "regression_runs": sum(float(r["regressed_tests"]) > 0 for r in records.values()),
                      "changed_loc_success_mean": mean([float(r["total_changed_lines"]) for r in successful])}
        for key in ["total_tokens", "total_api_calls", "iteration_count", "duration_seconds"]:
            stats[arm][key] = mean([float(r[key]) for r in records.values()])
        for key in ["final_line_coverage", "final_mutation_score"]:
            values = [float(r[key]) for r in successful if r[key].strip()]
            stats[arm][key] = {"n": len(values), "mean": mean(values)}
    for comparator in ["FTMR", "FCR"]:
        for key in ["status", "regressed_tests", "total_tokens", "total_api_calls", "iteration_count"]:
            def value(row):
                if key == "status":
                    return int(row[key] == "Success")
                if key == "regressed_tests":
                    return int(float(row[key]) > 0)
                return Decimal(row[key])
            delta = [float(value(r) - value(groups[comparator][i])) for i, r in groups["TGSLR"].items()]
            test = binary_test(delta) if key in ["status", "regressed_tests"] else signed_test(delta)
            tests.append({"comparison": "TGSLR-" + comparator, "metric": key, **test})
    for result, p in zip(tests, holm([r["p"] for r in tests])):
        result["p_holm"] = p
    output["rq2"] = {"summary": stats, "tests": tests, "cost_effect_direction": "TGSLR minus comparator"}
    refresh_rq3(output, hashes)
    encoded = json.dumps(output, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(encoded)
        print(f"Wrote {args.output}; RQ1=600 rows, RQ2=300 rows, RQ3={len(output['rq3'])} reports")
    else:
        print(encoded)


if __name__ == "__main__":
    main()
