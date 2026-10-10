# RQ1 Experiment WPF

Implementation follows `RQ1_EXPERIMENT_PHASES.md` phase by phase.

## Current status

- Phase 0 artifacts exist as **draft**. The protocol is not frozen because the benchmark revision, exact provider/model, primary decision rule and official statistical policy are not finalized.
- Phase 1 WPF scaffold is implemented and has been run on Windows.
- Phase 2 repository preflight is real: isolated compile, three existing-test runs,
  JaCoCo smoke and PIT dry-run.
- Phase 3 focal-method selection is real and frozen: JavaParser extraction,
  deterministic stratified sampling, source hashes and review ledger.
- Phase 4 independent target extraction is real and frozen: three-run
  determinism, source-mapped target IDs and `FULL_BRANCH_SITE_COVERAGE`
  semantics. See `experiments/rq1/targets/`.
- Stages from Phase 5 onward remain scaffold implementations until their
  corresponding phases are completed. A later scaffold stage reporting PASS is
  not experimental evidence.

## Build and check

```powershell
dotnet build src/Rq1Experiment/Rq1Experiment.sln -c Release
dotnet run --project src/Rq1Experiment/Rq1Experiment.Tests/Rq1Experiment.Tests.csproj -c Release
dotnet run --project src/Rq1Experiment/Rq1Experiment.Wpf/Rq1Experiment.Wpf.csproj -c Release
```

The final command must run on Windows because WPF is Windows-only.

## Local credentials

Plaintext keys are stored one per line in:

```text
Rq1Experiment.Wpf/data/api-keys/gemini.txt
Rq1Experiment.Wpf/data/api-keys/deepseek.txt
Rq1Experiment.Wpf/data/api-keys/openrouter.txt
```

These files are ignored by Git and must never be included in experiment artifacts.
