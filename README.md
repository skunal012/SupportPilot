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

## Repository layout

```
backend/SupportPilot.Api/   ASP.NET Core API (RAG loop, ingestion, vector store)
frontend/                   React + Vite chat UI
sample-docs/                Seed corpus (the fictional "Acme Gadgets" company)
scripts/seed-docs.ps1       Rebuild the knowledge base from sample-docs/
docs/revision/              Per-day interview-revision notes (day-01 … day-06-07)
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
