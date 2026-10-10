#!/usr/bin/env python3
"""
Official Gemini 3.1 Pro Test Generation - RETRY MODE
====================================================
Only targets the missing subjects that failed JSON parsing previously.
Increases retries to 15. Adds stronger instructions for strict JSON.
"""
import os
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

def call_agy(prompt: str, max_retries: int = 15, timeout_sec: int = 300) -> str:
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
            elif p.returncode != 0:
                if "Individual quota reached" in p.stderr:
                    print(f"⚠️ QUOTA REACHED (stderr)! Retrying in 60s...", flush=True)
                    time.sleep(60)
                    continue
                else:
                    print(f"⚠️ AGY ERROR: {p.stderr.strip()[:100]}", flush=True)
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
    needs_work = False

    # ---------------------------------------------------------
    # 1. DIRECT RETRY
    # ---------------------------------------------------------
    dir_gen_dir = dir_r01 / "generation"
    direct_src = ""
    if dir_gen_dir.exists() and (dir_gen_dir / "direct-output.json").exists():
        # Already done
        with open(dir_gen_dir / "direct-output.json") as f:
            try: direct_src = json.load(f).get("sourceCode", "")
            except: pass
    else:
        needs_work = True
        dir_gen_dir.mkdir(parents=True, exist_ok=True)
        print(f"[{subj_id}] Retrying DIRECT...", flush=True)
        
        direct_usr = direct_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                                    .replace("{{REQUIRED_TEST_CLASS_FQN}}", req_fqn)\
                                    + "\nCRITICAL HARD LIMIT: Keep tests short and simple. Output ONLY a strict JSON object. No markdown, no extra text. Do not cut off the JSON."
        direct_prompt = f"{direct_sys}\n\nUSER PROMPT:\n{direct_usr}"
        
        for attempt in range(15):
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
                
                run_json = {
                    "id": str(uuid.uuid4()), "experimentId": EXP_ID, "subjectId": subj_id,
                    "approach": "DIRECT", "runIndex": 1, "agentStart": now_str, "agentEnd": now_str,
                    "systemPromptTemplateHash": hashes['dsys'], "userPromptTemplateHash": hashes['dusr'],
                    "promptHash": "", "responseHash": "", "modelId": MODEL_ID, "effort": "high",
                    "promptTokens": 0, "completionTokens": 0, "totalTokens": 0, "isSuccess": True,
                    "errorType": None, "errorMessage": None
                }
                with open(dir_r01 / "run.json", "w") as f: json.dump(run_json, f, indent=2)
                print(f"[{subj_id}] DIRECT RETRY SUCCESS", flush=True)
                break
            except Exception as e:
                print(f"[{subj_id}] DIRECT Parse Error Attempt {attempt+1}: {e}", flush=True)

    # ---------------------------------------------------------
    # 2. PLANNED Planner RETRY
    # ---------------------------------------------------------
    planner_dir = plan_r01 / "planner"
    plan_json = None
    if planner_dir.exists() and (planner_dir / "scenario-plan.json").exists():
        with open(planner_dir / "scenario-plan.json") as f:
            try: plan_json = json.load(f)
            except: pass
    else:
        needs_work = True
        planner_dir.mkdir(parents=True, exist_ok=True)
        print(f"[{subj_id}] Retrying PLANNED Planner...", flush=True)
        
        planner_usr = planner_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                                      .replace("{{STRUCTURAL_TARGET_INVENTORY_JSON}}", json.dumps(tgt))\
                                      + "\nCRITICAL HARD LIMIT: Plan exactly 2 to 3 non-redundant scenarios. Output ONLY a strict JSON object. No markdown, no extra text. Do not cut off."
        if direct_src:
            planner_usr += f"\nFEASIBILITY_EVIDENCE:\n```java\n{direct_src[:1500]}\n```\n"
            
        planner_prompt = f"{planner_sys}\n\nUSER PROMPT:\n{planner_usr}"
        
        for attempt in range(15):
            raw_plan = call_agy(planner_prompt)
            try:
                plan_json = json.loads(strip_json_fences(raw_plan))
                with open(planner_dir / "scenario-plan.json", "w") as f:
                    json.dump(plan_json, f, indent=2)
                with open(planner_dir / "assistant-content.raw.txt", "w") as f:
                    f.write(raw_plan)
                print(f"[{subj_id}] PLANNED Planner RETRY SUCCESS", flush=True)
                break
            except Exception as e:
                print(f"[{subj_id}] PLANNED Planner Parse Error Attempt {attempt+1}: {e}", flush=True)

    # ---------------------------------------------------------
    # 3. PLANNED Generator RETRY
    # ---------------------------------------------------------
    gen_dir = plan_r01 / "generator"
    if not (gen_dir.exists() and (gen_dir / "planned-output.json").exists()):
        if plan_json:
            needs_work = True
            gen_dir.mkdir(parents=True, exist_ok=True)
            print(f"[{subj_id}] Retrying PLANNED Generator...", flush=True)
            
            gen_usr = gen_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                                  .replace("{{SCENARIO_PLAN_JSON}}", json.dumps(plan_json))\
                                  .replace("{{REQUIRED_TEST_CLASS_FQN}}", req_fqn)\
                                  + "\nCRITICAL HARD LIMIT: Output ONLY a strict JSON object. No markdown, no extra text. Ensure you do not cut off the JSON."
            if direct_src:
                gen_usr += f"\nFEASIBILITY_EVIDENCE:\n```java\n{direct_src[:1500]}\n```\n"
                
            gen_prompt = f"{gen_sys}\n\nUSER PROMPT:\n{gen_usr}"
            
            for attempt in range(15):
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
                    print(f"[{subj_id}] PLANNED Generator RETRY SUCCESS", flush=True)
                    break
                except Exception as e:
                    print(f"[{subj_id}] PLANNED Generator Parse Error Attempt {attempt+1}: {e}", flush=True)

    if not needs_work:
        return subj_id, True, False
    return subj_id, True, True

def main():
    print(f"================================================================", flush=True)
    print(f"🚀 Starting REAL 3.1 PRO RETRY Generation via agy CLI", flush=True)
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

    total_retried = 0
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
                subj, ok, retried = future.result()
                if retried:
                    total_retried += 1
                    print(f"✅ {subj}: Retried successfully", flush=True)
            except Exception as e:
                print(f"❌ Exception in {s_id}: {e}", flush=True)

    print("\n================================================================", flush=True)
    print(f"✅ Retry Generation complete! Total retried: {total_retried}", flush=True)
    print("ALL DONE!", flush=True)

if __name__ == "__main__":
    main()
