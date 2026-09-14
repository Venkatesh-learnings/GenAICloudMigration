# Scenario: Security — the governance path, actually exercised

```bash
dotnet run --project src/Orchestrator.Cli -- security --auto-approve --skip-real-tests
```

This is a **fourth** scenario beyond the three the assignment asks for. It exists because
a guardrail that never fires in any runnable demo is an unverified claim: the
greenfield, brownfield and ambiguous requirements are all benign, so none of them
escalate anything. This one is not benign.

## The requirement

> Add API key authentication to the URL shortener's create endpoint: callers must present
> a token, tokens are issued per tenant and stored hashed, and an invalid or revoked token
> must be rejected without leaking whether the key ever existed.

## What fires, and what doesn't

**`SecuritySensitiveChangeRule` escalates to approval** — the requirement mentions auth,
so the rule requires a human checkpoint. Crucially it escalates only the stages marked
`HighImpact` (the design, and each code-generating `Implementation:T*` stage), not the
read-only analysis and reporting stages:

```
Design               ApprovalRequested   SecuritySensitiveChange: requirement text mentions 'auth'
Implementation:T1    ApprovalRequested   SecuritySensitiveChange: requirement text mentions 'auth'
Implementation:T2    ApprovalRequested   SecuritySensitiveChange: requirement text mentions 'auth'
Implementation:T3    ApprovalRequested   SecuritySensitiveChange: requirement text mentions 'auth'
Implementation:T4    ApprovalRequested   SecuritySensitiveChange: requirement text mentions 'auth'
```

**`SecurityReview`'s entry gate opens.** In the other three scenarios this stage records
`Skipped`; here it runs as a third parallel branch alongside `Testing` and
`Documentation`.

**The release gate says NO-GO.** Offline, the review emits a manual checklist with
verdict `manual-review-required` rather than `pass`, and `ReleaseReadinessAgent` treats
anything other than `pass` on a security review as a hard blocker:

```
SecurityReview     Succeeded
ReleaseReadiness   Release readiness decision: NO-GO.
```

That's the correct outcome, and worth being precise about: the *pipeline* succeeded —
every stage ran and did its job — while the *release decision* is NO-GO. A security
change whose review hasn't been signed off should not be shippable, and the system
distinguishes "the work completed" from "the work is safe to ship."

## Denial and safe-stop

Escalating to a human is the common case; one rule refuses outright.
`HardcodedCredentialRule` returns `Deny` when generated work embeds a credential, which
trips the engine's safe-stop and halts the run. To see it without committing a real
secret anywhere, `--inject-policy-violation` makes the Design stage emit one:

```bash
dotnet run --project src/Orchestrator.Cli -- brownfield --auto-approve --skip-real-tests \
  --inject-policy-violation
```

```
Overall result: FAILED
Stop reason: Policy denied stage 'Implementation:T1': HardcodedCredential: generated work
             appears to embed a credential (matched 'password = "')

Implementation:T1    PolicyBlocked       HardcodedCredential: generated work appears to embed a credential
*                    SafeStopTriggered   Policy denied stage 'Implementation:T1': ...
```

No approval prompt is offered, because there is nothing to approve — see the reasoning in
[architecture.md](../architecture.md#key-decisions-and-why). The run stops scheduling new
work, the audit trail records exactly which rule denied which stage and why, and the
`RunReport` carries the stop reason.

## Validation

- The three benign scenarios and this one run the **same** engine, rules and graph
  definition. The difference in behavior comes entirely from the input, which is what
  makes the guardrails meaningful rather than decorative.
- Guardrail scoping is covered by unit tests in `Orchestrator.Tests` (same sensitive
  input against a high-impact and a non-high-impact stage produces different outcomes),
  as is the `Deny` → safe-stop path.
