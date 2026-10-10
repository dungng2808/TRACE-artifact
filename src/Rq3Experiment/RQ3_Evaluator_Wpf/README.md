# RQ3 Phase 6/7 Control Center (WPF .NET 8)

Desktop application for running, monitoring, and auditing the **RQ3 Phase 6 & Phase 7 technical/exploratory evaluation** across 73 evidence-qualified cases with B0 (Basic Tool Gate), B1 (General Reviewer), and B2 (Specialized Multi-Agent).

> **Cohort note**: This is a technical/exploratory evaluation on 73 evidence-qualified cases. One research-team member assigned and confirmed the final gold category for every included case; AI assisted with candidate screening and mapping checks. This is not a 144-case human-confirmatory study.

---

## 1. Run the application

### Build and Launch:

```powershell
# Build Batch Runner and WPF UI in Release mode
dotnet build tools/rq3/RQ3BatchRunner/RQ3BatchRunner.csproj -c Release
dotnet build src/Rq3Experiment/RQ3_Evaluator_Wpf/RQ3Evaluator.Wpf.csproj -c Release

# Run WPF Application
dotnet run --project src/Rq3Experiment/RQ3_Evaluator_Wpf/RQ3Evaluator.Wpf.csproj -c Release
```

---

## 2. Core Architecture & Safety Contract

1. **Subprocess Isolation**: WPF UI communicates with `RQ3BatchRunner.dll`, `score_phase6_amended_main_v1.ps1`, and `run_phase6_stability_v1.ps1` via `ProcessStartInfo.ArgumentList`.
2. **API Key Safety**: The OpenRouter API key is never logged, displayed, stored in JSON, or passed directly on the command line. Only the file path (`--api-key-file`) is referenced.
3. **Tamper-Evident Protocol Gate**: Predictions must be finalized and certified in `prediction_freeze_v1.json` with a SHA-256 hash before private gold is accessed for scoring.
4. **Resume Capability**: Real incremental checkpointing via `--run-dir` and `--resume` skips already completed `(case_id, verifier)` pairs without re-calling the API.

---

## 3. Four-Tab Workflow

* **Tab 1: Frozen Evidence & Preflight**
  * Auto-discovers workspace, Phase 2 dataset, amended manifest (`phase6_main_amended_manifest_v1.json`), evidence bundles, and tool projections.
  * Validates all 73 cases, checking SHA-256 hashes for public cases, bundles, projections, and freeze manifest.
* **Tab 2: Run Pipeline**
  * Step-by-step or one-click (`Run All Remaining Stages`) execution.
  * Explicit user confirmation dialog before opening private gold for scoring.
  * Safe cancellation preserving atomic checkpoints.
* **Tab 3: Live Log & Progress**
  * Real-time console output (capped at 10,000 lines), progress bar, elapsed time, API error counters, and live cost calculation.
* **Tab 4: Results & Artifacts Dashboard**
  * Detailed summary cards for B0, B1, and B2 (Decision Accuracy, Wilson 95% CI, Clustered Bootstrap 95% CI, Routing Accuracy, Macro Attribution F1, Selective Accuracy, and Cost).
  * Paired McNemar tests and Holm correction display.
  * 3-Repetition Stability Evaluation table (94.44% baseline).
  * 219 Joined Predictions DataGrid with search/audit filters.
  * Full audit artifact checklist with SHA-256 verification.
