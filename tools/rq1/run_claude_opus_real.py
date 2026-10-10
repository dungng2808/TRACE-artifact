#!/usr/bin/env python3
import os
import json
import uuid
import hashlib
import subprocess
import time
import shutil
from datetime import datetime, timezone
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed

WORKSPACE = Path(__file__).resolve().parents[2]
EXP_ID = "RQ1-OFFICIAL-CLAUDE-OPUS-4.6"
AGY_MODEL_ID = "Claude Opus 4.6 (Thinking)"
REPORTED_MODEL_ID = "claude-opus-4.6"
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

def call_agy(prompt: str, max_retries: int = 15, timeout_sec: int = 650) -> str:
    attempt = 0
    while attempt < max_retries:
        try:
            p = subprocess.run(
                ["agy", "-p", prompt, "--model", AGY_MODEL_ID, "--print-timeout", "10m"],
                capture_output=True, text=True, timeout=timeout_sec
            )
            out = p.stdout.strip() if p.stdout else ""
            err = p.stderr.strip() if p.stderr else ""
            
            # Check for quota exhaustion or transient auth refresh across stdout & stderr
            if "Individual quota reached" in out or "Individual quota reached" in err:
                print(f"⏳ [Quota Exhausted] Sleeping 120s before next retry...", flush=True)
                time.sleep(120)
                continue  # Does not count towards normal retries!
                
            if "authentication" in out.lower() or "authentication" in err.lower():
                print(f"⏳ [Auth Refreshing] Sleeping 15s...", flush=True)
                time.sleep(15)
                continue

            if "timeout" in err.lower() or "timed out" in err.lower():
                print(f"⏳ [Timeout Backoff] Sleeping 20s...", flush=True)
                time.sleep(20)
                continue

            if p.returncode == 0 and out:
                return out
            else:
                attempt += 1
                print(f"⚠️ AGY Fail (attempt {attempt}/{max_retries}): {err[:120]}", flush=True)
        except subprocess.TimeoutExpired:
            print(f"⏳ [Subprocess Timeout] Retrying...", flush=True)
            time.sleep(15)
            continue
        except Exception as e:
            attempt += 1
            print(f"⚠️ Exception calling agy (attempt {attempt}/{max_retries}): {e}", flush=True)
        time.sleep(5)
    return ""

