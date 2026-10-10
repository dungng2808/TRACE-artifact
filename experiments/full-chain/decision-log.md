# FullChain Experiment: Decision Log

**Status:** ALL DECISIONS FROZEN PRIOR TO OFFICIAL BENCHMARK RUNS  
**Date:** 2026-09-19 (ICT)  

---

## Logged Decisions

### D-01: Cohort Derivation from Official RQ1 Dataset
- **Decision:** Materialize `full-chain-rq1-150.v1.json` containing exactly the 150 official targets approved in RQ1.
- **Rationale:** Ensures direct continuity and comparability with prior established subject characteristics while prohibiting data leakage.
- **Constraints:** No historical prompt text, model completions, or prior scores are carried forward into the manifest.

### D-02: Strict TGSLR Single-Method Repair Strategy
- **Decision:** Only Targeted Guided Search / Local Replacement (TGSLR) is authorized as the repair mechanism.
- **Rationale:** Replacing only the failing focal test method preserves the stability and structure of passing companion tests within the suite.
- **Constraints:** Repair is invoked only on genuine parse errors, compilation failures, or deterministic assertion failures where a scenario-to-method mapping exists. Timeout or tool failures immediately abort to terminal states.

### D-03: Blinded B2 Multi-Agent Verification Gate
- **Decision:** The B2 Multi-Agent Verifier must receive a blinded `CommonEnvelope` identical in structure and metadata for both FullChain and EvoSuite suites.
- **Rationale:** Eliminates evaluator bias and ensures that safety and quality gating are evaluated under neutral conditions.
- **Constraints:** ABSTAIN maps to terminal `UNVERIFIED`. `REPAIR_REQUIRED` triggers local repair only if the route is `LOCAL_REPAIR` and budget remains; otherwise, it is terminal `VERIFIER_REJECTED`.

### D-04: Rigid 900-Second Hard Wall-Clock Cap per Unit
- **Decision:** Each target $\times$ repetition $\times$ treatment unit is constrained to a 900-second hard wall-clock budget enforced by `IGlobalBudgetGovernor`.
- **Rationale:** Balances thoroughness with practical feasibility, ensuring fair allocation between generative exploration and deterministic search.

### D-05: Strict Dual Accounting & Missing Evidence Policy
- **Decision:** Delivered-system outcomes (ITT) and Candidate-stage diagnostics are reported in separate columns.
- **Rationale:** Prevents conflating candidate-stage technical feasibility with delivered-system utility. Non-delivered suites receive 0 in ITT. Tool errors are flagged as `MissingEvidence` rather than penalized as quality failures.

### D-06: Toolchain and Environment Pinning
- **Decision:** Pin JDK 17.0.20+8, Maven 3.9.16, EvoSuite 1.2.0, JaCoCo 0.8.15, and PIT 1.25.3 with exact SHA-256 signatures in `tool-lock.json`.
- **Rationale:** Assures bit-exact reproducibility of build and measurement environments.

