# Scenario: Ambiguous — a vague requirement, normalized instead of guessed

```bash
dotnet run --project src/Orchestrator.Cli -- ambiguous --auto-approve --skip-real-tests
```

## The requirement

> Make the URL shortener handle high traffic and be more reliable.

With codebase context:

> Existing service: ASP.NET Core Web API with SQLite persistence, a fixed-window rate
> limiter on the create and redirect endpoints, and no caching layer.

This is deliberately underspecified: "high traffic" and "more reliable" have no
numbers, no SLO, no definition of done.

## Requirement understanding: surfacing ambiguity instead of hiding it

**Core Requirement #1** asks the system to "identify ambiguity" — not paper over it. The
`RequirementAnalysisAgent`'s offline heuristic (and, with a real LLM, its prompt) is
built to flag exactly this kind of vague language rather than silently picking an
interpretation ("high traffic" could mean caching, could mean horizontal scaling, could
mean connection pooling — and picking wrong wastes the rest of the pipeline's work).

Actual run output:

```
-- Decision lineage --
  [08:37:09] RequirementAnalysis: Requirement normalized. 2 ambiguity(ies) identified.
      rationale: LLM unavailable; normalized via deterministic offline heuristics (keyword-based ambiguity detection).
```

The offline heuristic matched two vague terms in the requirement text ("handle" and
"high traffic") against a known list of unquantified performance/scale language, and
attached a clarifying question to each ("What specific, measurable target should
replace 'high traffic'?"). With `ANTHROPIC_API_KEY` set, the same stage asks a real
model to do this judgment call instead of matching a fixed keyword list — the contract
(`ambiguities` + `clarifyingQuestions` in the stage's output) is identical either way,
which is what let this scenario be built and tested without ever needing a live key.

## What happens to an ambiguous requirement downstream

The pipeline does **not** stop and wait for the ambiguity to be resolved — by design,
this prototype's `Design` stage proceeds with the best available interpretation
(a `Design for: ...` placeholder in offline mode; a real model would pick a concrete
interpretation and say so in its rationale) while the ambiguities remain visible in the
decision lineage and the final documentation. This is a conscious scope trade-off, not
an oversight — see [testing-and-limitations.md](../testing-and-limitations.md) for why
a production version would instead route unresolved ambiguities to a blocking human
checkpoint (the same `IApprovalProvider` mechanism already used for `Implementation`)
before `Design` is allowed to proceed.

## Orchestration trace (actual run, offline fallback mode)

```
-- Stage statuses --
  RequirementAnalysis  Succeeded
  Documentation        Succeeded
  ReleaseReadiness     Succeeded
  Testing              Succeeded
  Implementation       Succeeded
  Design               Succeeded

-- Reliability metrics --
  Success rate:     100 % (6/6 stages)
  Total retries:    0
  Total rollbacks:  0
```

## Validation

- **The ambiguity is on record, not lost**: it's in `RequirementAnalysis`'s
  `StageResult.Outputs` (`ambiguities`, `clarifyingQuestions`), which flows into
  `WorkflowContext.DecisionLineage` and ultimately into `report.json` for this run —
  a human reviewing the artifact afterward sees exactly what the system was unsure
  about, not just the design it eventually produced.
- **Comparison with the well-specified scenarios is the actual proof**: the greenfield
  requirement (concrete endpoints and fields) produces **0** ambiguities; this one
  produces **2**. Same agent, same code path, different — and correct — outcomes based
  on the actual input, which is what distinguishes real requirement analysis from a
  hardcoded response.
