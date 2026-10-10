# RQ3 Verifier Protocol v2

## Frozen common envelope

B0, B1, and B2 receive the byte-identical JSON envelope identified by `evidence_envelope_sha256`. It contains the public case, exact generated test source, compiler and selected-JUnit evidence, per-test coverage, raw surviving-mutant records, and bounded diagnostics. It never contains gold category, injection operator, relevance adjudication, or repair gold.

## Verifiers

- B0: compile failure => `COMPILATION_ERROR`; selected JUnit failure => `SETUP_OR_RUNTIME_ERROR`; otherwise `VERIFIED`. It intentionally ignores semantic, coverage, and mutation evidence.
- B1: one general reviewer returns decision, cause, route, and rationale.
- B2: conformance, runtime, and coverage/mutation specialists return only `DETECTED`, `CLEAR`, or `INSUFFICIENT` findings. A deterministic aggregator maps findings to the final decision and route.

## Aggregation priority

`COMPILATION_ERROR` > `SHARED_INFRASTRUCTURE_ERROR` > `SETUP_OR_RUNTIME_ERROR` > `TARGET_NOT_REACHED` > `WEAK_ORACLE` > `PLAN_TEST_MISMATCH` > `ASSERTION_ERROR`. If no defect exists, all three specialists must be `CLEAR` for `VERIFIED`; otherwise the result is `ABSTAIN`.

## Mutation blinding

The public envelope exposes mutant identity, location, transformation, and survival status. Relevance/non-equivalence adjudication remains private gold. `UNKNOWN` is not used as a substitute for missing raw mutation evidence.

## Primary metric

False-passing cases are passing tests whose gold cause is `PLAN_TEST_MISMATCH`, `TARGET_NOT_REACHED`, `ASSERTION_ERROR`, or `WEAK_ORACLE`. False-pass precision/recall/F1 measure detection of this class. False-acceptance rate is the fraction of gold false-passing cases predicted `VERIFIED`.
