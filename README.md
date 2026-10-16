# LocalCodeForge

> A fully-local AI coding agent kernel for C#/.NET — it plans, writes, **builds, fixes compile errors, and verifies** your project, powered by a 30B model on your own GPU. No cloud. No code leaves your machine.

[![Build](https://img.shields.io/badge/build-passing-brightgreen)]() [![Tests](https://img.shields.io/badge/tests-864%2F864-brightgreen)]() [![E2E](https://img.shields.io/badge/e2e-7%2F7-brightgreen)]() [![License](https://img.shields.io/badge/license-MIT-blue)]()

---

## Why another coding agent?

Most coding agents are **prompt-based**: they try to behave correctly because a giant system prompt tells them to. LocalCodeForge is **data-injected**: the model receives the actual engineering facts it needs — type inventories with signatures, access modifiers, symbol indexes — so it has no room to hallucinate. When it does fail, the kernel fixes and rolls back *deterministically*, not by hoping the model "behaves better."

Three things make it different:

1. **Engineering awareness as data, not prompts** — type inventory with member signatures + access modifiers is injected before generation; a compile error is located to the exact file before the fix round begins (`LocateErrorFile`).
2. **A closed fix loop with a safety net** — write → `dotnet build/run` → on failure: locate, fix, retry; on repeated failure: **automatic rollback** to the pre-task state (task-id isolated backups).
3. **Verifiable, not vibes** — deterministic routing (rule-based intent classifier), advisory `expected_output` semantics for real computation, and an E2E suite (7/7 green) that distinguishes *completed*, *safely blocked*, and *broken*.

---

![Demo: incremental task on a real project — new class, compile, run, done](docs/demo.gif)

> Demo GIF: add a screen recording of a ShippingService-style incremental task (new class reusing existing services → compile → run → output).

---

## Quick start

```powershell
# 1. Serve the local model (Ollama; 30B Qwen3-Coder-A3B, single tag "law-unified")
ollama pull law-unified

# 2. Build (solution file is LocalCodeForge.slnx — internal name pending rename)
dotnet build LocalCodeForge.slnx --no-incremental

# 3. Run, point at your project in write mode, then just describe the task
dotnet run --project src\Agent.Cli\Agent.Cli.csproj
> use --write X:\path\to\your\project
> add a ShippingService class that reuses TaxService and DiscountService, wire it into Program.cs
```

The **web dashboard is the primary interaction surface**: the CLI auto-launches it on startup (loopback + token) and opens your browser, with live task streams, stats, history, backup diff, rollback and one-click rerun. The CLI itself stays as a fallback (pipeline/headless safe) — every command keeps working.

---

## What it can do

| Mode | Example | Behavior |
|---|---|---|
| Read / query | "read Order.cs and summarize" | AgentLoop, converges with a summary |
| Single file | "write hello.py printing Hello E2E" | Generates + verifies |
| Multi-file project | "C# student system: Student model, StudentService, Program" | Planner orchestrates files + run |
| **Incremental task** | "add ShippingService reusing TaxService/DiscountService, wire into Program.cs" | Planner → write → build → fix → run → done (3 consecutive green runs: 68s / 69s / 121s) |
| Modify existing code | "add CalculateFinalTotal to OrderService, switch Program.cs to call it" | Safely blocked by pre-write type-conflict guard if the model rewrites existing types (P-D) |

---

## Architecture at a glance

```
User input
   → TaskClassifier (deterministic intent) → SimpleQA / AgentLoop
                                          → Orchestrator
   → Planner (multi-file plan + run/verify spec)
   → Executor (type inventory + signature injection → write)
   → Verify (dotnet build/run; compile-error → LocateErrorFile → fix loop)
   → Done | Rollback (task-id isolated backups, dry-run preview)
```

Event stream → SQLite → web dashboard (SSE live updates, task replay, rollback).

---

## Tech stack

| Component | Choice |
|---|---|
| Runtime | .NET 10 (do not downgrade) |
| Model | Qwen3-Coder-A3B **30B** via Ollama (OpenAI-compatible, single tag, request-level temperature) |
| Orchestration | Semantic Kernel 1.80.1 |
| Storage | SQLite (sessions, symbols, memory, metrics, events) |
| UI | Local web dashboard (loopback + token auth, SSE) |

---

## Docs

- `docs/DEVELOPER.md` — how to maintain & extend (deterministic rules, data schemas, E2E cases)
- `docs/DEVELOPER_DETAILED.md` — deep walkthrough
- `docs/HANDOVER.md` — change history & pitfalls (incl. the failure-point migration chain: hallucination → signature injection → advisory semantics → 3 consecutive completions)

---

## License

MIT — the LICENSE file is pending; feel free to use under MIT terms meanwhile.

---

**Star if you believe local-first engineering is the way.** Issues and PRs welcome.