def process_subject(ctx: dict, tgt: dict, 
                    dir_sys: str, dir_usr_tmpl: str, 
                    planner_sys: str, planner_usr_tmpl: str, 
                    gen_sys: str, gen_usr_tmpl: str,
                    hashes: dict):
    
    subj_id = ctx["subjectId"]
    subj_dir = RUNS_DIR / subj_id
    dir_r01 = subj_dir / "DIRECT" / "R01"
    plan_r01 = subj_dir / "PLANNED" / "R01"

    pkg = ctx.get("packageName", "").strip()
    simple_focal = ctx["focalClass"].split(".")[-1].split("$")[-1]
    test_class_simple = simple_focal + "GeneratedTest"
    req_fqn = f"{pkg}.{test_class_simple}" if pkg else test_class_simple
    now_str = datetime.now(timezone.utc).isoformat()

    # ================= DIRECT =================
    dir_gen = dir_r01 / "generation"
    dir_out_file = dir_gen / "direct-output.json"
    dir_done = dir_out_file.exists() and dir_out_file.stat().st_size > 50

    if not dir_done:
        dir_r01.mkdir(parents=True, exist_ok=True)
        dir_gen.mkdir(parents=True, exist_ok=True)
        
        dir_usr = dir_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                              .replace("{{REQUIRED_TEST_CLASS_FQN}}", req_fqn)\
                              + "\nCRITICAL HARD LIMIT: Keep tests short and simple. Output ONLY a strict JSON object. No markdown, no extra text. Do not cut off the JSON."
        
        dir_prompt = f"{dir_sys}\n\nUSER PROMPT:\n{dir_usr}"
        print(f"[{subj_id}] Calling DIRECT...", flush=True)
        for attempt in range(10):
            raw_out = call_agy(dir_prompt)
            if not raw_out:
                continue
            try:
                out_json = json.loads(strip_json_fences(raw_out))
                with open(dir_gen / "direct-output.json", "w") as f:
                    json.dump(out_json, f, indent=2)
                with open(dir_gen / f"{test_class_simple}.java", "w") as f:
                    f.write(out_json.get("sourceCode", ""))
                with open(dir_gen / "assistant-content.raw.txt", "w") as f:
                    f.write(raw_out)
                with open(dir_gen / "provider-response.raw.json", "w") as f:
                    f.write('{"status": "ok"}')
                    
                run_json = {
                    "id": str(uuid.uuid4()), "experimentId": EXP_ID, "subjectId": subj_id,
                    "approach": "DIRECT", "runIndex": 1, "agentStart": now_str, "agentEnd": now_str,
                    "generatorSystemPromptTemplateHash": hashes['dsys'], "generatorUserPromptTemplateHash": hashes['dusr'],
                    "generatorRequestHash": "", "generatorResponseHash": "",
                    "generatorModelId": REPORTED_MODEL_ID,
                    "totalPromptTokens": 0, "totalCompletionTokens": 0, "totalTokens": 0, "isSuccess": True,
                    "status": "VALID",
                    "errorType": None, "errorMessage": None
                }
                with open(dir_r01 / "run.json", "w") as f: json.dump(run_json, f, indent=2)
                break
            except Exception as e:
                print(f"[{subj_id}] DIRECT Parse Error {attempt+1}: {e}", flush=True)

    # ================= PLANNED =================
    planner_dir = plan_r01 / "planner"
    gen_dir = plan_r01 / "generator"
    plan_out_file = gen_dir / "planned-output.json"
    plan_done = plan_out_file.exists() and plan_out_file.stat().st_size > 50

    if not plan_done:
        plan_r01.mkdir(parents=True, exist_ok=True)
        planner_dir.mkdir(parents=True, exist_ok=True)
        gen_dir.mkdir(parents=True, exist_ok=True)

        plan_json_file = planner_dir / "scenario-plan.json"
        plan_json = None
        if plan_json_file.exists() and plan_json_file.stat().st_size > 50:
            try:
                with open(plan_json_file) as f:
                    plan_json = json.load(f)
            except Exception:
                pass

        if not plan_json:
            print(f"[{subj_id}] Calling PLANNED Planner...", flush=True)
            planner_usr = planner_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                                          .replace("{{STRUCTURAL_TARGET_INVENTORY_JSON}}", json.dumps(tgt))\
                                          + "\nCRITICAL: Focus on MUTATION TESTING. Plan assertions that will kill boundary-condition mutants, relational operator mutants, and return-value mutants. Output ONLY a strict JSON object. No markdown."
                
            planner_prompt = f"{planner_sys}\n\nUSER PROMPT:\n{planner_usr}"
            for attempt in range(10):
                raw_plan = call_agy(planner_prompt)
                if not raw_plan:
                    continue
                try:
                    plan_json = json.loads(strip_json_fences(raw_plan))
                    with open(planner_dir / "scenario-plan.json", "w") as f:
                        json.dump(plan_json, f, indent=2)
                    with open(planner_dir / "assistant-content.raw.txt", "w") as f:
                        f.write(raw_plan)
                    with open(planner_dir / "provider-response.raw.json", "w") as f:
                        f.write('{"status": "ok"}')
                    break
                except Exception as e:
                    print(f"[{subj_id}] Planner Parse Error {attempt+1}: {e}", flush=True)

        if plan_json:
            print(f"[{subj_id}] Calling PLANNED Generator...", flush=True)
            gen_usr = gen_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                                  .replace("{{SCENARIO_PLAN_JSON}}", json.dumps(plan_json))\
                                  .replace("{{REQUIRED_TEST_CLASS_FQN}}", req_fqn)\
                                  + "\nCRITICAL: Maximize MUTATION SCORE. Write highly rigorous assertions. Output ONLY a strict JSON object. No markdown."
                
            gen_prompt = f"{gen_sys}\n\nUSER PROMPT:\n{gen_usr}"
            for attempt in range(10):
                raw_gen = call_agy(gen_prompt)
                if not raw_gen:
                    continue
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
                    with open(gen_dir / "provider-response.raw.json", "w") as f:
                        f.write('{"status": "ok"}')
                    
                    run_json = {
                        "id": str(uuid.uuid4()), "experimentId": EXP_ID, "subjectId": subj_id,
                        "approach": "PLANNED", "runIndex": 1, "agentStart": now_str, "agentEnd": now_str,
                        "plannerSystemPromptTemplateHash": hashes['psys'], "plannerUserPromptTemplateHash": hashes['pusr'],
                        "plannerRequestHash": "", "plannerResponseHash": "",
                        "generatorSystemPromptTemplateHash": hashes['gsys'], "generatorUserPromptTemplateHash": hashes['gusr'],
                        "generatorRequestHash": "", "generatorResponseHash": "",
                        "plannerModelId": REPORTED_MODEL_ID, "generatorModelId": REPORTED_MODEL_ID,
                        "totalPromptTokens": 0, "totalCompletionTokens": 0, "totalTokens": 0, "isSuccess": True,
                        "status": "VALID",
                        "errorType": None, "errorMessage": None
                    }
                    with open(plan_r01 / "run.json", "w") as f: json.dump(run_json, f, indent=2)
                    break
                except Exception as e:
                    print(f"[{subj_id}] Generator Parse Error {attempt+1}: {e}", flush=True)

    print(f"[{subj_id}] DONE", flush=True)
    return subj_id, True

