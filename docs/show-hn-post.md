# Show HN 帖子（英文，直接可用）

> 用途：发到 news.ycombinator.com（Show HN）。标题可微调；发帖前先把 README 里 demo.gif 替换为真实录屏，并确认 LICENSE 已加。

---

## 标题（Title，≤80 字符）

```
Show HN: LocalCodeForge – a 30B local coding agent that fixes its own compile errors
```

## 正文（Body / Comment，面向 HN 受众）

Hey HN — I built a fully-local AI coding agent kernel for C#/.NET that runs a 30B model on your own GPU (Ollama, Qwen3-Coder-A3B). No cloud, no code leaves your machine.

The thing I want your feedback on is the design bet: **engineering facts as data, not prompts.**

Instead of a giant system prompt telling the model to "behave", LocalCodeForge injects what the model actually needs before it writes anything:

- a type inventory with member signatures and access modifiers (so it can't hallucinate `Order.Items` — it sees the real symbol table),
- compile-error location before the fix round (so it edits the right file, not a guess),
- pre-write type-conflict guard + task-id isolated backups with automatic rollback on repeated failure.

The loop: plan → write → build → locate → fix → run → done. When the model genuinely can't do it, the kernel rolls back to the pre-task state and says so honestly — no fake "tool verified" completions.

How it behaves on a real incremental task ("add a ShippingService class reusing the existing TaxService/DiscountService, wire it into Program.cs" on a clean 6-file project):

- 3 consecutive green runs: 68s / 69s / 121s, all completing with compile+run verified
- variants (new class calling existing service chain, static utility class) also complete in ~1 round
- a "modify existing method" variant gets safely blocked by the pre-write conflict guard when the model rewrites existing types — honest failure, no corruption

Also: deterministic routing (rule-based intent), advisory expected-output semantics for real computation (a strict equality check was misfiring on actual math — 1.03 vs a planned 100.00), a web dashboard with live task streams and rollback, and an E2E suite that distinguishes completed / safely blocked / broken (currently 7/7).

It's .NET 10 + Semantic Kernel + SQLite. Docs: docs/DEVELOPER.md (maintenance), docs/HANDOVER.md (including the full failure-point migration chain that got us from hallucinated members to 3 consecutive completions).

Demo GIF: [link to docs/demo.gif]

I'd love feedback on: (1) is the data-injection approach worth it vs prompt-based agents? (2) what would make you actually use this daily? (3) which integration (VS Code extension? terminal TUI?) would matter most?

MIT, issues/PRs welcome. Repo: https://github.com/BMDPLAinsong/LocalCodeForge
