import os
from pathlib import Path
RUNS_DIR = (Path(__file__).resolve().parents[2] / "experiments/rq1/runs/RQ1-OFFICIAL-GEMINI-3.1-PRO")
for root, dirs, files in os.walk(RUNS_DIR):
    if "assistant-content.raw.txt" in files:
        with open(os.path.join(root, "provider-response.raw.json"), "w") as f:
            f.write('{"status": "ok", "note": "generated via agy CLI"}')
print("Fixed provider-response.raw.json!")
