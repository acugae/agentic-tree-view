# Copilot Instructions — Agentic Tree Search

This repository is a **reference pattern + reference implementation**, not a shippable package. There is no build system, no test suite, no CI pipeline. The goal is to communicate an architectural idea clearly — changes should preserve that clarity.

## What this repo actually is

- `src/KnowledgeBasePlugin.cs` — a single self-contained Semantic Kernel plugin (~340 lines) exposing exactly **three** `[KernelFunction]` tools: `GetKnowledgeMap`, `ReadNode`, `SearchNodes`. It is meant to be copy-pasted into a consumer project, not compiled here.
- `sql/01_schema_and_seed.sql` — the two tables the whole pattern rests on: `KnowledgeNode` (self-join via `ParentId`) and `KnowledgeChunk` (optional, for long nodes).
- `sql/02_cte_recursive.sql` — illustrative recursive CTE that builds `FullPath` from the self-joined tree.
- `docs/Agentic_Tree_Search_WhitePaper.{pdf,docx}` — the long-form argument. Regenerated from `../generate_whitepaper.py` (outside this repo).
- `examples/agent_flows.md` — walkthroughs of the three canonical agent flows (atomic / chunked / search).
- `README.md` — the primary pitch. It is the document outside readers judge the project by; treat edits to it with care.

## Architectural invariants — do not break these

These are the load-bearing decisions of the pattern. Any change that violates one of them is almost certainly wrong.

1. **Three tools, stable signatures.** The agent surface is exactly `GetKnowledgeMap()`, `ReadNode(Guid nodeId, int? chunkIndex = null)`, `SearchNodes(string query)`. Adding a fourth tool, splitting `ReadNode` into two, or changing a signature defeats the pattern's main selling point (see README §4 "Zero impact on the agent's tool interface").
2. **`ReadNode` is hybrid and the hybrid is invisible to the agent.** The branching between atomic read / chunk index / chunk content lives *inside* the method. The `[Description]` attribute is what the LLM sees — keep it aligned with actual behavior.
3. **Two tables, no more.** `KnowledgeNode` + optional `KnowledgeChunk`. Resist the urge to add lookup tables, join tables, or normalize further — the simplicity is the point. New columns on existing tables (e.g. `Version`, `OrganizationId`, `EmbeddingVector`) are fine and explicitly envisioned in the white paper.
4. **Hierarchy via self-join + recursive CTE.** Not via materialized paths, nested sets, or a separate edge table. The recursive CTE that computes `FullPath` is reproduced in three places in `KnowledgeBasePlugin.cs`; keep them consistent.
5. **SQL Server dialect.** `UNIQUEIDENTIFIER`, `NVARCHAR(MAX)`, `NEWID()`, `GETDATE()`, `Microsoft.Data.SqlClient`. Do not port to another dialect inside this repo — the white paper and code presuppose SQL Server. Cross-engine notes belong in prose, not in code changes.

## Conventions specific to this codebase

- **User-facing strings in `KnowledgeBasePlugin.cs` are Italian** (e.g. `"Il nodo [...] è una cartella"`, `"Nessun file trovato"`, `"Richiama ReadNode..."`). The seed data in the SQL file is also Italian (Billing/Fatturazione domain). Keep new strings in the same language unless deliberately internationalizing.
- **README, white paper, `examples/agent_flows.md` are in English.** Do not mix languages within a single document.
- **`[Description]` attributes matter.** They are the prompt the LLM sees for each tool. Edits to behavior require matching edits to the description. Write them as instructions to an agent, not as developer docstrings.
- **SQL is embedded as C# raw string literals** (`"""..."""`). Keep the indentation style already in the file — it shows up in query plans and logs.
- **No dependency injection framework, no logging abstraction, no async streaming.** The plugin uses `new SqlConnection` per call on purpose: the code has to be readable as a pattern, not optimal as a library.
- **Parameterized queries only.** `SqlCommand.Parameters.AddWithValue` is used everywhere; the `SearchNodes` `LIKE` pattern concatenates `N'%'` around `@Query` inside SQL, never in C#. Never introduce string-concatenated SQL.

## When editing the README

The README has a deliberate order: TL;DR table → "Why this matters" (four emphasized points) → diagram → schema → three tools → flows → layout → quick start → when *not* to use it. This structure is tuned to let a senior reader decide in ~60 seconds whether to keep reading. Preserve that flow when adding content, and prefer appending to existing sections over inserting new top-level sections high up.

## Useful context outside this repo

The parent folder (`..` from the repo root) contains `generate_whitepaper.py` — the script that produced `docs/Agentic_Tree_Search_WhitePaper.docx`. If white-paper content needs to change, edit that script and regenerate both the DOCX and the PDF (via `docx2pdf`), rather than hand-editing the Word file.
