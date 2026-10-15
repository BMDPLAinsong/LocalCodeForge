# Awesome 列表 PR 草案 — LocalCodeForge

## 目标仓库

**[bradAGI/awesome-cli-coding-agents](https://github.com/bradAGI/awesome-cli-coding-agents)** — "A curated list of 90+ CLI coding agents ... plus the harnesses that orchestrate, sandbox, or extend them."

LocalCodeForge 归类：**Harnesses & orchestration → Orchestrators & autonomous loops**（自治循环 + 编译修复闭环 + 回滚 + Dashboard 一体），也可视情况放 Open Source 列表。

---

## 1. README 条目（插入文本，英文）

### 方案 A：Harnesses & orchestration → Orchestrators & autonomous loops

```markdown
- **[LocalCodeForge](https://github.com/BMDPLAinsong/LocalCodeForge)** `⭐ 0` — C#/.NET local coding agent kernel (30B Qwen3-Coder via Ollama, no cloud). Injects type inventories with member signatures + access modifiers before generation — engineering facts as **data, not prompts**. Closed loop: plan → write → build → locate compile error → fix → run → verify; on repeated failure, **automatic rollback** to task-id isolated backups. Pre-write type-conflict guard, advisory expected-output semantics (strict equality misfires on real math — 1.03 vs a planned 100.00), web dashboard with live task stream / backup diff / rollback. E2E 7/7, 864 deterministic tests, 3 consecutive green incremental-task runs (68s / 69s / 121s). MIT.
```

### 方案 B：Open Source（terminal-native）列表

```markdown
- **[LocalCodeForge](https://github.com/BMDPLAinsong/LocalCodeForge)** `⭐ 0` — C#/.NET terminal coding agent kernel (30B via Ollama) that fixes its own compile errors: data-injected symbol tables (signatures + access modifiers), pre-write conflict guard, automatic rollback, build+run verification. CLI + web dashboard. E2E 7/7, 864 tests, MIT.
```

> 推荐 **方案 A**——列表里 Harnesses/Orchestrators 分类与 LocalCodeForge 的"自治循环内核 + 回滚 + Dashboard"定位最贴合，且该分类竞争密度低、0 star 条目不突兀。

---

## 2. PR 标题（英文，≤72 字符）

```
Add LocalCodeForge: local 30B coding agent kernel with compile-fix loop & rollback
```

## 3. PR 描述（英文，粘贴进 PR body）

```markdown
## What

Add [LocalCodeForge](https://github.com/BMDPLAinsong/LocalCodeForge) to the **Orchestrators & autonomous loops** section (Harnesses & orchestration).

A fully-local AI coding agent kernel for C#/.NET that runs a 30B Qwen3-Coder model on your own GPU via Ollama — no cloud, no code leaves the machine.

## Why it fits the list

- Terminal-native CLI (`use --write <path>` then describe the task) plus a local web dashboard (live task stream, backup diff, rollback).
- The differentiator is structural, not prompt-based: before writing, the kernel injects the **type inventory with member signatures + access modifiers** (a real symbol table, so the model cannot hallucinate `Order.Items`); compile errors are **located to the exact file** before the fix round; pre-write type-conflict guard; and on repeated failure the task **rolls back automatically** to task-id isolated backups.
- Verification is deterministic: 864 tests, E2E 7/7, and 3 consecutive green runs (68s / 69s / 121s) on an incremental task ("add ShippingService reusing TaxService/DiscountService, wire into Program.cs").

## Checks

- MIT licensed, public repo, README + LICENSE + v1.0.0 release in place.
- Star count is 0 as of today — honest entry; the section already hosts small projects.
```

## 4. 提交信息建议（本地 commit）

```
docs: add LocalCodeForge to Orchestrators & autonomous loops
```

---

## 操作步骤（fork → PR，需浏览器登录）

1. 打开 https://github.com/bradAGI/awesome-cli-coding-agents → Fork
2. 编辑 README.md → 在 *Orchestrators & autonomous loops* 小节末尾插入条目（方案 A）
3. Commit（信息见上）→ New Pull Request → 粘贴 PR 标题 + 描述 → Create
4. 关注维护者反馈（可能要求改措辞/挪位置，按提示改后 force-push）

## 备选目标（后续可做）

| 列表 | 理由 | 门槛 |
|---|---|---|
| [EndoTheDev/Awesome-Ollama](https://github.com/EndoTheDev/Awesome-Ollama) | opinionated Ollama 生态列表，收录 AI coding tools | 低，PR 通常被合并 |
| [ollama/ollama](https://github.com/ollama/ollama) README 社区区块 | 官方仓库曝光最大 | 高，需要稳定 star/使用证据 |
