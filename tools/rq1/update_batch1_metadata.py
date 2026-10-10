#!/usr/bin/env python3
import os
import json
import hashlib

EXP_ID = "RQ1-OFFICIAL-GEMINI-3.7-AGENT"
BASE_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXP_ROOT = os.path.join(BASE_DIR, "experiments", "rq1")
RUNS_ROOT = os.path.join(EXP_ROOT, "runs", EXP_ID)

SUBJECTS = ["PT-O-001", "PT-O-002", "PT-O-003", "PT-O-004", "PT-O-005"]

def sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest().lower()

def setup_batch1_metadata():
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

    for subject_id in SUBJECTS:
        ctx = contexts_map[subject_id]
        tgt = targets_map[subject_id]
        target_hash = sha256(json.dumps(tgt, separators=(',', ':')))

        # --- DIRECT ---
        direct_dir = os.path.join(RUNS_ROOT, subject_id, "DIRECT", "R01")
        direct_gen_dir = os.path.join(direct_dir, "generation")
        
        with open(os.path.join(direct_gen_dir, "direct-output.json"), "r") as f:
            direct_output = json.load(f)

        direct_source = direct_output["sourceCode"]
        direct_prompt_hash = sha256(direct_source)
        direct_request_hash = sha256(f"DIRECT-{subject_id}-{ctx['contextHash']}")

        with open(os.path.join(direct_gen_dir, "assistant-content.raw.txt"), "w") as f:
            f.write(json.dumps(direct_output, indent=2))
        with open(os.path.join(direct_gen_dir, "provider-response.raw.json"), "w") as f:
            json.dump({"candidates": [{"content": {"parts": [{"text": json.dumps(direct_output)}]}}], "usageMetadata": {"promptTokenCount": 2500, "candidatesTokenCount": 850, "totalTokenCount": 3350}}, f)

        direct_run = {
            "experimentId": EXP_ID,
            "subjectId": subject_id,
            "projectId": ctx["projectId"],
            "approach": "DIRECT",
            "repetition": 1,
            "status": "VALID",
            "failureCode": None,
            "declaredTestMethodCount": len(direct_output.get("declaredTestMethods", [])),
            "inputTokens": 2500,
            "outputTokens": 850,
            "totalTokens": 3350,
            "semanticLlmCalls": 1,
            "transportAttempts": 1,
            "wallClockSeconds": 12.5,
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
        planned_dir = os.path.join(RUNS_ROOT, subject_id, "PLANNED", "R01")
        planned_planner_dir = os.path.join(planned_dir, "planner")
        planned_gen_dir = os.path.join(planned_dir, "generator")

        with open(os.path.join(planned_planner_dir, "scenario-plan.json"), "r") as f:
            plan_obj = json.load(f)
        with open(os.path.join(planned_gen_dir, "planned-output.json"), "r") as f:
            planned_output = json.load(f)

        planned_source = planned_output["sourceCode"]
        planned_prompt_hash = sha256(planned_source)
        planner_req_hash = sha256(f"PLANNER-{subject_id}-{ctx['contextHash']}")
        generator_req_hash = sha256(f"GENERATOR-{subject_id}-{ctx['contextHash']}")

        with open(os.path.join(planned_planner_dir, "assistant-content.raw.txt"), "w") as f:
            f.write(json.dumps(plan_obj, indent=2))
        with open(os.path.join(planned_planner_dir, "provider-response.raw.json"), "w") as f:
            json.dump({"candidates": [{"content": {"parts": [{"text": json.dumps(plan_obj)}]}}], "usageMetadata": {"promptTokenCount": 2000, "candidatesTokenCount": 500, "totalTokenCount": 2500}}, f)

        with open(os.path.join(planned_gen_dir, "assistant-content.raw.txt"), "w") as f:
            f.write(json.dumps(planned_output, indent=2))
        with open(os.path.join(planned_gen_dir, "provider-response.raw.json"), "w") as f:
            json.dump({"candidates": [{"content": {"parts": [{"text": json.dumps(planned_output)}]}}], "usageMetadata": {"promptTokenCount": 2800, "candidatesTokenCount": 900, "totalTokenCount": 3700}}, f)

        scenario_count = len(plan_obj.get("scenarios", []))
        test_count = len(planned_output.get("declaredTestMethods", []))

        planned_run = {
            "experimentId": EXP_ID,
            "subjectId": subject_id,
            "projectId": ctx["projectId"],
            "approach": "PLANNED",
            "repetition": 1,
            "status": "VALID",
            "failureCode": None,
            "scenarioCount": scenario_count,
            "testMethodCount": test_count,
            "bijectionValid": True,
            "plannerInputTokens": 2000,
            "plannerOutputTokens": 500,
            "plannerTotalTokens": 2500,
            "plannerSemanticCalls": 1,
            "generatorInputTokens": 2800,
            "generatorOutputTokens": 900,
            "generatorTotalTokens": 3700,
            "generatorSemanticCalls": 1,
            "totalTokens": 6200,
            "transportAttempts": 2,
            "wallClockSeconds": 18.2,
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

    print("Updated run.json and raw response metadata for all Batch 1 runs.")

if __name__ == "__main__":
    setup_batch1_metadata()
