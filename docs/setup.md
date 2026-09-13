# Setup

## Prerequisites

- .NET 8 SDK
- Docker (optional, for the containerized paths)
- An `ANTHROPIC_API_KEY` (optional — every orchestrator stage has a deterministic
  offline fallback and runs without one; see [architecture.md](architecture.md#hybrid-agents-llm--offline-fallback))

## Build and test everything

```bash
dotnet build GenAICloudMigration.sln

dotnet test tests/UrlShortener.Tests/UrlShortener.Tests.csproj    # 43 tests
dotnet test tests/Orchestrator.Tests/Orchestrator.Tests.csproj    # engine unit tests
```

## Run the URL shortener API

```bash
dotnet run --project src/UrlShortener.Api
```

Swagger UI at `http://localhost:5001/swagger` (or whatever port the console prints).
A SQLite database file is created and migrated automatically on first run.

Quick manual check:

```bash
curl -X POST http://localhost:5001/api/urls \
  -H "Content-Type: application/json" \
  -d '{"originalUrl":"https://example.com"}'
# {"code":"abc1234", "shortUrl":"http://localhost:5001/abc1234", ...}

curl -i http://localhost:5001/abc1234          # 302 redirect + records a click
curl http://localhost:5001/api/urls/abc1234/analytics
```

## Run an orchestrator scenario

```bash
dotnet run --project src/Orchestrator.Cli -- <scenario> [options]
```

`<scenario>` is one of `greenfield`, `brownfield`, `ambiguous` (the three the assignment
asks for — see [docs/scenarios/](.)) plus `security`, a fourth scenario that exists to
exercise the governance path: its requirement genuinely touches an auth surface, so the
security guardrail escalates the high-impact stages to require approval and the
conditional `SecurityReview` stage's entry gate opens. Options:

| Flag | Effect |
|---|---|
| `--auto-approve` | Approve every human checkpoint automatically (unattended/CI runs) |
| `--skip-real-tests` | Skip the real `dotnet test` run in the Testing stage (faster) |
| `--inject-failure=Stage:N` | Fail `Stage` N times before letting it succeed — demonstrates bounded retry (small N) or rollback (N > the stage's MaxRetries) |
| `--simulate-replan` | After a successful run, force `RequirementAnalysis` stale and re-run on the same context, cascading re-planning through every downstream stage |
| `--inject-policy-violation` | Make the Design stage emit an embedded credential, so `HardcodedCredentialRule` denies the next stage and trips the engine's safe-stop |

Without `--auto-approve`, every high-impact stage (each derived `Implementation:T*` code-
generating stage, and `Design` when the change is security-sensitive) prompts on the
console — you'll need to type `y` to let the run continue.

Examples:

```bash
# A normal governed run
dotnet run --project src/Orchestrator.Cli -- greenfield --auto-approve --skip-real-tests

# Demonstrate bounded retry: Design fails once, then succeeds
dotnet run --project src/Orchestrator.Cli -- greenfield --auto-approve --skip-real-tests \
  --inject-failure=Design:1

# Demonstrate rollback: Design always fails, exceeding its MaxRetries (2)
dotnet run --project src/Orchestrator.Cli -- greenfield --auto-approve --skip-real-tests \
  --inject-failure=Design:99

# Demonstrate dynamic re-planning cascading through the whole downstream graph
dotnet run --project src/Orchestrator.Cli -- greenfield --auto-approve --skip-real-tests \
  --simulate-replan

# Demonstrate a guardrail DENYING a stage and halting the run via safe-stop
dotnet run --project src/Orchestrator.Cli -- brownfield --auto-approve --skip-real-tests \
  --inject-policy-violation

# Demonstrate the conditional security review actually running, and the release gate
# returning NO-GO because the review has not been signed off
dotnet run --project src/Orchestrator.Cli -- security --auto-approve --skip-real-tests
```

### What to look for in the output

- `TaskDecomposition` reports how many tasks it derived and how many waves they sequence
  into, and a `GraphExpanded` audit entry shows the implementation stages it added — the
  count differs per scenario because it's derived from the requirement.
- `CodebaseAnalysis` reports how many projects it scanned off disk and how many files
  relate to the requirement (it is `Skipped` for greenfield).
- `SecurityReview` shows as `Skipped` in the three main scenarios and `Succeeded` in
  `security` — the entry gate deciding, not the agent.

Every run writes `artifacts/runs/<runId>/report.json` (the full `RunReport`: stage
statuses, decision lineage, audit log, metrics) plus each stage's generated artifacts
(implementation notes, test plans, change docs).

### Using a real LLM instead of the offline fallback

```bash
export ANTHROPIC_API_KEY=sk-ant-...
# optional, defaults to claude-3-5-sonnet-latest:
export ANTHROPIC_MODEL=claude-3-5-sonnet-latest

dotnet run --project src/Orchestrator.Cli -- brownfield --auto-approve
```

Without `--skip-real-tests`, `Testing` also runs the real `dotnet test` suite — expect
the run to take longer (a real LLM round trip per stage, plus a genuine test run).

## Run in Docker

The API (runtime-only image):

```bash
docker build -t urlshortener-api -f Dockerfile .
docker run -p 8080:8080 urlshortener-api
curl http://localhost:8080/health
```

The orchestrator CLI (SDK image, since `Testing` shells out to `dotnet test`):

```bash
docker build -t urlshortener-orchestrator -f Dockerfile.orchestrator .
docker run --rm urlshortener-orchestrator brownfield --auto-approve --skip-real-tests
```

Or both, via Compose:

```bash
docker compose up api                                    # API on :8080
docker compose run --rm orchestrator ambiguous --auto-approve --skip-real-tests
```

`ANTHROPIC_API_KEY`/`ANTHROPIC_MODEL` in your shell's environment are forwarded to the
`orchestrator` service automatically by `docker-compose.yml`.
