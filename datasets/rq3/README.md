# RQ3 verification cases

The exploratory RQ3 cohort has 73 cases from 14 Java projects: 65
controlled-injection cases and 8 natural cases. Each case starts from a
developer-written test in a pinned public Java repository.

`phase6_exploratory_v2_dataset/envelopes/` contains one JSON evidence envelope
per case (73 files). The verifiers B0, B1, and B2 received the byte-identical
envelope of each case. According to
[`../../experiments/rq3/protocol/RQ3_PROTOCOL_V2.md`](../../experiments/rq3/protocol/RQ3_PROTOCOL_V2.md),
an envelope contains the public case, the generated test source, compiler and
selected-JUnit evidence, per-test coverage, raw surviving-mutant records, and
bounded diagnostics.

## Not included

The envelopes never contain the gold category, injection operator, mutant
relevance adjudication, or repair gold. These private gold labels are not
published, so the RQ3 metrics in `results/rq3/` can be inspected but not
rescored from gold with this artifact. Requests can be sent to the
corresponding author.
