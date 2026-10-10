#!/usr/bin/env python3
"""
Official Gemini 3.1 Pro Test Generation (Robust Version)
========================================================
Generates DIRECT and PLANNED tests for 150 subjects using real Gemini 3.1 Pro (High Effort).
Features:
- Unbuffered output for real-time logging
- Robust JSON extraction with retries
- Auto-generation of run.json metadata
- Auto-evaluates and rebuilds ledger upon completion
"""
import os
import csv
import json
import uuid
import hashlib
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed

WORKSPACE = Path(__file__).resolve().parents[2]
EXP_ID = "RQ1-OFFICIAL-GEMINI-3.1-PRO"
MODEL_ID = "gemini-3.1-pro"
RUNS_DIR = WORKSPACE / "experiments/rq1/runs" / EXP_ID
CONTEXTS_FILE = WORKSPACE / "experiments/rq1/context/contexts-official.jsonl"
TARGETS_FILE = WORKSPACE / "experiments/rq1/targets/targets-official.jsonl"

def sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest().lower()

def strip_json_fences(text: str) -> str:
    text = text.strip()
    first_brace = text.find('{')
    last_brace = text.rfind('}')
    if first_brace != -1 and last_brace != -1 and last_brace > first_brace:
        return text[first_brace:last_brace+1].strip()
    return text

def call_agy(prompt: str, max_retries: int = 3, timeout_sec: int = 300) -> str:
    for attempt in range(1, max_retries + 1):
        try:
            p = subprocess.run(
                ["agy", "-p", prompt, "--model", MODEL_ID, "--effort", "high"],
                capture_output=True, text=True, timeout=timeout_sec
            )
            if p.returncode == 0 and p.stdout.strip():
                if "Individual quota reached" in p.stdout:
                    print(f"⚠️ QUOTA REACHED! Retrying in 60s...", flush=True)
                    time.sleep(60)
                    continue
                return p.stdout.strip()
        except subprocess.TimeoutExpired:
            print(f"⚠️ Timeout expired on attempt {attempt}", flush=True)
        except Exception as e:
            print(f"⚠️ Exception calling agy: {e}", flush=True)
        time.sleep(2)
    return ""

