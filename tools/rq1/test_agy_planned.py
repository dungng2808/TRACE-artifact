#!/usr/bin/env python3
import json
import subprocess
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parents[2]
CONTEXTS_FILE = WORKSPACE / "experiments/rq1/context/contexts-official.jsonl"
TARGETS_FILE = WORKSPACE / "experiments/rq1/targets/targets-official.jsonl"

with open(CONTEXTS_FILE) as f:
    ctx = json.loads(f.readline())

with open(TARGETS_FILE) as f:
    tgt = json.loads(f.readline())

# Planner
with open(WORKSPACE / "experiments/rq1/prompts/planner-system.md") as f:
    planner_sys = f.read()

with open(WORKSPACE / "experiments/rq1/prompts/planner-user.md") as f:
    planner_usr_tmpl = f.read()

planner_usr = planner_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                              .replace("{{STRUCTURAL_TARGET_INVENTORY_JSON}}", json.dumps(tgt))\
                              + "\nHARD_LIMIT: Generate no more than 3 scenarios.\n"

planner_prompt = f"{planner_sys}\n\nUSER PROMPT:\n{planner_usr}\n\nIMPORTANT: Output ONLY the strict raw JSON object without any Markdown fences or formatting."

print("1. Calling Planner with real Gemini 3.7 Flash...")
p1 = subprocess.run(["agy", "-p", planner_prompt], capture_output=True, text=True)
print("Planner output:\n", p1.stdout)

plan_json = json.loads(p1.stdout.strip().removeprefix("```json").removesuffix("```").strip())

# Generator
with open(WORKSPACE / "experiments/rq1/prompts/generator-system.md") as f:
    gen_sys = f.read()

with open(WORKSPACE / "experiments/rq1/prompts/generator-user.md") as f:
    gen_usr_tmpl = f.read()

pkg = ctx.get("packageName", "").strip()
simple_name = ctx["focalClass"].split(".")[-1].split("$")[-1] + "GeneratedTest"
req_fqn = f"{pkg}.{simple_name}" if pkg else simple_name

gen_usr = gen_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                      .replace("{{FINALIZED_SCENARIO_PLAN_JSON}}", json.dumps(plan_json))\
                      .replace("{{REQUIRED_TEST_CLASS_FQN}}", req_fqn)

gen_prompt = f"{gen_sys}\n\nUSER PROMPT:\n{gen_usr}\n\nIMPORTANT: Output ONLY the strict raw JSON object without any Markdown fences or formatting."

print("\n2. Calling Generator with real Gemini 3.7 Flash...")
p2 = subprocess.run(["agy", "-p", gen_prompt], capture_output=True, text=True)
print("Generator output:\n", p2.stdout)
