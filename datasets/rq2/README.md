# RQ2 benchmark manifests (LIBRO--Defects4J)

Task manifests for the RQ2 repair study. Each task is one initially failing
LLM-generated bug-reproduction test from LIBRO (Kang et al., ICSE 2023,
doi:10.1109/ICSE48619.2023.00194) on a Defects4J bug (Just et al., ISSTA 2014,
doi:10.1145/2610384.2628055). A task records the Defects4J project, fixed and
buggy Git revisions, the LIBRO test artifact name and failing method, the focal
source files, the scenario specification (the public bug report), the
scenario--test traceability link, the build system, and the report paths.

## Files used by the reported 100-unit analysis

| File | Tasks | Role |
|---|---|---|
| `benchmark.postdevelopment-eval-ready-99.json` | 99 | Main post-development batch |
| `benchmark.replacement-ready-1.json` | 1 | Replacement task `D4J-JSOUP-32-48` |

Together they contain the 100 units (Compress 19, Gson 17, JacksonCore 20,
JacksonDatabind 20, JacksonXml 2, Jsoup 22) whose task IDs match the run exports
in `results/rq2/raw/`. `D4J-COMPRESS-6-4` in
`benchmark.postdevelopment-eval-100.json` failed readiness checks and was
replaced.

## Preparation chain

| File | Tasks | Stage |
|---|---|---|
| `benchmark.candidate.json` | 120 | Reconstructed candidate frame |
| `benchmark.structural-ready.json` | 119 | After structural sanitization |
| `benchmark.ready-100.json` | 100 | After preflight readiness |
| `benchmark.ready-100.provenance.json` | -- | SHA-256 links between the stages above |
| `benchmark.development-candidate-20.json`, `benchmark.development-ready-10.json` | 19, 10 | Development subsets |
| `benchmark.postdevelopment-eval-100.json` | 100 | Post-development evaluation frame |
| `benchmark.replacement-preflight.json` | 1 | Preflight of the replacement task |
| `reconstruction-audit.csv` | -- | Reconstruction status of each candidate bug |
| `structural-sanitization.csv` | -- | Kept/removed decision of each candidate task |

The manifests are frozen copies and have not been edited. Fields such as
`javaHome`, `repositoryPath`, and the paths in the provenance file refer to the
original Windows execution machine. The SHA-256 values in
`benchmark.ready-100.provenance.json` match the included
`benchmark.structural-ready.json` and `benchmark.ready-100.json`.

## Not included

The Defects4J project checkouts and the LIBRO-generated test files are not
redistributed; obtain them from Defects4J and the LIBRO replication package
using the project, revision, and artifact identifiers above. Bug-report text
in `scenarioSpecification` originates from the projects' public issue trackers
and remains subject to their terms.