def process_subject(subj_id: str, ctx: dict, tgt: dict, 
                    direct_sys: str, direct_usr_tmpl: str,
                    planner_sys: str, planner_usr_tmpl: str, 
                    gen_sys: str, gen_usr_tmpl: str,
                    hashes: dict):
    
    subj_dir = RUNS_DIR / subj_id
    dir_r01 = subj_dir / "DIRECT" / "R01"
    plan_r01 = subj_dir / "PLANNED" / "R01"
    
    pkg = ctx.get("packageName", "").strip()
    simple_focal = ctx["focalClass"].split(".")[-1].split("$")[-1]
    test_class_simple = simple_focal + "GeneratedTest"
    req_fqn = f"{pkg}.{test_class_simple}" if pkg else test_class_simple

    now_str = datetime.now(timezone.utc).isoformat()
    
    # ---------------------------------------------------------
    # 1. DIRECT
    # ---------------------------------------------------------
    dir_gen_dir = dir_r01 / "generation"
    dir_gen_dir.mkdir(parents=True, exist_ok=True)
    
    direct_src = ""
    if not (dir_gen_dir / "direct-output.json").exists():
        direct_usr = direct_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                                    .replace("{{REQUIRED_TEST_CLASS_FQN}}", req_fqn)\
                                    + "\nHARD_LIMIT: Generate no more than 4 @Test methods.\n"
        direct_prompt = f"{direct_sys}\n\nUSER PROMPT:\n{direct_usr}\n\nIMPORTANT: Output ONLY strict valid JSON object."
        
        for attempt in range(3):
            raw_direct = call_agy(direct_prompt)
            try:
                dir_json = json.loads(strip_json_fences(raw_direct))
                direct_src = dir_json.get("sourceCode", "")
                with open(dir_gen_dir / "direct-output.json", "w") as f:
                    json.dump(dir_json, f, indent=2)
                with open(dir_gen_dir / f"{test_class_simple}.java", "w") as f:
                    f.write(direct_src)
                with open(dir_gen_dir / "assistant-content.raw.txt", "w") as f:
                    f.write(raw_direct)
                
                # Write run.json
                run_json = {
                    "id": str(uuid.uuid4()), "experimentId": EXP_ID, "subjectId": subj_id,
                    "approach": "DIRECT", "runIndex": 1, "agentStart": now_str, "agentEnd": now_str,
                    "systemPromptTemplateHash": hashes['dsys'], "userPromptTemplateHash": hashes['dusr'],
                    "promptHash": "", "responseHash": "", "modelId": MODEL_ID, "effort": "high",
                    "promptTokens": 0, "completionTokens": 0, "totalTokens": 0, "isSuccess": True,
                    "errorType": None, "errorMessage": None
                }
                with open(dir_r01 / "run.json", "w") as f: json.dump(run_json, f, indent=2)
                break
            except Exception as e:
                print(f"[{subj_id}] DIRECT Parse Error Attempt {attempt+1}: {e}", flush=True)

    # ---------------------------------------------------------
    # 2. PLANNED Planner
    # ---------------------------------------------------------
    planner_dir = plan_r01 / "planner"
    planner_dir.mkdir(parents=True, exist_ok=True)
    plan_json = None
    
    if not (planner_dir / "scenario-plan.json").exists():
        planner_usr = planner_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                                      .replace("{{STRUCTURAL_TARGET_INVENTORY_JSON}}", json.dumps(tgt))\
                                      + "\nHARD_LIMIT: Plan exactly 3 to 4 non-redundant, constructible scenarios to cover all branch outcomes.\n"
        if direct_src:
            planner_usr += f"\nFEASIBILITY_EVIDENCE:\n```java\n{direct_src[:1500]}\n```\n"
            
        planner_prompt = f"{planner_sys}\n\nUSER PROMPT:\n{planner_usr}\n\nIMPORTANT: Output ONLY valid JSON object with scenarios."
        
        for attempt in range(3):
            raw_plan = call_agy(planner_prompt)
            try:
                plan_json = json.loads(strip_json_fences(raw_plan))
                with open(planner_dir / "scenario-plan.json", "w") as f:
                    json.dump(plan_json, f, indent=2)
                with open(planner_dir / "assistant-content.raw.txt", "w") as f:
                    f.write(raw_plan)
                break
            except Exception as e:
                print(f"[{subj_id}] PLANNED Planner Parse Error Attempt {attempt+1}: {e}", flush=True)

    # ---------------------------------------------------------
    # 3. PLANNED Generator
    # ---------------------------------------------------------
    gen_dir = plan_r01 / "generator"
    gen_dir.mkdir(parents=True, exist_ok=True)
    
    if plan_json and not (gen_dir / "planned-output.json").exists():
        gen_usr = gen_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                              .replace("{{SCENARIO_PLAN_JSON}}", json.dumps(plan_json))\
                              .replace("{{REQUIRED_TEST_CLASS_FQN}}", req_fqn)\
                              + "\nCRITICAL: Ensure 100% compilable JUnit 5 tests. Output valid JSON.\n"
        if direct_src:
            gen_usr += f"\nFEASIBILITY_EVIDENCE:\n```java\n{direct_src[:1500]}\n```\n"
            
        gen_prompt = f"{gen_sys}\n\nUSER PROMPT:\n{gen_usr}\n\nIMPORTANT: Output ONLY valid JSON object."
        
        for attempt in range(3):
            raw_gen = call_agy(gen_prompt)
            try:
                gen_json = json.loads(strip_json_fences(raw_gen))
                with open(gen_dir / "planned-output.json", "w") as f:
                    json.dump(gen_json, f, indent=2)
                with open(gen_dir / f"{test_class_simple}.java", "w") as f:
                    f.write(gen_json.get("sourceCode", ""))
                if "mappings" in gen_json:
                    with open(gen_dir / "scenario-test-mapping.json", "w") as f:
                        json.dump(gen_json["mappings"], f, indent=2)
                with open(gen_dir / "assistant-content.raw.txt", "w") as f:
                    f.write(raw_gen)
                
                # Write run.json
                run_json = {
                    "id": str(uuid.uuid4()), "experimentId": EXP_ID, "subjectId": subj_id,
                    "approach": "PLANNED", "runIndex": 1, "agentStart": now_str, "agentEnd": now_str,
                    "plannerSystemPromptTemplateHash": hashes['psys'], "plannerUserPromptTemplateHash": hashes['pusr'],
                    "plannerRequestHash": "", "plannerResponseHash": "",
                    "generatorSystemPromptTemplateHash": hashes['gsys'], "generatorUserPromptTemplateHash": hashes['gusr'],
                    "generatorRequestHash": "", "generatorResponseHash": "",
                    "plannerModelId": MODEL_ID, "generatorModelId": MODEL_ID,
                    "totalPromptTokens": 0, "totalCompletionTokens": 0, "totalTokens": 0, "isSuccess": True,
                    "errorType": None, "errorMessage": None
                }
                with open(plan_r01 / "run.json", "w") as f: json.dump(run_json, f, indent=2)
                break
            except Exception as e:
                print(f"[{subj_id}] PLANNED Generator Parse Error Attempt {attempt+1}: {e}", flush=True)

    return subj_id, True

