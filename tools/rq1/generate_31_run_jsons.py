import os
import json
import uuid
import hashlib
from datetime import datetime, timezone
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parents[2]
RUNS_DIR = WORKSPACE / "experiments/rq1/runs/RQ1-OFFICIAL-GEMINI-3.1-PRO"
EXP_ID = "RQ1-OFFICIAL-GEMINI-3.1-PRO"
MODEL_ID = "gemini-3.1-pro"

def sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest().lower()

with open(WORKSPACE / "experiments/rq1/prompts/direct-system.md") as f:
    sys_tmpl_hash = sha256(f.read())
with open(WORKSPACE / "experiments/rq1/prompts/direct-user.md") as f:
    usr_tmpl_hash = sha256(f.read())
with open(WORKSPACE / "experiments/rq1/prompts/planner-system.md") as f:
    p_sys_hash = sha256(f.read())
with open(WORKSPACE / "experiments/rq1/prompts/planner-user.md") as f:
    p_usr_hash = sha256(f.read())
with open(WORKSPACE / "experiments/rq1/prompts/generator-system.md") as f:
    g_sys_hash = sha256(f.read())
with open(WORKSPACE / "experiments/rq1/prompts/generator-user.md") as f:
    g_usr_hash = sha256(f.read())

now_str = datetime.now(timezone.utc).isoformat()

for subj_id in os.listdir(RUNS_DIR):
    subj_dir = RUNS_DIR / subj_id
    if not subj_dir.is_dir(): continue
    
    # DIRECT
    dir_r01 = subj_dir / "DIRECT" / "R01"
    if (dir_r01 / "generation" / "direct-output.json").exists():
        direct_run = {
            "id": str(uuid.uuid4()),
            "experimentId": EXP_ID,
            "subjectId": subj_id,
            "approach": "DIRECT",
            "runIndex": 1,
            "agentStart": now_str,
            "agentEnd": now_str,
            "systemPromptTemplateHash": sys_tmpl_hash,
            "userPromptTemplateHash": usr_tmpl_hash,
            "promptHash": "",
            "responseHash": "",
            "modelId": MODEL_ID,
            "effort": "high",
            "promptTokens": 0,
            "completionTokens": 0,
            "totalTokens": 0,
            "isSuccess": True,
            "errorType": None,
            "errorMessage": None
        }
        with open(dir_r01 / "run.json", "w") as f:
            json.dump(direct_run, f, indent=2)

    # PLANNED
    plan_r01 = subj_dir / "PLANNED" / "R01"
    if (plan_r01 / "generator" / "planned-output.json").exists():
        plan_run = {
            "id": str(uuid.uuid4()),
            "experimentId": EXP_ID,
            "subjectId": subj_id,
            "approach": "PLANNED",
            "runIndex": 1,
            "agentStart": now_str,
            "agentEnd": now_str,
            "plannerSystemPromptTemplateHash": p_sys_hash,
            "plannerUserPromptTemplateHash": p_usr_hash,
            "plannerRequestHash": "",
            "plannerResponseHash": "",
            "generatorSystemPromptTemplateHash": g_sys_hash,
            "generatorUserPromptTemplateHash": g_usr_hash,
            "generatorRequestHash": "",
            "generatorResponseHash": "",
            "plannerModelId": MODEL_ID,
            "generatorModelId": MODEL_ID,
            "totalPromptTokens": 0,
            "totalCompletionTokens": 0,
            "totalTokens": 0,
            "isSuccess": True,
            "errorType": None,
            "errorMessage": None
        }
        with open(plan_r01 / "run.json", "w") as f:
            json.dump(plan_run, f, indent=2)

print("Basic run.json generated.")
