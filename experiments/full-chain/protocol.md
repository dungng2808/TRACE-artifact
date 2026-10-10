# FullChain Experiment: Pre-Registered Protocol

**Version:** 1.0.0-frozen  
**Date:** 2026-09-19 (ICT)  
**Cohort:** `full-chain-rq1-150.v1.json` (150 official targets from RQ1)  
**Status:** FROZEN BEFORE PILOT EXECUTION

---

## 1. Primary Objectives & Hypotheses

The primary research question is evaluating whether an integrated full-chain test generation framework:
$$\text{PLANNED Generation} \longrightarrow \text{Targeted TGSLR Repair} \longrightarrow \text{Blinded B2 Multi-Agent Verification}$$
outperforms an industrial baseline generator (EvoSuite) under identical computational budgets and evaluated through an identical blinded safety gate.

- **Hypothesis $H_1$ (System Effectiveness):** FullChain achieves higher Delivered Verified-Suite Rate (VSR) and Compiler Satisfaction Rate (CSR) than EvoSuite.
- **Hypothesis $H_2$ (Adequacy):** FullChain delivers suites with significantly higher Branch Coverage (BC), Mutation Score (MS), and Structural Target Satisfaction (STS) on focal methods.
- **Scope Claim:** End-to-end system effectiveness. No claims are made regarding standalone prompt superiority or universal superiority of verification or repair.

---

## 2. Experimental Design

### 2.1 Cohort & Unit of Analysis
- **Subjects:** 150 official targets derived from SF110 benchmark (100_jgaap, 101_netweaver, 102_squirrel-sql, 103_sweethome3d, 105_freemind, 106_checkstyle, 107_weka, 109_pdfsam, etc.).
- **Repetitions:** 3 fixed repetitions per target: seeds `{42, 43, 44}`.
- **Unit of Inference:** Target-level aggregation across the 3 repetitions prior to hypothesis testing.

### 2.2 Treatments
1. **FullChain Treatment:**
   - RQ1 PLANNED Generation (structural scenario planning + JUnit test suite synthesis).
   - Candidate Build & Test execution.
   - RQ2 TGSLR Repair (invoked only for parse, compile, or deterministic assertion failures where scenario–focal-method mapping is unambiguous; maximum 2 rounds).
   - Common Evaluator: Maven Surefire, JaCoCo line/branch coverage, PIT mutation analysis.
   - RQ3 Blinded B2 Multi-Agent Verifier (specialists evaluate CommonEnvelope; deterministic aggregation).
   - Final state: `DELIVERED` or terminal failure.

2. **EvoSuite Comparator:**
   - EvoSuite 1.2.0 search (search budget: 420 seconds).
   - Common Evaluator (identical Maven test execution, JaCoCo, and PIT configuration).
   - Blinded B2 Multi-Agent Verifier (identical CommonEnvelope schema, zero repair loops).
   - Final state: `DELIVERED` or terminal failure.

### 2.3 Strict Blinded Evaluation
- Both treatments submit their final test suite to the common evaluator and B2 verifier inside an identical `CommonEnvelope`.
- The envelope includes: test source code, class/method hashes, compile/test execution logs, JaCoCo XML metrics, PIT XML metrics, and branch obligations.
- The envelope strictly strips: treatment name, prompts, scenario text, raw LLM outputs, repair history, round count, and internal execution paths.

---

## 3. Resource Budgets & Governor

Each (Target $\times$ Repetition $\times$ Treatment) unit is governed by `IGlobalBudgetGovernor`:
- **Hard Wall-Clock Timeout:** 900 seconds (15 minutes).
- **FullChain Stage Allocations:**
  - Generation: 180s
  - Repair (total across rounds): 240s
  - Evaluation (total across rounds): 180s
  - B2 Verification: 240s
  - Orchestration overhead: 60s
- **EvoSuite Stage Allocations:**
  - Search: 420s
  - Evaluation: 180s
  - B2 Verification: 240s
  - Orchestration overhead: 60s
- **LLM Provider Ceilings (FullChain):**
  - Maximum 13 semantic provider requests per unit.
  - Maximum 2 transient retries per request.
  - Maximum 128,000 total cumulative tokens (input + output).

---

## 4. Outcome Accounting & Missing Evidence Policy

### 4.1 Dual Reporting Framework
1. **Delivered-System Outcomes (Intention-to-Treat):**
   - Non-delivered suites (due to generation failure, unrepairable compile failure, verifier rejection, or budget exhaustion) are assigned a score of `0.0`.
   - Metrics: VSR (%), CSR (%), ESR (%), STS (%), BC (%), MS (%).
2. **Candidate-Stage Diagnostics:**
   - Records observed coverage and mutation metrics of generated candidates before gating, regardless of subsequent verification verdicts.

### 4.2 Missing Evidence Handling
- Tool crashes, measurement errors, or infrastructure aborts are classified as `MissingEvidence`.
- `MissingEvidence` is **never** zero-filled and **never** dropped via complete-case deletion.
- A maximum of 2 technical re-executions are permitted, strictly using the persisted candidate source and commands without LLM regeneration.
- Any unresolved `MissingEvidence` in the official campaign renders the comparison incomplete and precludes headline statistical claims.

---

## 5. Statistical Testing Protocol
- **Hierarchical Cluster Bootstrap:** Resampling at `project → target → repetition` levels with 2,000 iterations to estimate 95% confidence intervals for paired differences.
- **Paired Permutation Tests:** Calculated on target-level aggregates.
- **Multiplicity Correction:** Holm-Bonferroni step-down procedure applied across family outcomes (CSR, ESR, STS, BC, MS).
- **Secondary Semantic Audit:** Post-campaign blinded review of 30 randomly sampled test suites (15 FullChain, 15 EvoSuite) by 2 independent reviewers for assertions, readability, and test smells.