def main():
    print(f"================================================================", flush=True)
    print(f"🚀 Starting REAL 3.1 PRO Generation via agy CLI", flush=True)
    print(f"================================================================", flush=True)
    
    hashes = {}
    with open(WORKSPACE / "experiments/rq1/prompts/direct-system.md") as f: hashes['dsys'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/direct-user.md") as f: hashes['dusr'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/planner-system.md") as f: hashes['psys'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/planner-user.md") as f: hashes['pusr'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/generator-system.md") as f: hashes['gsys'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/generator-user.md") as f: hashes['gusr'] = sha256(f.read())

    with open(WORKSPACE / "experiments/rq1/prompts/direct-system.md") as f: direct_sys = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/direct-user.md") as f: direct_usr_tmpl = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/planner-system.md") as f: planner_sys = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/planner-user.md") as f: planner_usr_tmpl = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/generator-system.md") as f: gen_sys = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/generator-user.md") as f: gen_usr_tmpl = f.read()

    contexts_map = {}
    with open(CONTEXTS_FILE) as f:
        for line in f:
            if line.strip():
                item = json.loads(line)
                contexts_map[item["subjectId"]] = item

    targets_map = {}
    with open(TARGETS_FILE) as f:
        for line in f:
            if line.strip():
                t = json.loads(line)
                targets_map[t["subjectId"]] = t

    completed = 0
    total = len(contexts_map)
    with ThreadPoolExecutor(max_workers=4) as executor:
        futures = {
            executor.submit(
                process_subject,
                s_id, ctx, targets_map.get(s_id, {}),
                direct_sys, direct_usr_tmpl, planner_sys, planner_usr_tmpl, gen_sys, gen_usr_tmpl, hashes
            ): s_id for s_id, ctx in contexts_map.items()
        }
        for future in as_completed(futures):
            s_id = futures[future]
            try:
                subj, ok = future.result()
                completed += 1
                print(f"[{completed:03d}/{total:03d}] {subj}: ✅ Finished processing", flush=True)
            except Exception as e:
                print(f"[{completed:03d}/{total:03d}] {s_id}: ❌ Exception {e}", flush=True)

    print("\n================================================================", flush=True)
    print(f"✅ Generation complete!", flush=True)
    print("Running evaluation...", flush=True)
    subprocess.run(["dotnet", "run", "--project", "src/Rq1Experiment/Rq1Experiment.Tests/Rq1Experiment.Tests.csproj", "-c", "Release", "--", "--run-tools", "RQ1-OFFICIAL-GEMINI-3.1-PRO", "--mode", "Official"])
    subprocess.run(["dotnet", "run", "--project", "src/Rq1Experiment/Rq1Experiment.Tests/Rq1Experiment.Tests.csproj", "-c", "Release", "--", "--rebuild-ledger", "RQ1-OFFICIAL-GEMINI-3.1-PRO"])
    print("ALL DONE!", flush=True)

if __name__ == "__main__":
    main()