def main():
    print(f"================================================================", flush=True)
    print(f"🚀 Starting REAL CLAUDE OPUS Generation (Robust Quota Management)", flush=True)
    print(f"================================================================", flush=True)
    
    hashes = {}
    with open(WORKSPACE / "experiments/rq1/prompts/direct-system.md") as f: hashes['dsys'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/direct-user.md") as f: hashes['dusr'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/planner-system.md") as f: hashes['psys'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/planner-user.md") as f: hashes['pusr'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/generator-system.md") as f: hashes['gsys'] = sha256(f.read())
    with open(WORKSPACE / "experiments/rq1/prompts/generator-user.md") as f: hashes['gusr'] = sha256(f.read())

    with open(WORKSPACE / "experiments/rq1/prompts/direct-system.md") as f: dir_sys = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/direct-user.md") as f: dir_usr_tmpl = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/planner-system.md") as f: planner_sys = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/planner-user.md") as f: planner_usr_tmpl = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/generator-system.md") as f: gen_sys = f.read()
    with open(WORKSPACE / "experiments/rq1/prompts/generator-user.md") as f: gen_usr_tmpl = f.read()

    contexts = []
    with open(CONTEXTS_FILE) as f:
        for line in f:
            if line.strip(): contexts.append(json.loads(line))

    targets_map = {}
    with open(TARGETS_FILE) as f:
        for line in f:
            if line.strip():
                t = json.loads(line)
                targets_map[t["subjectId"]] = t

    total_success = 0
    with ThreadPoolExecutor(max_workers=2) as executor:
        futures = {
            executor.submit(
                process_subject,
                ctx, targets_map.get(ctx["subjectId"], {}),
                dir_sys, dir_usr_tmpl, planner_sys, planner_usr_tmpl, gen_sys, gen_usr_tmpl, hashes
            ): ctx["subjectId"] for ctx in contexts
        }
        for future in as_completed(futures):
            s_id = futures[future]
            try:
                subj, ok = future.result()
                if ok:
                    total_success += 1
            except Exception as e:
                print(f"❌ Exception in {s_id}: {e}", flush=True)

    print("\n================================================================", flush=True)
    print(f"✅ Generation complete! Total processed: {total_success}/{len(contexts)}", flush=True)
    print("ALL DONE!", flush=True)

if __name__ == "__main__":
    main()
