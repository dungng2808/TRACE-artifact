"""Check the displayed Qwen RQ3 numbers against frozen System 2 exports.

Uses only the standard library. This checks numerical consistency, not gold
validity, provider execution, or the bootstrap sampling implementation.
"""

import argparse
import csv
import hashlib
import json
from math import comb
from pathlib import Path


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def rate(numerator, denominator):
    return numerator / denominator if denominator else 0.0


def f1(tp, fp, fn):
    return rate(2 * tp, 2 * tp + fp + fn)


def exact_mcnemar(a_only, b_only):
    n = a_only + b_only
    if n == 0:
        return 1.0
    tail = sum(comb(n, k) for k in range(min(a_only, b_only) + 1)) / 2**n
    return min(1.0, 2 * tail)


def close(actual, expected):
    assert abs(actual - expected) < 0.000001, (actual, expected)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path, help="Qwen System 2 result directory")
    args = parser.parse_args()
    run = args.run
    metrics_path = run / "results/metrics_v2.json"
    csv_path = run / "results/predictions_joined_private_gold_v2.csv"
    frozen_path = run / "frozen/predictions_merged_v2.json"
    metrics = json.loads(metrics_path.read_text(encoding="utf-8-sig"))
    frozen = json.loads(frozen_path.read_text(encoding="utf-8-sig"))
    with csv_path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))

    assert len(rows) == len(frozen) == metrics["prediction_rows"] == 219
    assert len({(row["case_id"], row["verifier"]) for row in rows}) == 219
    assert metrics["evaluation_cases"] == 73
    assert metrics["predictions_sha256"].upper() == sha256(frozen_path)
    assert sha256(metrics_path) == "61DA8A5803B027F3A1B56D33169D9E281B55CA0DC0201EEF7AF423482C030C0C"
    assert sha256(csv_path) == "DAD17E38051E69CF7A9DA75A207564DC050E40E55FD1084EAF9713801CA8F105"
    assert all(row["execution_status"] == "OK" for row in rows)
    assert all(item["ExecutionStatus"] == "OK" for item in frozen)
    specialists = [finding for item in frozen
                   if item["Verifier"] == "B2_SPECIALIZED_MULTI_AGENT"
                   for finding in item["SpecialistFindings"]]
    assert len(specialists) == 219
    assert all(finding["ExecutionStatus"] == "OK" for finding in specialists)

    report = {entry["verifier"]: entry for entry in metrics["metrics"]}
    grouped = {name: {row["case_id"]: row for row in rows if row["verifier"] == name}
               for name in report}
    assert all(len(group) == 73 for group in grouped.values())
    assert len({row["case_id"] for row in rows}) == 73
    summary = {}
    for name, group in grouped.items():
        entries = list(group.values())
        expected = report[name]
        tp = sum(r["gold_false_passing"] == "True" and r["predicted_false_passing"] == "True" for r in entries)
        fp = sum(r["gold_false_passing"] == "False" and r["predicted_false_passing"] == "True" for r in entries)
        fn = sum(r["gold_false_passing"] == "True" and r["predicted_false_passing"] == "False" for r in entries)
        tn = sum(r["gold_false_passing"] == "False" and r["predicted_false_passing"] == "False" for r in entries)
        false_accepted = sum(r["gold_false_passing"] == "True" and r["decision"] == "VERIFIED" for r in entries)
        positive_abstentions = sum(r["gold_false_passing"] == "True" and r["decision"] == "ABSTAIN" for r in entries)
        defects = [r for r in entries if r["gold_decision"] == "REPAIR_REQUIRED"]
        labels = {r["gold_cause"] for r in defects}
        attribution = sum(
            f1(
                sum(r["gold_cause"] == label and r["cause"] == label for r in defects),
                sum(r["gold_cause"] != label and r["cause"] == label for r in defects),
                sum(r["gold_cause"] == label and r["cause"] != label for r in defects),
            ) for label in labels
        ) / len(labels)
        covered = [r for r in entries if r["decision"] != "ABSTAIN"]
        close(f1(tp, fp, fn), expected["false_pass_f1"])
        close(rate(false_accepted, 26), expected["false_acceptance_rate"])
        close(rate(sum(r["decision_correct"] == "True" for r in entries), 73), expected["decision_accuracy"])
        close(rate(len(covered), 73), expected["decision_coverage"])
        close(rate(sum(r["decision_correct"] == "True" for r in covered), len(covered)), expected["selective_accuracy"])
        close(attribution, expected["attribution_macro_f1"])
        close(rate(sum(r["route_correct"] == "True" for r in defects), 54), expected["routing_accuracy"])
        for key in ("input_tokens", "output_tokens", "api_calls"):
            close(sum(float(r[key]) for r in entries), expected[key])
        assert len(defects) == 54 and len(labels) == 7
        assert tp + fn == 26 and fp + tn == 47
        assert expected["api_or_output_error_count"] == 0
        summary[name] = {"TP": tp, "FP": fp, "FN": fn, "TN": tn,
                         "false_accepted": false_accepted,
                         "positive_abstentions": positive_abstentions,
                         "decision_accuracy": expected["decision_accuracy"],
                         "attribution_macro_f1": expected["attribution_macro_f1"],
                         "routing_accuracy": expected["routing_accuracy"]}

    b1 = grouped["B1_GENERAL_REVIEWER"]
    b2 = grouped["B2_SPECIALIZED_MULTI_AGENT"]
    ids = b1.keys()
    b2_only = sum(b2[i]["decision_correct"] == "True" and b1[i]["decision_correct"] != "True" for i in ids)
    b1_only = sum(b1[i]["decision_correct"] == "True" and b2[i]["decision_correct"] != "True" for i in ids)
    close(exact_mcnemar(b2_only, b1_only), metrics["b2_vs_b1"]["decision_mcnemar"]["p_value"])
    assert (b2_only, b1_only) == (12, 9)
    far_b1_only = sum(b1[i]["gold_false_passing"] == "True"
                      and b1[i]["decision"] == "VERIFIED"
                      and b2[i]["decision"] != "VERIFIED" for i in ids)
    far_b2_only = sum(b2[i]["gold_false_passing"] == "True"
                      and b2[i]["decision"] == "VERIFIED"
                      and b1[i]["decision"] != "VERIFIED" for i in ids)
    assert (far_b1_only, far_b2_only) == (8, 0)
    close(exact_mcnemar(far_b1_only, far_b2_only), 0.0078125)
    routes_b1_only = sum(b1[i]["gold_decision"] == "REPAIR_REQUIRED"
                         and b1[i]["route_correct"] == "True"
                         and b2[i]["route_correct"] != "True" for i in ids)
    routes_b2_only = sum(b2[i]["gold_decision"] == "REPAIR_REQUIRED"
                         and b2[i]["route_correct"] == "True"
                         and b1[i]["route_correct"] != "True" for i in ids)
    assert (routes_b1_only, routes_b2_only) == (7, 7)
    close(exact_mcnemar(routes_b1_only, routes_b2_only), 1.0)
    assert summary["B1_GENERAL_REVIEWER"]["false_accepted"] == 10
    assert summary["B2_SPECIALIZED_MULTI_AGENT"]["false_accepted"] == 2
    assert summary["B1_GENERAL_REVIEWER"]["positive_abstentions"] == 5
    assert summary["B2_SPECIALIZED_MULTI_AGENT"]["positive_abstentions"] == 2
    print(json.dumps({"status": "PASS", "summary": summary,
                      "decision_mcnemar": exact_mcnemar(b2_only, b1_only),
                      "far_mcnemar": exact_mcnemar(far_b1_only, far_b2_only),
                      "routing_mcnemar": exact_mcnemar(routes_b1_only, routes_b2_only)},
                     indent=2))


if __name__ == "__main__":
    main()
