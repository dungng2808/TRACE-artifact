#!/usr/bin/env python3
"""Post hoc proxy-tokenizer estimate for unrecorded EvoSuite B2 usage.

Requires tiktoken 0.14.0. This estimates neither historical provider billing
nor the exact Gemini 2.5 Flash count: the original B2 prompts and responses
were not retained with the EvoSuite records.
"""

import argparse
from hashlib import sha256
import json
from pathlib import Path
from statistics import mean

import tiktoken


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def fit_line(points):
    xs = [x for x, _ in points]
    ys = [y for _, y in points]
    x_mean, y_mean = mean(xs), mean(ys)
    slope = sum((x - x_mean) * (y - y_mean) for x, y in points) / sum(
        (x - x_mean) ** 2 for x in xs)
    intercept = y_mean - slope * x_mean
    squared_error = sum((y - intercept - slope * x) ** 2 for x, y in points)
    squared_total = sum((y - y_mean) ** 2 for y in ys)
    return intercept, slope, 1 - squared_error / squared_total


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    workspace = args.workspace.resolve()

    campaign = workspace / "experiments/full-chain/runs/official-150-20260920-r42-r44-v1"
    raw_path = campaign / "raw-records.jsonl"
    records = [json.loads(line) for line in raw_path.read_text(encoding="utf-8-sig").splitlines() if line]
    reached = [r for r in records if r["treatment"] == "EvoSuite" and r["totalVerificationPasses"] == 1]
    if len(reached) != 275:
        raise ValueError(f"Expected 275 B2-reached EvoSuite records; found {len(reached)}")

    source_paths = []
    for record in reached:
        run = campaign / record["subjectId"] / f"rep_{record['repetitionId']}"
        test_root = run / "EvoSuite/benchmark/workspace/src/test/java"
        matches = list(test_root.rglob("*_ESTest.java"))
        if len(matches) != 1:
            raise ValueError(f"Expected one delivered test in {test_root}; found {len(matches)}")
        source_paths.append(matches[0])

    predictions = list((workspace / "results/rq3/in_paper").glob(
        "*gemini_3_5*/predictions/predictions_merged_v2.json"))
    if len(predictions) != 1:
        raise ValueError(f"Expected one Gemini 3.5 RQ3 prediction file; found {len(predictions)}")
    prediction_path = predictions[0]
    reference = [r for r in read_json(prediction_path)
                 if r["Verifier"] == "B2_SPECIALIZED_MULTI_AGENT" and r["ApiCalls"] == 3]
    if len(reference) != 67:
        raise ValueError(f"Expected 67 non-retry RQ3 B2 cases; found {len(reference)}")
    envelope_dir = workspace / "datasets/rq3/phase6_exploratory_v2_dataset/envelopes"

    estimates = {}
    for tokenizer_name in ("o200k_base", "cl100k_base"):
        tokenizer = tiktoken.get_encoding(tokenizer_name)
        calibration = []
        for row in reference:
            envelope = read_json(envelope_dir / (row["CaseId"] + ".json"))
            source = envelope["generated_test"]["source_text"]
            calibration.append((len(tokenizer.encode(source)), row["InputTokens"]))
        intercept, slope, r_squared = fit_line(calibration)
        source_token_sum = sum(len(tokenizer.encode(path.read_text(encoding="utf-8-sig")))
                               for path in source_paths)
        input_estimate = intercept * len(reached) + slope * source_token_sum
        output_estimate = mean(row["OutputTokens"] for row in reference) * len(reached)
        estimates[tokenizer_name] = {
            "source_token_sum": source_token_sum,
            "input_tokens_per_b2_fit_intercept": intercept,
            "input_tokens_per_source_token_fit_slope": slope,
            "calibration_r_squared": r_squared,
            "estimated_input_tokens": round(input_estimate),
            "estimated_output_tokens": round(output_estimate),
            "estimated_total_tokens": round(input_estimate + output_estimate),
        }

    report = {
        "scope": "Post hoc sensitivity estimate; not observed Gemini 2.5 Flash campaign usage",
        "campaign_model": "google/gemini-2.5-flash via OpenRouter",
        "calibration_model": "Gemini 3.5 Flash RQ3 exploratory study",
        "calibration_non_retry_b2_cases": len(reference),
        "evosuite_b2_reached_runs": len(reached),
        "nominal_specialist_invocations": len(reached) * 3,
        "raw_records_sha256": sha256(raw_path.read_bytes()).hexdigest(),
        "rq3_predictions_sha256": sha256(prediction_path.read_bytes()).hexdigest(),
        "source_java_bytes": sum(path.stat().st_size for path in source_paths),
        "estimates": estimates,
        "limitations": [
            "Neither tokenizer is the Gemini 2.5 Flash tokenizer.",
            "Only retained test source is tokenized; the complete historical B2 prompts and responses are unavailable.",
            "Prompt/evidence overhead and output tokens are inferred from a different RQ3 model and cohort.",
            "Retries and provider-specific hidden/thinking tokens cannot be reconstructed.",
            "The fitted R-squared is in-sample and does not measure transfer accuracy to EvoSuite.",
        ],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({name: estimate["estimated_total_tokens"]
                      for name, estimate in estimates.items()}))


if __name__ == "__main__":
    main()
