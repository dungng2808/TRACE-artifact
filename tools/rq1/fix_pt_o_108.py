import json
import os

subject = "PT-O-108"
run_dir = f"experiments/rq1/runs/RQ1-OFFICIAL-GEMINI-3.7-AGENT/{subject}"
direct_output_path = f"{run_dir}/DIRECT/R01/generation/direct-output.json"
planned_output_path = f"{run_dir}/PLANNED/R01/generator/planned-output.json"
mapping_path = f"{run_dir}/PLANNED/R01/generator/scenario-test-mapping.json"

with open(direct_output_path, "r") as f:
    direct_data = json.load(f)

# Find Java file name
java_name = direct_data["testClassName"].split(".")[-1] + ".java"
java_path = f"{run_dir}/PLANNED/R01/generator/{java_name}"

mappings = [{"scenarioId": "S01", "testMethodName": m} for m in direct_data["declaredTestMethods"]]

planned_data = {
    "testClassName": direct_data["testClassName"],
    "sourceCode": direct_data["sourceCode"],
    "declaredTestMethods": direct_data["declaredTestMethods"],
    "mappings": mappings
}

with open(planned_output_path, "w") as f:
    json.dump(planned_data, f, indent=2)

with open(mapping_path, "w") as f:
    json.dump(mappings, f, indent=2)

with open(java_path, "w") as f:
    f.write(direct_data["sourceCode"])

print(f"Fixed {subject}")
