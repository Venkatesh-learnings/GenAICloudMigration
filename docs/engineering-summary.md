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
6. A deliberate audit pass against the assignment's own wording, treating my own work as
   the thing under review — then closing every gap it found (below).

## Audit against the requirement, and what it changed

After the system was working end to end, I re-read the assignment requirement by
requirement and checked each claim against the code rather than against my memory of
writing it. That found four gaps worth fixing — two of them against numbered Core
Requirements:

1. **Task Decomposition (Core Req #2) was not actually happening.** The SDLC stages were
   a fixed graph, byte-identical for every scenario; nothing converted a requirement into
   tasks with dependencies and sequencing. Fixed by adding `TaskDecompositionAgent`,
   which derives a validated task DAG and then *expands the workflow graph at runtime*
   with one implementation stage per task. The executed graph now differs per requirement,
   and the parallelism inside the implementation phase is whatever the task dependencies
   imply. This required new engine capability: queued, atomically-validated graph
   expansion committed between scheduling ticks.
2. **Codebase Reasoning (Core Req #3) was an assertion, not an analysis.** The
   "codebase context" was a paragraph I had hand-written; nothing read the repository.
   Fixed by adding `CodebaseScanner` (real projects, project references, HTTP routes, EF
   entity sets, requirement-relevant files) and a `CodebaseAnalysis` stage that reasons
   over those scanned facts. Its output now changes when the code changes.
3. **Entry/exit gates existed but were never configured.** The assignment explicitly
   requires "an explicit dependency graph with entry/exit gates"; every shipped stage was
   using the defaults. Fixed by wiring four real gates (see
   [architecture.md](architecture.md#entry-and-exit-gates-in-the-shipped-pipeline)) —
   which in turn required a real engine fix, because a skipped stage previously poisoned
   everything downstream of it. Hence `DependencyRule.AllSettled`.
4. **Guardrails and safe-stop never fired in any runnable demo.** No shipped rule could
   return `Deny`, and none of the three scenario texts could trip the security rule, so
   both paths were untested claims. Fixed by adding `HardcodedCredentialRule` (denies and
   trips safe-stop), a fourth `security` scenario that genuinely escalates, and an
   `--inject-policy-violation` flag to demonstrate denial without committing a secret.

Fixing #4 surfaced a fifth problem the audit hadn't predicted: with a sensitive
requirement, the security rule demanded approval on *every* stage, including read-only
analysis. That is how you train reviewers to click through approvals without reading
them, so policy escalation is now scoped to stages marked `HighImpact`.

## Artifacts produced

- Working URL shortener: `src/UrlShortener.*`, with unit and real-HTTP integration tests
  in `tests/UrlShortener.Tests`.
- Working orchestration engine: `src/Orchestrator.*`, covered by `tests/Orchestrator.Tests`
  (scheduling and concurrency, dependency rules, runtime graph expansion, retry/rollback,
  approvals, guardrail scoping and denial, re-planning, task-plan validation, the
  codebase scanner).
- Four runnable scenarios with real captured traces: [docs/scenarios/](scenarios/) —
  the three the assignment requires, plus one that exercises the governance path.
- `Dockerfile`, `Dockerfile.orchestrator`, `docker-compose.yml` — all three built and
  run successfully against a live Docker daemon during development, including a full
  HTTP round trip against the containerized API.
- This documentation set: [architecture.md](architecture.md), [setup.md](setup.md),
  [testing-and-limitations.md](testing-and-limitations.md), and the scenario docs.

## Risks, trade-offs, and validation

Covered in depth in [testing-and-limitations.md](testing-and-limitations.md) — the
short version: the biggest deliberate risk taken was **not** letting the
`Implementation` stage write to the real source tree, trading "looks more autonomous"
for "a human always reviews before anything real changes." The biggest deliberate
trade-off was building the policy guardrails as transparent keyword rules rather than a
second LLM call — less semantically capable, but fully auditable and independently
testable, which mattered more given the assignment's emphasis on governance.

Real bugs found and fixed during development, not left as `TODO`s:

1. The offline fallback path in `RequirementAnalysisAgent`/`DesignAgent` was
   accidentally re-parsing its own fallback JSON instead of running the intended
   keyword-heuristic path, silently suppressing ambiguity detection in offline mode.
   Fixed by gating JSON parsing on whether the LLM was actually used.
2. A stage depending on a `Skipped` stage could deadlock the run forever (readiness
   required a `Succeeded` dependency, and nothing ever cascaded the skip). My first fix
   was itself incomplete — it only worked when another branch of the graph happened to
   keep the scheduling loop alive for one more tick, so an *isolated* skip chain still
   deadlocked. Fixed properly by having the loop take an extra tick whenever a tick
   changed anything before concluding a deadlock. At that point the deadlock branch
   became provably unreachable for any validly constructed graph (the constructor rejects
   cycles, so induction over topological depth applies); it stays in as a guard against a
   future scheduling change reintroducing one.

These were caught by combining independent test-writing (background agents working from
the actual code, not from my description of it) with my own manual CLI verification —
neither channel alone would have caught all of them. The pattern worth noting: in two
cases the tests I asked for came back reporting that my fix did not do what I said it
did, which is exactly what an independent check is for.

## Assumptions

- "Working prototype" means genuinely runnable end-to-end (API + orchestrator, with and
  without a real LLM key, with and without Docker) — not a design document with
  illustrative pseudocode.
- The required scenarios (greenfield/brownfield/ambiguous) should exercise the *same*
  orchestration engine and stage definitions, not three bespoke pipelines — that's what
  proves the engine is a general orchestration layer rather than scenario-specific
  scripting. Note the distinction from the executed graph, which *should* differ per
  scenario: same definitions and same engine, different derived work.
- A 2-3 day prototype scope justifies SQLite over a networked database, and a CLI over
  a persisted/resumable service — both are called out explicitly as trade-offs rather
  than silently assumed acceptable.

## Limitations

See [testing-and-limitations.md](testing-and-limitations.md#known-limitations) for the
full list with rationale; the headline ones: Implementation output isn't applied to the
real codebase automatically (by design), ambiguity is surfaced but not blocking,
approval is process-local (no durable pending-approval state for async human review),
and policy guardrails are keyword-based rather than semantic.
