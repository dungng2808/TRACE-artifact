# Datasets

Inputs of the three component studies and the integrated TRACE--EvoSuite
comparison. Third-party benchmark code (SF110 projects, Defects4J checkouts,
LIBRO-generated tests) is not redistributed; the manifests identify exactly
which projects, revisions, and methods were used.

| Study | Benchmark | Units | Where the definition is | Details |
|---|---|---|---|---|
| RQ1 | SF110 (SF110-20130704) | 150 focal methods from 58 projects | `experiments/full-chain/manifests/full-chain-rq1-150.v1.json` | [`rq1/`](rq1/README.md) |
| RQ2 | LIBRO--Defects4J | 100 failing-test units from 6 projects | `datasets/rq2/` | [`rq2/`](rq2/README.md) |
| RQ3 | Verification cases from pinned public Java repositories | 73 cases from 14 projects | `datasets/rq3/phase6_exploratory_v2_dataset/envelopes/` | [`rq3/`](rq3/README.md) |
| TRACE vs. EvoSuite | Same 150 SF110 focal methods as RQ1 | 150 targets x 3 repetitions | Same manifest as RQ1 | [`rq1/`](rq1/README.md) |

Run outputs and analysis results are under `results/`, and protocols and
configurations under `experiments/`.
