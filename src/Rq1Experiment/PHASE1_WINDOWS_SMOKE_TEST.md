# Phase 1 Windows Smoke Test

Run this checklist on Windows 10/11 from the workspace root. Do not mark Phase 1 complete until every item passes.

## 1. Build and automated checks

```powershell
dotnet build src/Rq1Experiment/Rq1Experiment.sln -c Release
dotnet run --project src/Rq1Experiment/Rq1Experiment.Tests/Rq1Experiment.Tests.csproj -c Release
```

- [ ] Build reports `0 Warning(s), 0 Error(s)`.
- [ ] Test harness ends with `All RQ1 Phase 0-1 scaffold checks passed.`.

## 2. Start WPF

```powershell
dotnet run --project src/Rq1Experiment/Rq1Experiment.Wpf/Rq1Experiment.Wpf.csproj -c Release
```

- [ ] `RQ1 Experiment Dashboard` opens without an exception dialog.
- [ ] Configuration, provider/model, API keys, pipeline controls, status, run grid/log and Phase Checklist are visible in the same `MainWindow`.

## 3. Interaction and responsiveness

Use `Smoke` mode, a real local repository path, any exact model ID, and at least one placeholder key. The Phase 1 scaffold does not send a remote request.

- [ ] `Load`, `Save`, and `Validate` respond.
- [ ] Changing provider loads the correct provider-specific key text.
- [ ] `Run Full Pipeline` starts and progress/log update.
- [ ] While running, the window can still move, resize and switch tabs.
- [ ] `Pause after stage`, `Resume`, and `Cancel` respond without losing completed stage artifacts.

## 4. Results window

- [ ] Complete one scaffold smoke run.
- [ ] Click `Xem kết quả`.
- [ ] `RQ1 Results` opens and shows the scaffold artifact row.
- [ ] `Rebuild from artifacts` returns the same artifact count.
- [ ] Closing Results does not close or reset MainWindow.

## Evidence

Save evidence under:

```text
experiments/rq1/reports/phase-1/
  windows-build.txt
  windows-tests.txt
  main-window.png
  results-window.png
  windows-smoke-test.md
```

After all boxes pass, update the remaining Windows-only Exit Gate boxes and both Phase 1 summary boxes in `RQ1_EXPERIMENT_PHASES.md`.
