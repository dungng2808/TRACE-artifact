# RQ1 and TRACE--EvoSuite cohort (SF110)

RQ1 and the integrated TRACE--EvoSuite comparison use the same 150 focal
methods from 58 projects of the SF110 corpus, version SF110-20130704 (Fraser
and Arcuri, "A Large-Scale Evaluation of Automated Unit Test Generation Using
EvoSuite", ACM TOSEM 24(2), 2014, doi:10.1145/2685612).

This folder contains no data files of its own. The cohort is defined by:

- [`../../experiments/full-chain/manifests/full-chain-rq1-150.v1.json`](../../experiments/full-chain/manifests/full-chain-rq1-150.v1.json):
  one record per focal method (`PT-O-001` to `PT-O-150`) with the SF110
  project, source file, focal class, method signature, start and end lines,
  LOC, cyclomatic complexity and stratum, the SHA-256 of the source file, and
  the structural targets (branch sites, predicates, and source ranges) used for
  Structural-Target Satisfaction.

The subject IDs in this manifest match those in the RQ1 run exports
[`../../results/rq1/csv/`](../../results/rq1/csv/) and in the TRACE--EvoSuite
pair export [`../../results/full-chain/paired-comparison.csv`](../../results/full-chain/paired-comparison.csv).

## Not included

The SF110 project sources are not redistributed. The experiments ran on a
build-ready copy of the corpus (`datasets/rq1/sf110/java-ready/projects` in the
original workspace). To check that a local copy matches, compare each focal
source file with the `sourceSha256` value in the manifest. Selection settings
(150 official subjects, at most three per project, selection seed 4902026) are
recorded in [`../../experiments/rq1/config/rq1.json`](../../experiments/rq1/config/rq1.json).
