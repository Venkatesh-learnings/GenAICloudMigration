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

## Codebase reasoning

This is the scenario that exercises **Core Requirement #3 (Codebase Reasoning)**: the
`Design` stage has to propose a change that (a) reuses the existing repository
abstraction instead of reaching around it with raw SQL, (b) doesn't regress the
single-expire endpoint's idempotency, and (c) accounts for a real operational concern
(an unbounded table scan) that only exists because a real table with a real growth
pattern already exists — none of which a greenfield design would need to consider.

## Orchestration trace (actual run, offline fallback mode)

```
-- Stage statuses --
  RequirementAnalysis  Succeeded
  Documentation        Succeeded
  ReleaseReadiness     Succeeded
  Testing              Succeeded
  Implementation       Succeeded
  Design               Succeeded

-- Decision lineage --
  [08:37:xx] RequirementAnalysis: Requirement normalized. 0 ambiguity(ies) identified.
  [08:37:xx] Design: Design complete: Design for: Add a bulk-expire operation to the
             existing URL shortener: given a cutoff date, deactivate every short URL
             created before that date in one call, without breaking the existing
             single-URL expire endpoint or its idempotency guarantees.
  [08:37:xx] Implementation: Implementation artifact generated at .../Implementation/implementation.md
  [08:37:xx] Testing: Test plan generated (real test execution skipped by configuration).
  [08:37:xx] Documentation: Documentation written to .../Documentation/CHANGE.md
  [08:37:xx] ReleaseReadiness: Release readiness decision: GO.

-- Reliability metrics --
  Success rate:     100 % (6/6 stages)
```

Same graph, same engine, same governance mechanisms as the greenfield run — nothing
about the orchestrator changes between scenarios. Only the input (`requirement` +
`codebaseContext`) and, in a real LLM run, the resulting Design/Implementation content
differ. That's intentional: the orchestration layer is generic over the kind of SDLC
work it's coordinating.

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
