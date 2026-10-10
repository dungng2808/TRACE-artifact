# Integrated TRACE–EvoSuite results

This directory gathers the compact results for the 150-target, three-repetition integrated comparison (450 paired observations):

- `paired-comparison.csv`: the 450-row paired analysis input, byte-identical to `experiments/full-chain/paired-comparison.csv`.
- `raw-records.jsonl`: 900 terminal treatment records, byte-identical to the staged official campaign's `raw-records.jsonl`.
- `full-chain-results.json`: recalculated effect estimates and statistical tests using the paired CSV.
- `full-chain-archive-audit.json`: the recorded audit of the full campaign archive, including the EvoSuite search-budget checks.
- `evosuite-timing-consistency.json` and `.csv`: recorded stage/terminal timing checks for the 450 EvoSuite runs.

The copies under `experiments/full-chain/` and `TRACE-Paper/supporting/verification/` preserve the paths used by the existing verification scripts. Do not count the duplicate files as additional experimental observations. The 9.8 GB archive and complete extracted run workspaces are not included in this compact staging package, so the archive and per-stage timing audits cannot be independently rerun from this directory alone.
