# Scenario: Greenfield — build the core URL shortener

```bash
dotnet run --project src/Orchestrator.Cli -- greenfield --auto-approve --skip-real-tests
```

## The requirement

> Build the core URL shortener service from scratch: an endpoint to create a short URL
> from a long URL (optionally with a custom alias and an expiry date), an endpoint that
> redirects a short code to its original URL, and an endpoint that returns click
> analytics (total clicks, last click time, clicks broken down by referrer) for a given
> code.

No existing codebase context — `RequirementAnalysis` and `Design` run with an empty
`codebaseContext`.

## Decomposition

`RequirementAnalysis` treats this as well-specified (concrete endpoints, concrete
fields) and reports **0 ambiguities** — contrast with the [ambiguous scenario](ambiguous.md),
where the same agent flags vague language explicitly instead of guessing. `Design`
then produces the API/data-model shape, `Implementation` generates the code artifact,
and `Testing`/`Documentation` run in parallel off of it.

## Orchestration trace (actual run, offline fallback mode)

```
=== Run greenfield-20260913-083708 (greenfield) ===
Overall result: SUCCESS

-- Stage statuses --
  RequirementAnalysis  Succeeded
  Documentation        Succeeded
  ReleaseReadiness     Succeeded
  Testing              Succeeded
  Implementation       Succeeded
  Design               Succeeded

-- Audit trail --
  [08:37:08] RequirementAnalysis  StageStarted             stage started
  [08:37:08] RequirementAnalysis  StageSucceeded           Requirement normalized. 0 ambiguity(ies) identified.
  [08:37:08] Design               StageStarted             stage started
  [08:37:08] Design               StageSucceeded           Design complete: ...
  [08:37:08] Implementation       StageStarted             stage started
  [08:37:08] Implementation       ApprovalRequested        explicit approval gate configured for this stage
  [08:37:08] Implementation       ApprovalGranted          human approved the stage
  [08:37:08] Implementation       StageSucceeded           Implementation artifact generated at .../Implementation/implementation.md
  [08:37:08] Testing              StageStarted             stage started
  [08:37:08] Testing              StageSucceeded           Test plan generated (real test execution skipped by configuration).
  [08:37:08] Documentation        StageStarted             stage started
  [08:37:08] Documentation        StageSucceeded           Documentation written to .../Documentation/CHANGE.md
  [08:37:08] ReleaseReadiness     StageStarted             stage started
  [08:37:08] ReleaseReadiness     StageSucceeded           Release readiness decision: GO.

-- Reliability metrics --
  Success rate:     100 % (6/6 stages)
  Total retries:    0
  Total rollbacks:  0
```

Note the `Implementation` stage's `ApprovalRequested`/`ApprovalGranted` pair — every
run pauses there for a human checkpoint (auto-approved here via `--auto-approve` for a
non-interactive demo; drop the flag to approve/reject it yourself on the console).
`Testing` and `Documentation` both start immediately after `Implementation` succeeds —
in a real (non-offline) run with non-trivial LLM latency, timestamps on those two would
overlap, which is exactly the parallel-branch behavior the engine is designed for.

## Validation

- **Governance**: the approval gate on `Implementation` fired and was recorded; no
  policy guardrail (security-sensitive or destructive-operation keywords) matched this
  requirement's text, so no additional approval was required beyond the explicit gate.
- **Real signal, not simulated**: run the same command without `--skip-real-tests` and
  the `Testing` stage's `realTestsPassed` output reflects an actual `dotnet test` run
  against the solution (43 tests as of this writing), not a model's claim.
- **Artifacts on disk**: `artifacts/runs/<runId>/` contains the generated implementation
  note, test plan, and change doc for a human to review — see `report.json` in the same
  directory for the full machine-readable `RunReport` (stage statuses, decision lineage,
  audit log, metrics).
- **Release decision**: `ReleaseReadiness` independently re-checks that `Testing` and
  `Documentation` both succeeded and that no approval was denied anywhere in the run
  before declaring **GO** — it doesn't just trust that reaching this stage means
  everything upstream was fine.
