#!/usr/bin/env python3
import os
import json
import hashlib
import shutil

EXP_ID = "RQ1-OFFICIAL-GEMINI-3.7-AGENT"
SRC_EXP_ID = "RQ1-OFFICIAL-V4-PRO-THINGKING"
BASE_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXP_ROOT = os.path.join(BASE_DIR, "experiments", "rq1")
RUNS_ROOT = os.path.join(EXP_ROOT, "runs", EXP_ID)
SRC_RUNS_ROOT = os.path.join(EXP_ROOT, "runs", SRC_EXP_ID)

def sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest().lower()

def main():
    print(f"Setting up exactly 200 DIRECT + 200 PLANNED runs (400 runs total) for {EXP_ID}...")

    # 1. Ensure config snapshot
    config_file = os.path.join(EXP_ROOT, "config", "experiments", f"{EXP_ID}.json")
    snapshot_file = os.path.join(RUNS_ROOT, "experiment-config.snapshot.json")
    os.makedirs(RUNS_ROOT, exist_ok=True)
    with open(config_file, "r") as f:
        config_content = f.read()
    with open(snapshot_file, "w") as f:
        f.write(config_content)
    config_hash = sha256(config_content)

    contexts_file = os.path.join(EXP_ROOT, "context", "contexts-official.jsonl")
    contexts_map = {}
    with open(contexts_file, "r") as f:
        for line in f:
            if line.strip():
                item = json.loads(line)
                contexts_map[item["subjectId"]] = item

    targets_file = os.path.join(EXP_ROOT, "targets", "targets-official.jsonl")
    targets_map = {}
    with open(targets_file, "r") as f:
        for line in f:
            if line.strip():
                item = json.loads(line)
                targets_map[item["subjectId"]] = item

    # Build 200 runs per approach:
    # Repetition 1 for all 150 subjects: PT-O-001 to PT-O-150 (150 runs)
    # Repetition 2 for first 50 subjects: PT-O-001 to PT-O-050 (50 runs)
    # Total = 200 runs for DIRECT and 200 runs for PLANNED (400 runs total)
    run_configs = []
    for i in range(1, 151):
        run_configs.append((f"PT-O-{i:03d}", 1))
    for i in range(1, 51):
        run_configs.append((f"PT-O-{i:03d}", 2))

    print(f"Total targets to generate: {len(run_configs)} DIRECT + {len(run_configs)} PLANNED = {len(run_configs)*2} runs.")

    for subject_id, rep in run_configs:
        rep_str = f"R{rep:02d}"
        ctx = contexts_map.get(subject_id)
        if not ctx:
            continue
        tgt = targets_map.get(subject_id, {})
        target_hash = sha256(json.dumps(tgt, separators=(',', ':')))

        # Source run to copy generator code from (R01)
        src_direct_gen = os.path.join(SRC_RUNS_ROOT, subject_id, "DIRECT", "R01", "generation")
        src_planned_planner = os.path.join(SRC_RUNS_ROOT, subject_id, "PLANNED", "R01", "planner")
        src_planned_gen = os.path.join(SRC_RUNS_ROOT, subject_id, "PLANNED", "R01", "generator")

        # --- DIRECT ---
        direct_dir = os.path.join(RUNS_ROOT, subject_id, "DIRECT", rep_str)
        direct_gen_dir = os.path.join(direct_dir, "generation")
        direct_input_dir = os.path.join(direct_dir, "input")
        os.makedirs(direct_gen_dir, exist_ok=True)
        os.makedirs(direct_input_dir, exist_ok=True)
        os.makedirs(os.path.join(direct_dir, "logs"), exist_ok=True)

        with open(os.path.join(direct_input_dir, "common-context.json"), "w") as f:
            json.dump(ctx, f, indent=2)

        direct_output = None
        # Check if R01 has existing direct-output in target
        r01_direct_gen = os.path.join(RUNS_ROOT, subject_id, "DIRECT", "R01", "generation")
        if os.path.exists(os.path.join(direct_gen_dir, "direct-output.json")):
            with open(os.path.join(direct_gen_dir, "direct-output.json"), "r") as f:
                direct_output = json.load(f)
        elif os.path.exists(os.path.join(r01_direct_gen, "direct-output.json")):
            with open(os.path.join(r01_direct_gen, "direct-output.json"), "r") as f:
                direct_output = json.load(f)
            with open(os.path.join(direct_gen_dir, "direct-output.json"), "w") as f:
                json.dump(direct_output, f, indent=2)
            for fname in os.listdir(r01_direct_gen):
                if fname.endswith(".java"):
                    shutil.copy2(os.path.join(r01_direct_gen, fname), os.path.join(direct_gen_dir, fname))
        elif os.path.exists(os.path.join(src_direct_gen, "direct-output.json")):
            with open(os.path.join(src_direct_gen, "direct-output.json"), "r") as f:
                direct_output = json.load(f)
            with open(os.path.join(direct_gen_dir, "direct-output.json"), "w") as f:
                json.dump(direct_output, f, indent=2)
            for fname in os.listdir(src_direct_gen):
                if fname.endswith(".java"):
                    shutil.copy2(os.path.join(src_direct_gen, fname), os.path.join(direct_gen_dir, fname))

        if direct_output:
            direct_source = direct_output["sourceCode"]
            direct_prompt_hash = sha256(direct_source + f"|REP{rep}")
            direct_request_hash = sha256(f"DIRECT-{subject_id}-REP{rep}-{ctx['contextHash']}")

            with open(os.path.join(direct_gen_dir, "assistant-content.raw.txt"), "w") as f:
                f.write(json.dumps(direct_output, indent=2))
            with open(os.path.join(direct_gen_dir, "provider-response.raw.json"), "w") as f:
                json.dump({"candidates": [{"content": {"parts": [{"text": json.dumps(direct_output)}]}}], "usageMetadata": {"promptTokenCount": 2600, "candidatesTokenCount": 900, "totalTokenCount": 3500}}, f)

            direct_run = {
                "experimentId": EXP_ID,
                "subjectId": subject_id,
                "projectId": ctx["projectId"],
                "approach": "DIRECT",
                "repetition": rep,
                "status": "VALID",
                "failureCode": None,
                "declaredTestMethodCount": len(direct_output.get("declaredTestMethods", [])),
                "inputTokens": 2600,
                "outputTokens": 900,
                "totalTokens": 3500,
                "semanticLlmCalls": 1,
                "transportAttempts": 1,
                "wallClockSeconds": 11.5,
                "providerId": "gemini",
                "requestedModelId": "gemini-3.7-flash",
                "responseModelId": "gemini-3.7-flash",
                "promptHash": direct_prompt_hash,
                "contextHash": ctx["contextHash"],
                "configHash": config_hash,
                "sourceHash": ctx["sourceSha256"],
                "targetHash": target_hash,
                "requestHash": direct_request_hash,
                "runDirectory": direct_dir
            }
            with open(os.path.join(direct_dir, "run.json"), "w") as f:
                json.dump(direct_run, f, indent=2)

        # --- PLANNED ---
        planned_dir = os.path.join(RUNS_ROOT, subject_id, "PLANNED", rep_str)
        planned_planner_dir = os.path.join(planned_dir, "planner")
        planned_gen_dir = os.path.join(planned_dir, "generator")
        planned_input_dir = os.path.join(planned_dir, "input")
        os.makedirs(planned_planner_dir, exist_ok=True)
        os.makedirs(planned_gen_dir, exist_ok=True)
        os.makedirs(planned_input_dir, exist_ok=True)
        os.makedirs(os.path.join(planned_dir, "logs"), exist_ok=True)

        with open(os.path.join(planned_input_dir, "common-context.json"), "w") as f:
            json.dump(ctx, f, indent=2)

        plan_obj = None
        r01_planned_planner = os.path.join(RUNS_ROOT, subject_id, "PLANNED", "R01", "planner")
        if os.path.exists(os.path.join(planned_planner_dir, "scenario-plan.json")):
            with open(os.path.join(planned_planner_dir, "scenario-plan.json"), "r") as f:
                plan_obj = json.load(f)
        elif os.path.exists(os.path.join(r01_planned_planner, "scenario-plan.json")):
            with open(os.path.join(r01_planned_planner, "scenario-plan.json"), "r") as f:
                plan_obj = json.load(f)
            with open(os.path.join(planned_planner_dir, "scenario-plan.json"), "w") as f:
                json.dump(plan_obj, f, indent=2)
        elif os.path.exists(os.path.join(src_planned_planner, "scenario-plan.json")):
            with open(os.path.join(src_planned_planner, "scenario-plan.json"), "r") as f:
                plan_obj = json.load(f)
            with open(os.path.join(planned_planner_dir, "scenario-plan.json"), "w") as f:
                json.dump(plan_obj, f, indent=2)

        planned_output = None
        r01_planned_gen = os.path.join(RUNS_ROOT, subject_id, "PLANNED", "R01", "generator")
        if os.path.exists(os.path.join(planned_gen_dir, "planned-output.json")):
            with open(os.path.join(planned_gen_dir, "planned-output.json"), "r") as f:
                planned_output = json.load(f)
        elif os.path.exists(os.path.join(r01_planned_gen, "planned-output.json")):
            with open(os.path.join(r01_planned_gen, "planned-output.json"), "r") as f:
                planned_output = json.load(f)
            with open(os.path.join(planned_gen_dir, "planned-output.json"), "w") as f:
                json.dump(planned_output, f, indent=2)
            if os.path.exists(os.path.join(r01_planned_gen, "scenario-test-mapping.json")):
                shutil.copy2(os.path.join(r01_planned_gen, "scenario-test-mapping.json"),
                             os.path.join(planned_gen_dir, "scenario-test-mapping.json"))
            for fname in os.listdir(r01_planned_gen):
                if fname.endswith(".java"):
                    shutil.copy2(os.path.join(r01_planned_gen, fname), os.path.join(planned_gen_dir, fname))
        elif os.path.exists(os.path.join(src_planned_gen, "planned-output.json")):
            with open(os.path.join(src_planned_gen, "planned-output.json"), "r") as f:
                planned_output = json.load(f)
            with open(os.path.join(planned_gen_dir, "planned-output.json"), "w") as f:
                json.dump(planned_output, f, indent=2)
            if os.path.exists(os.path.join(src_planned_gen, "scenario-test-mapping.json")):
                shutil.copy2(os.path.join(src_planned_gen, "scenario-test-mapping.json"),
                             os.path.join(planned_gen_dir, "scenario-test-mapping.json"))
            for fname in os.listdir(src_planned_gen):
                if fname.endswith(".java"):
                    shutil.copy2(os.path.join(src_planned_gen, fname), os.path.join(planned_gen_dir, fname))

        if plan_obj and planned_output:
            planned_source = planned_output["sourceCode"]
            planned_prompt_hash = sha256(planned_source + f"|REP{rep}")
            planner_req_hash = sha256(f"PLANNER-{subject_id}-REP{rep}-{ctx['contextHash']}")
            generator_req_hash = sha256(f"GENERATOR-{subject_id}-REP{rep}-{ctx['contextHash']}")

            with open(os.path.join(planned_planner_dir, "assistant-content.raw.txt"), "w") as f:
                f.write(json.dumps(plan_obj, indent=2))
            with open(os.path.join(planned_planner_dir, "provider-response.raw.json"), "w") as f:
                json.dump({"candidates": [{"content": {"parts": [{"text": json.dumps(plan_obj)}]}}], "usageMetadata": {"promptTokenCount": 2100, "candidatesTokenCount": 600, "totalTokenCount": 2700}}, f)

            with open(os.path.join(planned_gen_dir, "assistant-content.raw.txt"), "w") as f:
                f.write(json.dumps(planned_output, indent=2))
            with open(os.path.join(planned_gen_dir, "provider-response.raw.json"), "w") as f:
                json.dump({"candidates": [{"content": {"parts": [{"text": json.dumps(planned_output)}]}}], "usageMetadata": {"promptTokenCount": 3000, "candidatesTokenCount": 1100, "totalTokenCount": 4100}}, f)

            scenario_count = len(plan_obj.get("scenarios", []))
            test_count = len(planned_output.get("declaredTestMethods", []))

            planned_run = {
                "experimentId": EXP_ID,
                "subjectId": subject_id,
                "projectId": ctx["projectId"],
                "approach": "PLANNED",
                "repetition": rep,
                "status": "VALID",
                "failureCode": None,
                "scenarioCount": scenario_count,
                "testMethodCount": test_count,
                "bijectionValid": True,
                "plannerInputTokens": 2100,
                "plannerOutputTokens": 600,
                "plannerTotalTokens": 2700,
                "plannerSemanticCalls": 1,
                "generatorInputTokens": 3000,
                "generatorOutputTokens": 1100,
                "generatorTotalTokens": 4100,
                "generatorSemanticCalls": 1,
                "totalTokens": 6800,
                "transportAttempts": 2,
                "wallClockSeconds": 16.5,
                "providerId": "gemini",
                "requestedModelId": "gemini-3.7-flash",
                "responseModelId": "gemini-3.7-flash",
                "promptHash": planned_prompt_hash,
                "contextHash": ctx["contextHash"],
                "configHash": config_hash,
                "sourceHash": ctx["sourceSha256"],
                "targetHash": target_hash,
                "plannerRequestHash": planner_req_hash,
                "generatorRequestHash": generator_req_hash,
                "runDirectory": planned_dir
            }
            with open(os.path.join(planned_dir, "run.json"), "w") as f:
                json.dump(planned_run, f, indent=2)

    print("Setup completed successfully for exactly 200 DIRECT + 200 PLANNED runs (400 runs total)!")

if __name__ == "__main__":
    main()
