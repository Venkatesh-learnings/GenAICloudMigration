# Final Engineering Summary

## Plan and rationale

The assignment's grading weight is on the orchestration layer, not the URL shortener
itself, so the plan was: build a real (if intentionally scoped-down) product first,
then build a genuinely non-linear orchestration engine around it, and prove the engine
with real behavior rather than a diagram — every governance mechanism claimed in
[architecture.md](architecture.md) (parallel sync, retry, rollback, approval,
guardrails, re-planning) is exercised by an automated test or a reproducible CLI flag,
not just asserted in prose. Concretely, in order:

1. URL shortener domain → application → infrastructure → API, each layer tested before
   moving to the next.
2. The orchestration engine (`WorkflowGraph`, `WorkflowEngine`, guardrails, metrics,
   approvals) built and hand-verified via the CLI before writing agents around it.
3. Hybrid LLM agents with offline fallback, so the engine's correctness never depends on
   network access or a valid API key.
4. Three scenarios wired through the same graph, proving the orchestration is generic
   over the kind of SDLC work, not scenario-specific logic in disguise.
5. Docker packaging, then documentation last, once there was real behavior to document
   truthfully instead of aspirationally.

## Artifacts produced

- Working URL shortener: `src/UrlShortener.*`, 43 passing tests in `tests/UrlShortener.Tests`.
- Working orchestration engine: `src/Orchestrator.*`, 43 passing tests in `tests/Orchestrator.Tests`.
- Three runnable scenarios with real captured traces: [docs/scenarios/](scenarios/).
- `Dockerfile`, `Dockerfile.orchestrator`, `docker-compose.yml` — all three built and
  run successfully against a live Docker daemon during development, including a full
  HTTP round trip against the containerized API.
- This documentation set: [architecture.md](architecture.md), [setup.md](setup.md),
  [testing-and-limitations.md](testing-and-limitations.md), and the three scenario docs.

## Risks, trade-offs, and validation

Covered in depth in [testing-and-limitations.md](testing-and-limitations.md) — the
short version: the biggest deliberate risk taken was **not** letting the
`Implementation` stage write to the real source tree, trading "looks more autonomous"
for "a human always reviews before anything real changes." The biggest deliberate
trade-off was building the policy guardrails as transparent keyword rules rather than a
second LLM call — less semantically capable, but fully auditable and independently
testable, which mattered more given the assignment's emphasis on governance.

Two real bugs were found and fixed during development, not left as `TODO`s:

1. The offline fallback path in `RequirementAnalysisAgent`/`DesignAgent` was
   accidentally re-parsing its own fallback JSON instead of running the intended
   keyword-heuristic path, silently suppressing ambiguity detection in offline mode.
   Fixed by gating JSON parsing on whether the LLM was actually used.
2. The engine's skip-cascade fix (added after a background test pass surfaced that
   a stage depending on a `Skipped` stage could deadlock forever) initially only worked
   when another branch of the graph happened to keep the scheduling loop alive long
   enough for a second `PropagateBlocking` pass. An isolated skip chain still deadlocked.
   Fixed by having the main loop take one more tick whenever anything changed that tick
   (a new block, a new skip) before concluding a genuine deadlock — verified by a
   dedicated test suite covering both the isolated case and the cascading case.

Both were caught by combining independent test-writing (background agents working from
the actual code, not from my description of it) with my own manual CLI verification —
neither channel alone would have caught both.

## Assumptions

- "Working prototype" means genuinely runnable end-to-end (API + orchestrator, with and
  without a real LLM key, with and without Docker) — not a design document with
  illustrative pseudocode.
- The three required scenarios (greenfield/brownfield/ambiguous) should exercise the
  *same* orchestration graph and engine, not three bespoke pipelines — that's what
  actually proves the engine is a general orchestration layer rather than
  scenario-specific scripting.
- A 2-3 day prototype scope justifies SQLite over a networked database, and a CLI over
  a persisted/resumable service — both are called out explicitly as trade-offs rather
  than silently assumed acceptable.

## Limitations

See [testing-and-limitations.md](testing-and-limitations.md#known-limitations) for the
full list with rationale; the headline ones: Implementation output isn't applied to the
real codebase automatically (by design), ambiguity is surfaced but not blocking,
approval is process-local (no durable pending-approval state for async human review),
and policy guardrails are keyword-based rather than semantic.
