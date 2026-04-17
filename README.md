# Agentic Tree Search

**A database-native approach to agentic knowledge retrieval.**

> Three tools. One relational tree. Zero filesystem dependencies.

Agentic Tree Search is an architectural pattern for AI agents that need to retrieve information from a curated knowledge base. It replaces the common **Agentic File Search** approach (an agent navigating a tree of Markdown files via filesystem tools) with a **database-native tree** exposed through just three Semantic Kernel tools.

The result: centralized governance, native versioning, extensible search, transparent chunking, multi-tenancy, and a linear path to vector RAG — without expanding the agent's tool surface.

---

## TL;DR

| | Agentic File Search | **Agentic Tree Search** |
|---|---|---|
| Storage | Markdown files on disk | Relational tables (self-join) |
| Updates | File edits + redeploy | `INSERT/UPDATE/DELETE` at runtime |
| Search | grep-style substring | `LIKE` today → Full-Text / vector tomorrow |
| Chunking | Extra files or extra tool | Opt-in per node, transparent to the agent |
| Versioning | External (Git) | Native columns / temporal tables |
| Multi-tenancy | Parallel folder trees + ACLs | Single `OrganizationId` column |
| Tool surface | N filesystem tools | **3 stable KernelFunctions** |

📄 Full white paper: [PDF](docs/Agentic_Tree_Search_WhitePaper.pdf) · [DOCX](docs/Agentic_Tree_Search_WhitePaper.docx)

---

## Why this matters — four points that change the game

### 1. Full-text search is native on a database, effectively impossible on a filesystem

A relational database gives you production-grade full-text search **for free**: SQL Server `CONTAINS` / `FREETEXT`, PostgreSQL `tsvector`, MySQL `FULLTEXT`. Language-aware analyzers, stemming, thesauri, ranking — all already there, battle-tested, maintained by the engine vendor.

