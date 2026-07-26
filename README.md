# SupportPilot

An AI customer-support assistant that answers questions over a company's own
documents using **RAG** (Retrieval-Augmented Generation): it retrieves relevant
chunks from ingested docs, grounds a local LLM in them, streams the answer
token-by-token, and cites its sources — refusing to guess when the answer isn't
in the documents.

> Runs **fully local and free** — no API keys, no billing. Generation and
> embeddings both come from a local [Ollama](https://ollama.com) server; the
> vector store is [Qdrant](https://qdrant.tech) in Docker.

## Stack

| Layer | Choice |
|---|---|
| Backend | ASP.NET Core (.NET 10) minimal API |
| Generation | Ollama `llama3.2:3b` (raw `HttpClient`, streamed) |
| Embeddings | Ollama `nomic-embed-text` (768-dim, cosine) |
| Vector DB | Qdrant (Docker) |
| Frontend | React + Vite (SSE streaming, clickable citations) |
| PDF extraction | PdfPig (MIT) |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Docker](https://www.docker.com/) (for Qdrant)
- [Ollama](https://ollama.com/), with the two models pulled:
  ```bash
  ollama pull llama3.2:3b
  ollama pull nomic-embed-text
  ```
- Node.js (for the frontend)

## Run it

```bash
# 1. Vector DB
docker run -d --name qdrant -p 6333:6333 qdrant/qdrant

# 2. Local models (Ollama usually runs as a service; otherwise:)
ollama serve

# 3. Backend  ->  http://localhost:5254
cd backend/SupportPilot.Api
dotnet run

# 4. Seed the demo knowledge base from sample-docs/ (idempotent)
pwsh ./scripts/seed-docs.ps1

# 5. Frontend  ->  http://localhost:5173
cd frontend
npm install
npm run dev
```

Open http://localhost:5173 and ask away. Sample questions against the seeded
"Acme Gadgets" docs:

- *"How long do refunds take?"* → cites the support policy
- *"How do I pair the SoundPods Pro?"* → cites the product manual
- *"How much do the SoundPods Pro cost?"* → cites the catalog
- *"Who is the CEO of Acme Gadgets?"* → *"I don't know based on the available documents."*

## Evaluation

"It works when I try it" isn't evidence. `scripts/eval.ps1` runs a fixed set of
questions with known answers (`docs/eval/testset.json`) through `/chat` and scores
two things:

- **Correctness** — does the answer contain the known facts? For questions the
  docs *can't* answer, "correct" means the system **refuses to fabricate** —
  either escalating to a human or saying "I don't know."
- **Groundedness** — an answered question must cite a source; an unanswerable one
  must refuse. Either way it may not make something up.

```bash
pwsh ./scripts/eval.ps1        # backend running + docs seeded; writes docs/eval/results.md
```

Latest run (16 cases, `llama3.2:3b`, generation temperature pinned to **0** for
reproducible scores — see [docs/eval/results.md](docs/eval/results.md)):

| Metric | Score |
|---|---|
| **Correctness** | **15 / 16 (93.8%)** |
| **Groundedness** | **16 / 16 (100%)** |

All 3 unanswerable questions refuse correctly (no hallucinations). The one miss is
instructive: *"How long do international orders take to arrive?"* is refused even
though the fact sits in the **rank-1 retrieved chunk** (score 0.65) — the same fact
is answered when phrased *"international shipping"*. So the failure is in
**generation, not retrieval**: the 3B model is brittle to phrasing. That points at
the planned fixes — query rewriting (Day 16) and/or a larger generation model —
rather than anything in the retrieval layer.

## Repository layout

```
backend/SupportPilot.Api/   ASP.NET Core API (RAG loop, ingestion, vector store)
frontend/                   React + Vite chat UI
sample-docs/                Seed corpus (the fictional "Acme Gadgets" company)
scripts/seed-docs.ps1       Rebuild the knowledge base from sample-docs/
scripts/eval.ps1            Run the evaluation set and score correctness/groundedness
docs/eval/                  Evaluation test set (testset.json) + latest results.md
docs/revision/              Per-day interview-revision notes (day-01 … day-11)
CLAUDE.md                   Project plan and 3-week roadmap
```

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/chat?q=…` | RAG answer, streamed as SSE, with citations |
| `POST` | `/ingest` | Upload a `.pdf/.txt/.md` → extract, chunk, embed, store |
| `GET` | `/search?q=…` | Raw retrieval results (debug view of the "retrieve" half) |

---

Built as a 3-week learning project — see [CLAUDE.md](CLAUDE.md) for the roadmap
and [docs/revision/](docs/revision/) for per-day write-ups.
