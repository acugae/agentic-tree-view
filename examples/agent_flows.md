# Agent Flows

Concrete walkthroughs of how an AI agent interacts with Agentic Tree Search.
The agent only ever sees three tools: `GetKnowledgeMap`, `ReadNode`, `SearchNodes`.

---

## Flow 1 — Atomic node (typical case)

**User:** *"How is VAT calculated on domestic gas consumption?"*

**Step 1.** The agent orients itself.

```
→ GetKnowledgeMap()
```

Response (abridged):
```
[11111111-...001] 📁 Billing
[11111111-...002]   📁 Billing/Fatturazione
[11111111-...006]     📄 Billing/Fatturazione/Calcolo IVA su gas
[11111111-...007]     📄 Billing/Fatturazione/Storno fattura
[11111111-...003]   📁 Billing/Pagamenti
[11111111-...008]     📄 Billing/Pagamenti/Scadenze pagamento
[11111111-...004] 📁 Provisioning
[11111111-...005]   📁 Provisioning/Attivazione Servizi
```

**Step 2.** Semantic node naming makes the target obvious.

```
→ ReadNode(id = 11111111-...006)
```

Response:
```
# Billing/Fatturazione/Calcolo IVA su gas
> Updated: 14/04/2026 | Tags: iva,gas,calcolo,aliquota

## Calcolo IVA su gas
L'IVA applicata al consumo gas domestico è al 10% fino a 480 mc/anno,
22% oltre soglia. La soglia viene calcolata su base annua per punto di fornitura.
```

**Step 3.** The agent answers the user. Total DB round-trips: **2**.

---

## Flow 2 — Chunked node (long document)

Some nodes carry long content and have been split into `KnowledgeChunk` rows.
The agent does not know this in advance — and doesn't need to.

**User:** *"What are the tariff bands for the commercial electricity contract?"*

**Step 1.** `GetKnowledgeMap()` → agent identifies node `...042` (*Contratti/Energia Commerciale*).

**Step 2.**
```
→ ReadNode(id = ...042)
```

Because the node has chunks, the tool returns an **index**, not content:
```
Il nodo [...042] contiene 4 sezioni.
Richiama ReadNode(nodeId, chunkIndex) per leggere la sezione desiderata.

| ChunkIndex | Section                       | Est. tokens |
|-----------:|-------------------------------|------------:|
| 0          | (introduction)                | 180         |
| 1          | Fasce orarie                  | 420         |
| 2          | Scaglioni tariffari           | 510         |
| 3          | Penali e recesso              | 260         |
```

**Step 3.** The agent picks the right section.
```
→ ReadNode(id = ...042, chunkIndex = 2)
```

Response: only the "Scaglioni tariffari" section, a few hundred tokens.

Total DB round-trips: **3** — and the agent loaded only the relevant slice of the document.

---

## Flow 3 — Transverse search

**User:** *"Is there anything about late payment reminders?"*

The agent is not sure which node owns this information.

**Step 1.** Skip the map, go straight to search.
```
→ SearchNodes(query = "sollecito")
```

Response:
```
# Results: "sollecito"
Found 1 file. Use ReadNode(id) to read the full content.

## [11111111-...008] Billing/Pagamenti/Scadenze pagamento
**Excerpt:**
> ...Il mancato pagamento entro 60 giorni attiva il processo di sollecito automatico...
**Tags:** scadenza,pagamento,sollecito
```

**Step 2.** In this case the excerpt already answers the question. The agent
*may* skip the `ReadNode` call if the excerpt is sufficient, or fetch the full
content for higher fidelity.

```
→ ReadNode(id = 11111111-...008)   (optional)
```

Total DB round-trips: **1 or 2**.

---

## Flow 4 — Unknown / out-of-scope question

**User:** *"What's the weather like today?"*

The agent runs `SearchNodes("weather")` → 0 hits. The tool returns:

```
Nessun file trovato contenente "weather".
Prova con un termine più generico o usa GetKnowledgeMap per esplorare la struttura.
```

The agent gracefully admits the knowledge base doesn't cover the topic,
instead of hallucinating. The tool's explicit fallback message is itself a
prompt-engineering aid.

---

## Takeaways

- **`GetKnowledgeMap` once, then targeted reads.** The flat map with precomputed
  `FullPath` eliminates recursive directory traversal.
- **Chunking stays behind the tool boundary.** `ReadNode` is one signature, two behaviors.
- **Search returns excerpts + IDs.** The agent can short-circuit or drill down.
- **Semantic node names are prompts.** Good taxonomy reduces the number of calls.