On a plain filesystem *this does not exist*. You either live with `grep`-style substring matching (which is not search, it's pattern matching) or you build a parallel indexing pipeline with Lucene/Elastic/Meilisearch and pay the cost of keeping two systems in sync forever. Agentic Tree Search inherits real search *by being on a database in the first place*.

### 2. Database-backed knowledge unifies content creation *and* consumption

When the knowledge base is a database, **any application** can read and write it: back-office UIs, editorial CMSs, ETL jobs, content moderation services, reporting tools, the agent itself. Authoring, approval, publication, analytics, and retrieval all speak the same query language against the same tables.

A filesystem fragments this picture: agents read files, but authors need an editor, approvals need Git/PR tooling, analytics need a separate index, audit needs yet another system. Centralizing on a database collapses these silos into a single governed surface.

### 3. The tree structure is preserved — organization doesn't get lost

The self-join (`ParentId` referencing the same table) faithfully models a hierarchical tree inside a flat relational schema. A recursive CTE rebuilds the `FullPath` on demand, so the agent still sees a clean, human-readable structure like `Billing/Invoicing/VAT on Gas`.

This is the point: **moving to a database does not mean losing the tree**. The mental model that makes filesystem-based agents work — domain, macro-topic, topic — is preserved exactly. What changes is the storage engine, not the cognitive structure the agent reasons about.

### 4. Zero impact on the agent's tool interface

The three `KernelFunction` tools (`GetKnowledgeMap`, `ReadNode`, `SearchNodes`) have the **same signatures** regardless of whether:

- the underlying storage is a filesystem or a database,
- content is atomic or chunked,
- search is backed by `LIKE`, SQL Full-Text, or a vector store,
- the corpus grows from dozens to millions of nodes.

This is what makes the pattern a safe bet for production. The agent's prompts, the tool definitions, the conversation traces — all remain stable while the infrastructure behind them evolves freely. You can start with `LIKE`, migrate to `CONTAINS`, add embeddings, without ever touching the agent layer.

---

## The idea in one picture

```
                   ┌──────────────────────┐
                   │       AI Agent       │
                   │ (Semantic Kernel)    │
                   └─────────┬────────────┘
                             │  uses only 3 tools
         ┌───────────────────┼────────────────────┐
         ▼                   ▼                    ▼
  GetKnowledgeMap        ReadNode              SearchNodes
   (flat tree)        (atomic OR chunks       (LIKE → FullText
                      — transparent)            → vector)
         │                   │                    │
         └───────────────────┼────────────────────┘
                             ▼
                 ┌───────────────────────┐
                 │    SQL database       │
                 │  KnowledgeNode (tree) │
                 │  KnowledgeChunk (opt) │
                 └───────────────────────┘
```

---

## Schema

Two tables. That's it.

```sql
CREATE TABLE KnowledgeNode (
    Id        UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    ParentId  UNIQUEIDENTIFIER NULL REFERENCES KnowledgeNode(Id),
    Name      NVARCHAR(200)  NOT NULL,
    NodeType  NVARCHAR(10)   NOT NULL,   -- 'folder' | 'file'
    Content   NVARCHAR(MAX)  NULL,
    Tags      NVARCHAR(500)  NULL,
    UpdatedAt DATETIME2      NOT NULL DEFAULT GETDATE()
);

CREATE TABLE KnowledgeChunk (
    Id         INT PRIMARY KEY IDENTITY,
    NodeId     UNIQUEIDENTIFIER NOT NULL REFERENCES KnowledgeNode(Id) ON DELETE CASCADE,
    ChunkIndex INT NOT NULL,
    Heading    NVARCHAR(200) NULL,
    Content    NVARCHAR(MAX) NOT NULL,
    TokenCount INT NULL
);
```

Full schema + seed data: [`sql/01_schema_and_seed.sql`](sql/01_schema_and_seed.sql)

---

## The three tools

All three are exposed to the agent as `[KernelFunction]` (Microsoft Semantic Kernel):

| Tool | Purpose | Returns |
|---|---|---|
| `GetKnowledgeMap()` | First call, always. | Flat tree with IDs + full paths |
| `ReadNode(id, chunkIndex?)` | **Hybrid**: atomic content OR chunk index OR specific chunk | Markdown |
| `SearchNodes(query)` | Transverse search on `Content` + `Tags` | Hits with excerpts |

Reference implementation (C#, ~300 lines): [`src/KnowledgeBasePlugin.cs`](src/KnowledgeBasePlugin.cs)

### The hybrid `ReadNode`

The key design decision: chunking is **invisible to the agent**.

```csharp
[KernelFunction]
public async Task<string> ReadNode(Guid nodeId, int? chunkIndex = null) {
    if (chunkIndex.HasValue) return await ReadChunkAsync(nodeId, chunkIndex.Value);
    var count = await GetChunkCountAsync(nodeId);
    return count > 0
        ? await GetNodeIndexAsync(nodeId, count)   // chunked node → section index
        : await ReadNodeContentAsync(nodeId);      // atomic node → full content
}
```

The agent always calls `ReadNode`. The branching logic lives inside the tool, not inside the prompt.

---

## Agent flows

**Atomic node (typical case):**
```
GetKnowledgeMap → ReadNode(id) → answer
```

**Chunked node (long document):**
```
GetKnowledgeMap → ReadNode(id) [returns section index]
                → ReadNode(id, chunkIndex) → answer
```

**Transverse search:**
```
SearchNodes("VAT on gas") → [excerpts + IDs]
                          → ReadNode(chosenId) → answer
```

More examples: [`examples/agent_flows.md`](examples/agent_flows.md)

---

## Repository layout

```
Repo/
├── README.md
├── LICENSE                         MIT
├── .gitignore
├── docs/
│   ├── Agentic_Tree_Search_WhitePaper.pdf
│   └── Agentic_Tree_Search_WhitePaper.docx
├── src/
│   └── KnowledgeBasePlugin.cs      Reference implementation (C# / Semantic Kernel)
├── sql/
│   ├── 01_schema_and_seed.sql      Tables + example data
│   └── 02_cte_recursive.sql        Recursive CTE for FullPath
└── examples/
    └── agent_flows.md              Concrete walkthroughs
```

---

## Quick start

Requirements: **SQL Server** (Express / Developer / Azure SQL), **.NET 8+**, **Semantic Kernel 1.x**.

1. Run [`sql/01_schema_and_seed.sql`](sql/01_schema_and_seed.sql) against an empty database.
2. Register the plugin in your kernel:
   ```csharp
   var kernel = Kernel.CreateBuilder()
       .AddOllamaChatCompletion(modelId: "qwen3:8b", endpoint: new Uri("http://localhost:11434"))
       .Build();

   kernel.Plugins.AddFromObject(new KnowledgeBasePlugin(connectionString), "KnowledgeBase");
   ```
3. Ask the agent a question. It will call `GetKnowledgeMap` on its own.

---

## Why this pattern?

See the white paper for the full argument. In short:

- **Enterprise fit.** In most enterprise stacks SQL is already there; writable filesystems often aren't.
- **Governance.** Who changed what, when, and with what approval — all as plain columns.
- **Extensibility.** Upgrading search from `LIKE` to `CONTAINS` to a vector store doesn't change the agent's interface.
- **Honesty about chunking.** Chunking is an engineering concern, not a domain concern. Treat it that way.

---

## When *not* to use it

Agentic File Search remains the better choice when:

- The knowledge base is already a Git repo of `.md` files and PR review is the editorial workflow.
- Authors rely on local Markdown editors and live-preview.
- There is no relational database in the stack.
- You're prototyping the agent loop itself, not the knowledge layer.

---

## Status

This is a **reference pattern + reference implementation**, not a published NuGet package. The goal is to share an architectural idea and a working blueprint. Feedback — especially critical feedback — is welcome via Issues and Discussions.

---

## License

[MIT](LICENSE)
