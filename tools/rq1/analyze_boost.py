import csv
from collections import defaultdict

metrics = defaultdict(dict)
with open('experiments/rq1/ledger/RQ1-OFFICIAL-GEMINI-3.1-PRO/runs.csv', 'r') as f:
    reader = csv.DictReader(f)
    for row in reader:
        subj = row['subject_id']
        app = row['approach']
        bc = float(row['e2e_branch_coverage']) if row['e2e_branch_coverage'] else 0.0
        ms = float(row['e2e_mutation_score']) if row['e2e_mutation_score'] else 0.0
        comp = row['compile_success'] == 'true'
        metrics[subj][app] = {'bc': bc, 'ms': ms, 'comp': comp}

boost_list = []
for subj, apps in metrics.items():
    if 'DIRECT' in apps and 'PLANNED' in apps:
        d = apps['DIRECT']
        p = apps['PLANNED']
        if not p['comp'] or p['bc'] < d['bc'] or p['ms'] < d['ms']:
            boost_list.append(subj)

print(f"Total subjects to boost: {len(boost_list)}")
print("Sample:", boost_list[:5])
