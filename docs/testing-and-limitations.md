# Testing approach, limitations, and trade-offs

## Testing approach

**`UrlShortener.Tests` (43 tests)** — the product:
- Unit tests on `UrlShortenerService` against a real `UrlShortenerDbContext` (EF Core
  InMemory provider, not a mock repository) plus a controllable `FakeTimeProvider`, so
  expiry logic is tested against real time comparisons, not stubbed booleans.
- Integration tests via `WebApplicationFactory<Program>` driving real HTTP requests
  against the actual ASP.NET pipeline (routing, model binding, rate limiting middleware
  included) — a custom factory swaps the SQLite connection for an in-memory one but
  otherwise runs the exact `Program.cs` startup path, migrations included.
- Domain-level tests on `ShortUrl.IsExpired`/`IsUsable` and the short-code alphabet.

**`Orchestrator.Tests` (43 tests)** — the engine, entirely with scripted fakes, no LLM
or network calls, no real `dotnet test` sub-processes, sub-200ms total runtime:
- Scheduling: diamond graphs proving genuine concurrency (not just declared
  parallelism), linear ordering, EntryGate → Skip, and skip-cascade to dependents.
- Retry/rollback: bounded retry counts, `IRollbackable` invoked exactly once on
  exhaustion, downstream stages correctly `Blocked`.
- Approval and policy: rejection never invokes the underlying agent; guardrail rules
  aggregate correctly (Deny beats RequireApproval beats Allow across multiple rules);
  a Deny trips safe-stop.
- Re-planning: seeding `RunAsync` with `resumeFrom` and reusing the same
  `WorkflowContext` re-executes a stale stage; if its new output hash is identical to
  its old one, downstream stages are correctly left alone (re-planning is output-driven,
  not "rerun everything downstream unconditionally"); if the output genuinely changes,
  every transitive descendant is marked stale and re-executes, and `DecisionLineage`
  accumulates across both calls.
- `WorkflowGraph` validation (cycle detection, unknown-dependency detection) and
  `MetricsCollector` arithmetic (success rate, MTTR only appearing after an actual
  recovery) in isolation.

**End-to-end / manual**: every scenario (greenfield/brownfield/ambiguous) was run via
the CLI in both offline-fallback mode and against the real Anthropic API; retry and
rollback were exercised via `--inject-failure`; re-planning was exercised via
`--simulate-replan`; both Docker images were built and run, including a full
create → redirect → analytics round trip against the containerized API.

## Validation and risk control (Core Requirement #6)

| Risk | Mitigation |
|---|---|
| LLM output isn't valid JSON / doesn't follow the schema | `AgentBase.TryParseLenientJson` extracts the outermost `{...}` span and parses defensively; a parse failure degrades to the offline template rather than throwing and failing the stage |
| LLM is unreachable (no key, network failure, rate limit, bad model id) | Every call is wrapped in `LlmUnavailableException` handling with a deterministic offline fallback per agent — the run still completes and produces a real report |
| An LLM "test plan" is trusted as if it were a real test result | `TestingAgent` actually shells out to `dotnet test`; the stage's `Success` is the real process exit code, not the model's opinion |
| A stage that touches security/payment/auth surfaces gets shipped without a human looking at it | `SecuritySensitiveChangeRule` scans the requirement text and forces `RequireApproval` |
| A destructive operation (schema drop, mass delete) slips through Design/Implementation | `DestructiveOperationRule` scans those stages' outputs for destructive keywords and forces `RequireApproval` |
| A transient failure (flaky dependency, momentary LLM hiccup) fails a whole run | Bounded retry with exponential backoff before falling back to rollback |
| An agent silently mutates the real product source tree with no review | `Implementation` writes to `artifacts/runs/<runId>/`, never to `src/`, forcing a human-reviewed apply step |
| A run's reasoning is opaque after the fact | `WorkflowContext.DecisionLineage` + `AuditLog`, serialized to `report.json` per run |

## Known limitations

1. **Implementation doesn't touch the real source tree.** By design (see
   [architecture.md](architecture.md#key-decisions-and-why)) — but it does mean this
   prototype's "engineering output generation" is a reviewable artifact, not a merged
   change. A production version would have `Implementation` open a branch + PR instead
   of writing a file, keeping the exact same approval semantics but making the output a
   real, revertible commit.
2. **Ambiguity is surfaced, not blocked on.** The [ambiguous scenario](scenarios/ambiguous.md)
   flags unresolved ambiguity in the decision lineage but still lets `Design` proceed
   with a best-effort interpretation. A stricter version would route unresolved
   ambiguity through the same `IApprovalProvider` checkpoint used for `Implementation`,
   blocking `Design` until a human resolves it — deliberately left as a fallthrough here
   to keep the three scenarios comparable (all six stages complete in all three).
3. **The approval provider is process-local.** `ConsoleApprovalProvider` blocks on
   `Console.ReadLine()`; there's no persisted "pending approval" state that would let a
   real deployment pause a run, notify a human out-of-band (Slack, email), and resume it
   later. `IApprovalProvider` is the seam where that would plug in.
4. **Policy rules are keyword-based, not semantic.** `SecuritySensitiveChangeRule` and
   `DestructiveOperationRule` scan for literal substrings. This is transparent and fully
   auditable (a real advantage for a compliance-adjacent gate — you can point at exactly
   which string matched) but will both over- and under-trigger relative to a
   semantic understanding of the change. A production version would likely combine this
   deterministic layer with an LLM-based secondary check, keeping the deterministic
   rules as the non-bypassable floor.
5. **Dynamic re-planning requires an explicit second `RunAsync` call.** The engine
   detects output drift and cascades staleness correctly (see `Orchestrator.Tests`), but
   nothing watches a live external system for upstream changes and triggers that second
   call automatically — `--simulate-replan` demonstrates the mechanism deliberately
   rather than it firing "for real" from an external event source, which is out of scope
   for a CLI prototype.
6. **Single-process, in-memory run state.** `WorkflowContext`'s decision lineage and
   audit log live in memory for the duration of one CLI invocation (persisted to
   `report.json` at the end); a long-running orchestrator service would need durable,
   crash-recoverable state instead of "the process is still alive."
7. **No authentication on the URL shortener API.** Out of scope for the assignment's
   emphasis on orchestration, but a real deployment would need it before the create
   endpoint is exposed publicly.

## Trade-offs made deliberately

- **Hybrid over pure-LLM or pure-deterministic.** A pure-LLM pipeline would be far less
  reliable and impossible to unit-test deterministically; a pure-deterministic one
  wouldn't demonstrate genuine requirement/design reasoning. Splitting so that
  `ReleaseReadiness` is deterministic while the earlier stages are LLM-backed-with-fallback
  reflects where each approach actually earns its keep.
- **Two Docker images instead of one.** The API only ever needs the ASP.NET runtime; the
  orchestrator needs the full SDK because `Testing` runs `dotnet test`. Shipping one
  bloated SDK-based image for both would be simpler to write but noticeably worse for
  the API's actual deployment footprint.
- **SQLite over a networked database.** Right for a 2-3 day prototype's setup cost; the
  repository/EF Core seam (`IShortUrlRepository`, `IClickEventRepository`) is what would
  actually change to swap in Postgres, not the service layer above it.
