# Agentic SDLC Orchestrator — URL Shortener

An interview prototype in two parts:

1. **`UrlShortener.*`** — a real URL shortener service (ASP.NET Core + EF Core/SQLite):
   create/redirect/analytics/expire, idempotency, rate limiting.
2. **`Orchestrator.*`** — the actual point of the exercise: an agentic orchestration
   layer that drives a governed SDLC pipeline (requirements → design → implementation →
   testing → documentation → release-readiness) as a dependency graph with parallelism,
   bounded retries, rollback, human approval checkpoints, policy guardrails, dynamic
   re-planning, and audit-grade observability. It uses the URL shortener as its subject
   matter across three scenarios: **greenfield**, **brownfield**, and **ambiguous**.

Start here, then go deeper:

| Doc | Covers |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Components, the orchestration model, control flow, key decisions |
| [docs/scenarios/greenfield.md](docs/scenarios/greenfield.md) | Building the core APIs from nothing |
| [docs/scenarios/brownfield.md](docs/scenarios/brownfield.md) | Adding a bulk-expire feature to the existing codebase |
| [docs/scenarios/ambiguous.md](docs/scenarios/ambiguous.md) | A vague requirement, normalized before it's acted on |
| [docs/setup.md](docs/setup.md) | Build, test, run the API, run the orchestrator, run in Docker |
| [docs/testing-and-limitations.md](docs/testing-and-limitations.md) | Testing approach, known limitations, trade-offs |
| [docs/engineering-summary.md](docs/engineering-summary.md) | Plan, rationale, risks, assumptions — the final summary |

## 60-second quick start

```bash
dotnet build GenAICloudMigration.sln

# The product: URL shortener API
dotnet run --project src/UrlShortener.Api
# -> http://localhost:5001/swagger

# The differentiator: run an orchestrated SDLC scenario
dotnet run --project src/Orchestrator.Cli -- greenfield --auto-approve --skip-real-tests
```

No `ANTHROPIC_API_KEY` is required to run anything — every LLM-backed stage has a
deterministic offline fallback (see [docs/architecture.md](docs/architecture.md#hybrid-agents-llm--offline-fallback)).
Set the key to see the stages actually reasoning over the requirement text instead of
using templated placeholders.

## Repository layout

```
src/
  UrlShortener.Domain/          entities, short-code generation
  UrlShortener.Application/     use-case services (create/resolve/analytics/expire)
  UrlShortener.Infrastructure/  EF Core + SQLite persistence
  UrlShortener.Api/             ASP.NET Core Web API, rate limiting, Swagger
  Orchestrator.Core/            the graph engine: scheduling, retries, rollback,
                                 approvals, guardrails, metrics, audit log
  Orchestrator.Agents/          LLM-backed stage agents with offline fallback
  Orchestrator.Cli/             scenario runner (greenfield/brownfield/ambiguous)
tests/
  UrlShortener.Tests/           unit + integration tests for the product
  Orchestrator.Tests/           unit tests for the engine (scheduling, retry,
                                 rollback, approvals, guardrails, re-planning)
docs/                           the documents linked above
artifacts/runs/                 generated engineering output per orchestrator run
Dockerfile                      builds the API as a runtime image
Dockerfile.orchestrator         builds the CLI as an SDK image (needs `dotnet test`)
docker-compose.yml              both services wired up
```
