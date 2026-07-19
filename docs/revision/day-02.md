# Day 2 — Interview Revision: Embeddings + Vector Search

> **What Day 2 delivered:** the *Retrieval* half of RAG. Text is turned into embedding vectors (Ollama `nomic-embed-text`, 768 dims), stored in **Qdrant** (vector DB in Docker), and searched by **meaning** — so "can I get my money back?" finds the refund policy even though it never says "money back."
>
> **Run it:** start Qdrant (`docker start qdrant`) + the API (`dotnet run` in `backend/SupportPilot.Api`), then:
> `POST http://localhost:5254/demo/seed` → `GET http://localhost:5254/demo/search?q=how+long+is+shipping`

---

## Topic map

```mermaid
flowchart TD
    D2((Day 2)):::root

    D2 --> C[Concepts]:::grp
    D2 --> B[Build]:::grp

    C --> C1[Embeddings]
    C --> C2[Cosine similarity]
    C --> C3[Vector DB / Qdrant]
    C --> C4[Collection: dims + metric]
    C --> C5[Semantic vs keyword search]

    B --> B1[EmbeddingClient → Ollama]
    B --> B2[VectorStore → Qdrant REST]
    B --> B3[/demo/seed]
    B --> B4[/demo/search]

    click C1 "#concept-qa" "Embeddings"
    click C2 "#concept-qa" "Cosine similarity"
    click C3 "#concept-qa" "Vector DB"
    click C4 "#concept-qa" "Collection config"
    click C5 "#concept-qa" "Semantic vs keyword"
    click B1 "#code-walkthrough" "EmbeddingClient"
    click B2 "#code-walkthrough" "VectorStore"
    click B3 "#code-walkthrough" "seed endpoint"
    click B4 "#code-walkthrough" "search endpoint"

    classDef root fill:#1e3a5f,stroke:#0d1b2a,color:#fff;
    classDef grp fill:#2d6a4f,stroke:#1b4332,color:#fff;
```

---

## Concept Q&A

**What is an embedding?**
A model that maps a piece of text to a fixed-length list of numbers (a *vector*) that captures its *meaning*. `nomic-embed-text` outputs **768 numbers** per text. Texts with similar meaning land near each other in this 768-dimensional space — that "nearness" is the whole point. The vector on its own is meaningless; value comes from **comparing** vectors.

**What is cosine similarity, and why cosine (not distance)?**
It measures the **angle** between two vectors: `dot(a,b) / (|a|·|b|)`. `1.0` = same direction (same meaning), `~0` = unrelated, `-1` = opposite. Cosine ignores vector *length* and only cares about *direction*, which is what you want for meaning — a long document and a short query about the same topic should still match. (You saw "money back" ≈ "refund policy" at **0.61** despite zero shared words.)

**What does a vector database (Qdrant) do that a normal DB can't?**
It stores millions of vectors and answers **"give me the N closest vectors to this one"** in milliseconds using an approximate-nearest-neighbour index (HNSW). A normal SQL `WHERE` can only match exact values/keywords; it has no notion of "close in meaning."

**What is a collection, and why do dimensions + metric matter?**
A collection is Qdrant's equivalent of a table. It's configured with a **vector size** (must equal the embedding model's output — **768** for `nomic-embed-text`) and a **distance metric** (**Cosine** here). If the size doesn't match your vectors, inserts fail; if the metric is wrong, scores are meaningless. **The query must use the same embedding model as the stored data** — mixing models = garbage results.

**Semantic search vs keyword search — when does each win?**
Keyword (e.g. `LIKE '%refund%'`) is exact and cheap but **brittle**: it misses synonyms ("money back") and paraphrases. Semantic search matches meaning but can **miss exact tokens** like order IDs or SKUs ("#1042"). Best answer: **use both (hybrid search)** — that's Day 15.

---

## Code walkthrough

Two small classes keep `Program.cs` clean; both talk raw REST so every request is visible.

**`Rag/EmbeddingClient.cs`** — text → vector
| Part | What it does |
|---|---|
| `EmbedAsync(text)` | `POST /api/embeddings {model, prompt}` to Ollama → returns `float[]` of length 768 |
| `const Dimensions = 768` | Single source of truth for the vector size — used when creating the collection |
| `file record` DTOs | `EmbeddingRequest`/`EmbeddingResponse` map C# ↔ Ollama's JSON; `file`-scoped so they don't leak |

**`Rag/VectorStore.cs`** — the Qdrant wrapper (a "deep module": 3 simple methods hide all the REST/JSON)
| Method | Qdrant call | Notes |
|---|---|---|
| `EnsureCollectionAsync(name, size)` | `GET /collections/{name}` → if 404, `PUT` with `{size, distance:"Cosine"}` | Idempotent — safe to call every seed |
| `UpsertAsync(collection, points)` | `PUT /collections/{name}/points?wait=true` | `wait=true` = don't return until the write is searchable (reliable seed-then-search) |
| `SearchAsync(collection, queryVector, limit)` | `POST /collections/{name}/points/search` | Returns hits with `score` + `payload.text` |

**Endpoints in `Program.cs`:**
- `POST /demo/seed` — ensure collection → embed 8 sample support sentences → upsert. (In Day 3 these become real PDF chunks.)
- `GET /demo/search?q=...&k=3` — embed the query → Qdrant search → return top-K with scores.

**One-sentence flow to recite:** *embed each document once and store the vectors in Qdrant; at query time, embed the question with the same model, ask Qdrant for the nearest vectors by cosine, and return their original text.*

---

## Talking points

- **"This is the R in RAG."** Day 2 built *Retrieval*. Day 4 adds *Augmented Generation*: stuff the retrieved chunks into the LLM prompt so it answers grounded in your docs, with citations. Retrieval quality caps the whole system — a bad chunk retrieved = a bad answer generated.

- **Why the "same model both sides" rule matters.** The stored doc vectors and the query vector must come from the *same* embedding model, or they live in different spaces and cosine scores are noise. It's the most common silent RAG bug.

- **The chunk-size cliffhanger (Day 3).** Interviewers *always* ask "how did you choose chunk size?" Today's 8 hand-typed sentences dodge it; Day 3 replaces them with real chunks (~500 tokens, 50 overlap) and forces the trade-off: too big = imprecise retrieval + wasted context; too small = lost context.

- **Local + visible.** Embeddings run on local Ollama (free, offline) and Qdrant runs in Docker — the same architecture as a paid API (OpenAI embeddings + Pinecone), so the design transfers directly.

---

## Reproduce-it cheatsheet

```bash
# 1. Qdrant (vector DB) in Docker — data persists in a named volume
docker start qdrant                 # (first time: docker run -d --name qdrant \
                                    #   -p 6333:6333 -v qdrant_storage:/qdrant/storage qdrant/qdrant)
curl http://localhost:6333/         # sanity: returns {"title":"qdrant...","version":...}

# 2. Run the API
cd backend/SupportPilot.Api && dotnet run

# 3. Seed the sample docs (embeds 8 sentences → Qdrant), then search by meaning
curl -X POST http://localhost:5254/demo/seed
curl "http://localhost:5254/demo/search?q=can+I+get+my+money+back"
curl "http://localhost:5254/demo/search?q=I+forgot+my+login+details&k=2"
```

**What to notice:** the top hit shares *no keywords* with your query, yet is clearly the right topic — and the cosine `score` drops off for unrelated sentences. That gap between "right topic" and "wrong topic" is what makes retrieval work.

**Inspect Qdrant directly (optional):** open `http://localhost:6333/dashboard` in a browser to see the collection, points, and payloads.
