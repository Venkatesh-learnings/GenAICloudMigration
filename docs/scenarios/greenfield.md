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
where the same agent flags vague language explicitly instead of guessing.

`TaskDecomposition` then derives **5 tasks** from this requirement (versus 4 for the
[brownfield](brownfield.md) one) and expands the graph with one implementation stage per
task, wired to the dependencies it computed. The full plan, including the execution
waves, is written to `artifacts/runs/<runId>/TaskDecomposition/task-plan.md`.

`CodebaseAnalysis` is **`Skipped`** here — its entry gate only opens when the change is
landing in an existing system, and this one isn't. `Design` still runs, because it
declares `DependencyRule.AllSettled`: a stage that was deliberately gated out as
inapplicable must not block the pipeline behind it.

## Orchestration trace (actual run, offline fallback mode)

```
Overall result: SUCCESS

-- Stage statuses --
  RequirementAnalysis  Succeeded
  CodebaseAnalysis     Skipped      <- entry gate: nothing to analyze on a greenfield change
  TaskDecomposition    Succeeded
  Design               Succeeded    <- ran anyway: AllSettled dependency rule
  Implementation:T1    Succeeded
  Implementation:T2    Succeeded
  Implementation:T3    Succeeded
  Implementation:T4    Succeeded
  Implementation:T5    Succeeded
  Implementation       Succeeded
  Testing              Succeeded
  Documentation        Succeeded
  SecurityReview       Skipped      <- entry gate: change touches no security surface
  ReleaseReadiness     Succeeded

TaskDecomposition: Decomposed into 5 task(s) across 3 execution wave(s);
                   expanded the graph with 5 implementation stage(s).
*                  GraphExpanded    graph expanded with 5 stage(s): Implementation:T1 ...
ReleaseReadiness:  Release readiness decision: GO.
```

Every `Implementation:T*` stage carries an `ApprovalRequested`/`ApprovalGranted` pair —
generating code is the high-impact action in this pipeline, so each one takes a human
checkpoint (auto-approved here via `--auto-approve` for a non-interactive demo; drop the
flag to approve or reject each yourself on the console). The read-only stages don't
prompt, which is deliberate: see the note on scoping approvals in
[architecture.md](../architecture.md#key-decisions-and-why).

## Validation

- **Governance**: an approval checkpoint fired and was recorded for each code-generating
  stage; no policy guardrail (security-sensitive, destructive-operation, or embedded
  credential) matched this requirement, so nothing escalated beyond those checkpoints.
- **Real signal, not simulated**: run the same command without `--skip-real-tests` and
  the `Testing` stage's `realTestsPassed` output reflects an actual `dotnet test` run
  against the solution, not a model's claim — and the stage's **exit gate** fails the
  stage if it reports having run the suite without the suite passing.
- **Artifacts on disk**: `artifacts/runs/<runId>/` contains the generated implementation
  note, test plan, and change doc for a human to review — see `report.json` in the same
  directory for the full machine-readable `RunReport` (stage statuses, decision lineage,
  audit log, metrics).
- **Release decision**: `ReleaseReadiness` independently re-checks that `Testing` and
  `Documentation` both succeeded and that no approval was denied anywhere in the run
  before declaring **GO** — it doesn't just trust that reaching this stage means
  everything upstream was fine.
