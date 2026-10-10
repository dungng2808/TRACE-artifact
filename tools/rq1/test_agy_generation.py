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

with open(WORKSPACE / "experiments/rq1/prompts/direct-system.md") as f:
    direct_sys = f.read()

with open(WORKSPACE / "experiments/rq1/prompts/direct-user.md") as f:
    direct_usr_tmpl = f.read()

pkg = ctx.get("packageName", "").strip()
simple_name = ctx["focalClass"].split(".")[-1].split("$")[-1] + "GeneratedTest"
req_fqn = f"{pkg}.{simple_name}" if pkg else simple_name

direct_usr = direct_usr_tmpl.replace("{{COMMON_CONTEXT_JSON}}", json.dumps(ctx))\
                            .replace("{{REQUIRED_TEST_CLASS_FQN}}", req_fqn)\
                            + "\nHARD_LIMIT: Generate no more than 4 @Test methods.\n"

full_prompt = f"{direct_sys}\n\nUSER PROMPT:\n{direct_usr}\n\nIMPORTANT: Output ONLY the strict raw JSON object without any Markdown fences or formatting."

print("Calling agy -p with real Gemini 3.7 Flash...")
p = subprocess.run(["agy", "-p", full_prompt], capture_output=True, text=True)
print("Return code:", p.returncode)
print("Output length:", len(p.stdout))
print("Output preview:\n", p.stdout[:500])
