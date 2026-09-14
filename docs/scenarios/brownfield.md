# Scenario: Brownfield — bulk-expire on the existing service

```bash
dotnet run --project src/Orchestrator.Cli -- brownfield --auto-approve --skip-real-tests
```

## The requirement

> Add a bulk-expire operation to the existing URL shortener: given a cutoff date,
> deactivate every short URL created before that date in one call, without breaking the
> existing single-URL expire endpoint or its idempotency guarantees.

This time `RequirementAnalysis` and `Design` also receive real **codebase context**
(`ScenarioLibrary.cs`), describing the actual architecture they have to fit into:

> Existing service: ASP.NET Core Web API (UrlShortener.Api) over a layered
> UrlShortener.Domain / UrlShortener.Application / UrlShortener.Infrastructure split.
> UrlShortenerService.ExpireAsync(code) already deactivates a single ShortUrl by code
> via IShortUrlRepository, backed by EF Core over SQLite (UrlShortenerDbContext,
> ShortUrls table keyed by Code with a CreatedAt column). DELETE /api/urls/{code} in
> UrlsController already exposes single-expire. A bulk operation must reuse
> IShortUrlRepository rather than bypass it, and must not lock the ShortUrls table for
> an unbounded scan on a large dataset.

## Codebase reasoning — reading the repo, not a description of it

This is the scenario that exercises **Core Requirement #3 (Codebase Reasoning)**. The
`CodebaseAnalysis` stage runs `CodebaseScanner` over the actual repository before
anything reasons about impact: it enumerates projects and their references, the HTTP
routes each exposes, the EF entity sets behind them, and ranks files by how many of the
requirement's distinctive terms they contain. On the run below it reported:

```
CodebaseAnalysis: Codebase analyzed: 9 project(s), 12 file(s) related to this requirement.
   rationale: Scanned 9 project(s) off disk; LLM unavailable, so impact was derived
   directly from the scan (keyword-relevant files and the projects owning them).
```

Those numbers come from disk, so they change when the code changes — which is the point.
The full inventory is written to `artifacts/runs/<runId>/CodebaseAnalysis/codebase-inventory.md`,
and with an API key set the model reasons over that inventory to name impacted modules,
APIs, data flows and regression risks, grounded in facts it was given rather than
invented file names.

## Decomposition

`TaskDecomposition` turns the requirement into a validated task DAG and then *becomes*
the graph — here it derived four tasks across three waves (the real `task-plan.md`
artifact from the run):

| Task | Title | Depends on |
|---|---|---|
| T1 | Add a bulk-expire operation to the existing URL shortener: given a cutoff date | — |
| T2 | Deactivate every short URL created before that date in one call | T1 |
| T3 | Without breaking the existing single-URL expire endpoint or its idempotency guarantees | T1 |
| T4 | Integrate and validate the delivered pieces end to end | T2, T3 |

Sequencing: `T1` → `T2 ∥ T3` → `T4`. The engine then runs `Implementation:T2` and
`Implementation:T3` concurrently, because the decomposition said they're independent —
the parallelism is derived from the requirement, not hardcoded in the pipeline.

## Orchestration trace (actual run, offline fallback mode)

```
-- Stage statuses --
  RequirementAnalysis  Succeeded
  CodebaseAnalysis     Succeeded
  TaskDecomposition    Succeeded
  Design               Succeeded
  Implementation:T1    Succeeded
  Implementation:T2    Succeeded
  Implementation:T3    Succeeded
  Implementation:T4    Succeeded
  Implementation       Succeeded
  Testing              Succeeded
  Documentation        Succeeded
  SecurityReview       Skipped
  ReleaseReadiness     Succeeded
```

`SecurityReview` is `Skipped` because its entry gate found nothing security-sensitive in
this change — and `ReleaseReadiness` still ran anyway, because it declares
`DependencyRule.AllSettled`. A skipped-because-inapplicable review must not block a
release; a *failed* one still would.

Same engine and same governance mechanisms as the greenfield run — but not the same
executed graph, because the decomposition and the entry gates responded to this
requirement.

## Validation

- **Guardrail coverage**: `DestructiveOperationRule` specifically scans `Design`'s and
  `Implementation`'s outputs for destructive-operation keywords (`drop table`,
  `truncate`, `delete from`, ...). A bulk-deactivate is a soft update (`IsActive = false`
  per row via the existing repository), not a destructive delete, so this run doesn't
  trip that rule — but a design that proposed `TRUNCATE`-ing the table to "reset" it
  would, and would then require an explicit approval before `Implementation` could
  proceed. This is exercised directly in `Orchestrator.Tests` rather than by hand in a
  CLI run.
- **Non-regression framing is explicit in the requirement itself**, not left for
  `Testing` to discover after the fact — "without breaking the existing single-URL
  expire endpoint" is part of what `RequirementAnalysis` normalizes and what `Design`
  has to account for.
- **Real test suite as the check**: the existing single-expire behavior already has
  integration test coverage in `UrlShortener.Tests` (`ApiIntegrationTests.cs`); running
  this scenario without `--skip-real-tests` re-runs that suite as part of `Testing`,
  so an actual regression there would fail the stage for real, not hypothetically.
