# RQ1 Protocol — DRAFT

This protocol is intentionally not frozen yet.

## Treatment

- DIRECT: common repository context to direct test generation.
- PLANNED: common repository context plus structural planning, followed by planned generation.
- Repair and verifier feedback are disabled for RQ1.

## Controls

- Same repository revision and focal method.
- Same provider, exact model ID, decoding parameters and repository-context policy.
- Same generated-test cap and total token ceiling.
- Same compile, execution, coverage and mutation tools.

## Required decisions before freeze

- [x] Dataset/benchmark revision: ProjectTest Java subset, Git `a893144b017c44e9a042d605a04baa447f525433`; current official artifact contains 19 project records.
- [ ] Exact provider, base endpoint and model ID.
- [ ] Subject count and repetitions.
- [ ] Primary outcome and decision rule.
- [ ] Structural-target measurement predicates.
- [ ] Mutation manifest policy.
- [ ] Statistical handling of repetitions and project clustering.

When every decision is complete, set `isFrozen=true` in `rq1.json`, record hashes, and append the freeze event to `decision-log.md`.
