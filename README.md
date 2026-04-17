# Agentic Tree View

**A database-native approach to agentic knowledge retrieval.**

> Three tools. One relational tree. Zero filesystem dependencies.

Agentic Tree View is an architectural pattern for AI agents that need to retrieve information from a curated knowledge base. It replaces the common **Agentic File Search** approach (an agent navigating a tree of Markdown files via filesystem tools) with a **database-native tree** exposed through just three Semantic Kernel tools.

The result: centralized governance, native versioning, extensible search, transparent chunking, multi-tenancy, and a linear path to vector RAG — without expanding the agent's tool surface.

---

## TL;DR

| | Agentic File Search | **Agentic Tree View** |
|---|---|---|
| Storage | Markdown files on disk | Relational tables (self-join) |
| Updates | File edits + redeploy | `INSERT/UPDATE/DELETE` at runtime |
| Search | grep-style substring | `LIKE` today → Full-Text / vector tomorrow |
| Chunking | Extra files or extra tool | Opt-in per node, transparent to the agent |
| Versioning | External (Git) | Native columns / temporal tables |
| Multi-tenancy | Parallel folder trees + ACLs | Single `OrganizationId` column |
| Tool surface | N filesystem tools | **3 stable KernelFunctions** |

📄 Full white paper: [PDF](docs/Agentic_Tree_View_WhitePaper.pdf) · [DOCX](docs/Agentic_Tree_View_WhitePaper.docx)

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
│   ├── Agentic_Tree_View_WhitePaper.pdf
│   └── Agentic_Tree_View_WhitePaper.docx
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
