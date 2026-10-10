# Verification scripts and reports

This folder keeps the path layout of the authors' manuscript workspace so that
the verification commands in the top-level `README.md` work unchanged. Only the
material needed to check the reported numbers is included; the manuscript
source, figures, and LaTeX template are not.

- `supporting/scripts/`: scripts that recalculate the manuscript's numbers from
  the exported data in this repository (component results, the TRACE--EvoSuite
  statistics, EvoSuite timing consistency, the full-chain archive audit, and
  the post hoc B2 token estimate). Each script defaults to the repository root
  as its workspace.
- `supporting/verification/`: the recorded outputs of those scripts, with
  instructions and expected package versions in `verification/README.md`.
- `supporting/RQ3_PROVENANCE.md`: SHA-256 values of the RQ3 artifacts that are
  not published here (cohort manifest, scoring script, aggregator, and joined
  prediction/gold records). Paths in it refer to the original RQ3 workspace.
