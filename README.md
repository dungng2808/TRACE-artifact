# TRACE: code and data artifact

Code and data accompanying the article *TRACE: Structural Planning,
Tool-Grounded Multi-Agent Verification, and Traceability-Guided Local Repair
for LLM-Based Java Unit Testing* (Nguyen Tuan Dung, Vuong Kieu Anh, Doan Huu
Minh Quang, Ngo Duc Chinh, Tran Van Han, Trung Tran, Nguyen Thi Nguyet).

Repository: https://github.com/dungng2808/TRACE-artifact

## License

Source code is released under the MIT License (`LICENSE`). Data, results, and
documentation are released under CC BY 4.0 (`LICENSE-DATA`). Third-party
material, including code and text originating from SF110, Defects4J, LIBRO,
and other public projects, keeps its original license;
see `LICENSE-DATA`.

This repository is a curated snapshot of the code and data supporting the
article. The manuscript source and compiled PDF are not included.

## Contents

The directory layout mirrors the wider `paper-2` workspace so that the
manuscript's verification scripts can resolve their relative paths:

- `TRACE-Paper/supporting/`: verification scripts, recorded verification
  reports, and the RQ3 provenance record; see `TRACE-Paper/README.md`.
- `src/` and `tools/`: source code and analysis engines, without build output,
  local credentials or downloaded tool binaries.
- `results/rq1/csv/`: the two 300-row CSVs used by the component checker.
- `results/rq2/raw/`: the 297-row run CSV and three-row replacement CSV used by
  the component checker.
- `results/rq3/in_paper/`: the three RQ3 runs displayed in the paper's tables
  (Gemini 3.5 Flash, repaired DeepSeek V4 Pro, Qwen3.8 Flash): metrics reports
  and non-gold frozen prediction files used by the paper and the post hoc B2
  estimate.
- `results/rq3/sensitivity_reported_in_text/`: metrics reports of the other
  complete runs whose B2-minus-B1 differences are quoted in the text
  (DeepSeek V4 Flash, Gemini 3.7 Google, Gemini 3.7 local medium).
- `results/rq3/excluded_from_comparison/`: metrics report of the earlier GLM
  run, which the paper names but omits from the numerical comparison because
  87 of its 219 case-level outputs were not fully valid.
- `results/full-chain/`: the 450-pair CSV, 900 terminal records, and recorded
  statistical, archive, and timing checks for the integrated comparison.
- `datasets/`: an overview of the inputs of every study is in
  `datasets/README.md`.
- `datasets/rq2/`: the RQ2 LIBRO--Defects4J task manifests, including the
  99-task main batch and the one replacement task that form the 100 analyzed
  units, and the preparation chain from the 120-task candidate frame; see
  `datasets/rq2/README.md`.
- `datasets/rq3/phase6_exploratory_v2_dataset/envelopes/`: RQ3 evidence
  envelopes used to calibrate that estimate.
- `experiments/full-chain/`: the 450-pair CSV, protocol, manifests, tool lock,
  redacted/example configuration, and a compact subset of EvoSuite evidence.
  `manifests/full-chain-rq1-150.v1.json` defines the 150 SF110 focal methods
  (project, class, method signature, source lines, source SHA-256, and
  structural targets) shared by RQ1 and the TRACE--EvoSuite comparison; its
  subject IDs match those in `results/rq1/csv/`. The SF110 corpus itself
  (version SF110-20130704; Fraser and Arcuri, TOSEM 2014,
  doi:10.1145/2685612) is not redistributed.

Absolute paths of the original author workstation have been replaced by the
placeholder `<WORKSPACE>` (the root of the original `paper-2` workspace) in run
records, EvoSuite command files, one RQ1 CSV column (`run_directory`), and
three EvoSuite-generated test sources. No numerical value was changed.

`SHA256SUMS.txt` records hashes for the curated source and result files;
`SOURCE_INVENTORY.tsv` records where each file was copied from. The manifests,
top-level README, Git metadata, and generated build output are excluded.

## Checks that can run from this directory

Run these from the directory containing this README:

```sh
python3 TRACE-Paper/supporting/scripts/verify-component-results.py \
  --workspace . --output /tmp/trace-component-results.json \
  --rq3-metrics \
  results/rq3/in_paper/KQ_Thucnghiem_VerHeThong2_deepseek_v4_pro_exploratory_v2_free_repaired_20260831/metrics_v2.json \
  results/rq3/in_paper/KQ_Thucnghiem_VerHeThong2_gemini_3_5_flash_google_ai_studio_exploratory_v2_20260826_101118/results/metrics_v2.json \
  results/rq3/in_paper/KQ_Thucnghiem_VerHeThong2_qwen3_8_flash_bai_free_20260904/results/metrics_v2.json

dotnet build TRACE-Paper/supporting/scripts/VerifyFullChain/VerifyFullChain.csproj \
  --artifacts-path /tmp/trace-full-chain-build
dotnet /tmp/trace-full-chain-build/bin/VerifyFullChain/debug/VerifyFullChain.dll \
  results/full-chain/paired-comparison.csv /tmp/trace-full-chain-results.json

python3 TRACE-Paper/supporting/scripts/estimate-evosuite-b2-tokens.py \
  --workspace . --output /tmp/trace-evosuite-b2-estimate.json
```

The first command requires Python with NumPy and SciPy; the third requires
`tiktoken==0.14.0`. The .NET command requires .NET 8. Check exact package
versions and outputs against `TRACE-Paper/supporting/verification/README.md`.
`TRACE-Paper/supporting/scripts/verify-rq3-qwen.py` needs the private
`predictions_joined_private_gold_v2.csv`, which is not included, so it cannot
run from this directory.
Install the Python verification dependencies with
`python3 -m pip install -r requirements-verification.txt` in a virtual
environment.

The token columns of the RQ1 CSVs in `results/rq1/csv/` hold the
provider-reported usage summed over each run's planning and generation
responses. The raw provider responses are not included here;
`TRACE-Paper/supporting/verification/rq1-current-token-accounting.json`
records their sums and hashes. The EvoSuite timing audit
(`audit-evosuite-timing.py`) needs per-stage `execution-meta.json` files that
are also not included; its recorded output is in
`TRACE-Paper/supporting/verification/`.

These scripts recalculate from exported data. They do **not** independently
authenticate the historical experiment execution, establish the provider
provenance of reported token usage, or rerun all Java targets.

## Intentionally absent

- The 9.8 GB full-chain archive and full extracted workspaces, which are not
  included because of their size and third-party content; the compact command
  and retained-test subset here does not replace that archive.
- API keys, local configuration, `.env` files, binary build output, downloaded
  toolchains, and third-party project snapshots (SF110 projects, Defects4J
  checkouts, and LIBRO-generated test files).
- RQ3 private gold and prediction/gold joined files. Consequently, the staged
  RQ3 metrics can be inspected but not independently rescored from gold here.
- Complete RQ1 raw responses and full RQ2/RQ3 run histories. The included
  exports support the documented numerical checks, not a fresh execution.

## Contact

Questions about the artifact or requests concerning material listed under
*Intentionally absent* can be sent to the corresponding author, Nguyen Thi
Nguyet (nguyetnt41@fpt.edu.vn).
