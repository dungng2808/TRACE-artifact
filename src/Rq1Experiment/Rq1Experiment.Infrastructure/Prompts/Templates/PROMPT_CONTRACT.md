# Frozen Phase 5 prompt contract

DIRECT receives only `COMMON_CONTEXT_JSON`. The Planner receives the identical common context plus the frozen structural-target inventory, including its source-level intraprocedural CFG. The PLANNED Generator receives the identical common context plus the finalized scenario plan. Existing `src/test` bodies are excluded.

The depth-one retrieval rule resolves only same-type calls, calls through statically declared project types, and static project-type calls by exact method name and arity. Ambiguous, external, chained, or otherwise unsafe calls remain `UNRESOLVED`; no guessed implementation is inserted.

Imported generic variable types are resolved by their explicit imports before simple-name fallback. Related project types include accessible constructor bodies so constructor chaining and default state are evidence rather than guesses. This bounded behavior context is shared identically by both treatments.

DIRECT and the PLANNED Generator share the same test-code safety policy for accessibility, build compatibility, runtime prerequisites, implementation-derived oracles, mocking, determinism, and exact output identity. This shared policy is a validity control, not treatment evidence. DIRECT still receives no structural target inventory or scenario plan.

Build compatibility is an explicit execution contract: each context records `requiredTestApi`, `testAnnotation`, and `executionProvider`. Generated source is rejected before tool execution when it violates a `JUNIT_JUPITER` contract. This prevents a JUnit 4 annotation from compiling but being undiscovered by the JUnit Platform provider.

Each context also records all same-name `focalMethodOverloadSignatures`. When overloads exist, Planner inputs and generated invocations must type any null argument explicitly; an ambiguous single bare-null focal invocation is rejected before compilation.

The Planner may only create source-mapped, implementation-derived, constructible scenarios. Every scenario must record a complete `cfgPathNodeIds` path from CFG entry to an exit, and the parser rejects unknown nodes or disconnected consecutive edges. The source-level CFG is structural guidance under its declared limitations; it does not override feasibility evidence. The Planner must not turn inaccessible APIs, missing framework containers, guessed fixtures, or desirable behavior into a scenario. The Generator must preserve exact scenario-to-test ownership and exact fully qualified class identity in every mapping.

Every later LLM call must retain rendered system/user prompts, template hashes, context hash, request hash, provider ID, base endpoint, requested/response model IDs, token usage, and API key slot ID (never the raw key).
