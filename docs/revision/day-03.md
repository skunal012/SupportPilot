# Day 3 — Interview Revision: The Ingestion Pipeline (Chunking)

> **What Day 3 delivered:** the pipeline that turns real documents into searchable knowledge. Upload a file → **extract** text page-by-page (PdfPig for PDF) → **chunk** into ~500-token overlapping pieces → **embed** each chunk → **store** in Qdrant with metadata (filename, page). Search now returns **citations**.
>
> **Run it:** `dotnet run` in `backend/SupportPilot.Api`, then:
> `POST /ingest` (multipart `file=@sample-docs/acme-support-policy.md`) → `GET /search?q=how+long+do+refunds+take`

---

## Topic map

```mermaid
flowchart TD
    D3((Day 3)):::root

    D3 --> C[Concepts]:::grp
    D3 --> B[Build]:::grp

    C --> C1[Why chunk at all]
    C --> C2[Chunk size trade-off]
    C --> C3[Overlap]
    C --> C4[Metadata → citations]
    C --> C5[Tokens ≈ words]

    B --> B1[TextExtractor: PdfPig per-page]
    B --> B2[DocumentChunker: sliding window]
    B --> B3[VectorStore payload: file/page]
    B --> B4[/ingest + /search]

    click C1 "#concept-qa" "Why chunk"
    click C2 "#concept-qa" "Chunk size"
    click C3 "#concept-qa" "Overlap"
    click C4 "#concept-qa" "Citations"
    click C5 "#concept-qa" "Tokens vs words"
    click B1 "#code-walkthrough" "TextExtractor"
    click B2 "#code-walkthrough" "DocumentChunker"
    click B3 "#code-walkthrough" "VectorStore"
    click B4 "#code-walkthrough" "endpoints"

    classDef root fill:#1e3a5f,stroke:#0d1b2a,color:#fff;
    classDef grp fill:#2d6a4f,stroke:#1b4332,color:#fff;
```

---

## Concept Q&A

**Why chunk a document instead of embedding the whole thing?**
Three reasons: (1) **precision** — one vector for a 20-page doc averages everything together, so a refund question matches weakly; (2) **context limits** — you inject retrieved text into the LLM prompt (Day 4), and a whole doc blows the context window; (3) **citations** — page-level pieces let you say "from *refund-policy.pdf, p.2*." Chunking lets retrieval return the *specific relevant passage*.

**How did you choose chunk size, and what's the trade-off?** *(the #1 RAG interview question)*
~500 tokens (≈375 words) — roughly a paragraph or two. **Too small** (~100 tokens) = precise match but loses surrounding context (a chunk saying "within 30 days" without "refunds are accepted" is useless). **Too big** (~2000 tokens) = full context but imprecise retrieval, wasted context window, higher cost. 500 is the common sweet spot. I *saw* the dial: my 375-word chunks retrieved accurately but coarsely (refunds + shipping bundled) — smaller chunks would isolate topics better.

**Why overlap, and how much?**
~40 words (~50 tokens). Each chunk repeats the tail of the previous one so a sentence straddling a boundary still appears **whole** in at least one chunk. In testing, the boundary fell mid-way through the password-reset instructions — without overlap that sentence would've been split across two chunks and lost from both; with overlap, the next chunk kept it intact.

**How do citations work?**
When storing each chunk, we attach **metadata** to the Qdrant point's payload: `filename`, `page`, `chunk_index`. On search, that metadata comes back with the hit, so results carry a source like `acme-support-policy.md (p.1)`. No metadata = no citations.

**How do you count 500 tokens exactly?**
We don't — we **approximate** tokens with whitespace-delimited words (1 token ≈ 0.75 words → 500 tokens ≈ 375 words). Honest answer: chunk size is a *soft target*, not a hard limit, and the embedding model tolerates variance, so a real tokenizer isn't worth the dependency here. (At scale you'd use the model's actual tokenizer.)

---

## Code walkthrough

Four pieces, each a small focused module:

**`Rag/TextExtractor.cs`** — file → text
- `Extract(stream, fileName)` branches on extension: PDF → PdfPig (`PdfDocument.Open` → loop `GetPages()`, one `PageText(page.Number, page.Text)` each); `.txt`/`.md` → one page.
- PDFs are extracted **per page** so page numbers stay accurate for citations. PdfPig needs a seekable stream, so the upload is copied into a `MemoryStream` first.

**`Rag/DocumentChunker.cs`** — text → chunks (pure, testable)
- Splits on whitespace into words, then slides a window: `stride = wordsPerChunk - overlapWords` (375 − 40 = 335). Chunk *n* = `words[start .. start+375]`, advance by 335, stop when the window reaches the end.
- No I/O, no dependencies — just a function. Easy to unit-test (Day 11).

**`Rag/VectorStore.cs`** — now stores metadata
- `VectorPoint` gained `Filename`, `Page`, `ChunkIndex`; `Id` is `object` so it can be an `int` (demo) or a `Guid` (ingested chunks → serialized as a UUID, both valid Qdrant ids).
- Payload now carries `{text, filename, page, chunk_index}`; `SearchHit` returns `filename` + `page` so the endpoint can format a citation.

**Endpoints in `Program.cs`:**
- `POST /ingest?dryRun=` — extract → chunk → (dryRun: return chunk previews; else embed + upsert to the `supportpilot_docs` collection). `.DisableAntiforgery()` so file uploads work without a token.
- `GET /search?q=&k=` — embed query → Qdrant search over docs → results with `score` + `source` citation.

**One-sentence flow to recite:** *extract text page-by-page, slide a 375-word window (40-word overlap) to make chunks, embed each chunk once and store it in Qdrant tagged with its filename and page; at query time embed the question, retrieve the nearest chunks, and return them with their source citation.*

---

## Talking points

- **Chunking is where RAG quality is won or lost.** Garbage chunks → garbage retrieval → garbage answers, no matter how good the LLM is. Being able to *justify* 500/50 (and show you understand the trade-off both ways) is what separates "did a RAG tutorial" from "understands RAG."

- **`dryRun` is a debugging affordance I built deliberately** — it returns the chunk boundaries without the cost of embedding, so you can inspect sizing/overlap. Shows product thinking, not just wiring.

- **The coarse-retrieval observation is honest and interview-gold.** My 375-word chunks bundled refunds + shipping into one hit. The fix isn't just "smaller chunks" (that loses context) — it's **re-ranking** (Day 15) and **hybrid search** (Day 15): retrieve broadly, then rank precisely.

- **Licensing matters in real engineering.** Chose **PdfPig (MIT)** over iText7 (AGPL) because AGPL's copyleft is a liability for closed-source employers. Knowing this signals maturity beyond just making it work.

---

## Reproduce-it cheatsheet

```bash
# Prereqs: Qdrant (docker start qdrant) + Ollama running, then:
cd backend/SupportPilot.Api && dotnet run

# 1. See the chunking WITHOUT storing (inspect sizes + overlap)
curl -X POST "http://localhost:5254/ingest?dryRun=true" \
     -F "file=@sample-docs/acme-support-policy.md"

# 2. Really ingest it (extract → chunk → embed → store)
curl -X POST "http://localhost:5254/ingest" \
     -F "file=@sample-docs/acme-support-policy.md"

# 3. Search — results come back WITH citations (filename + page)
curl "http://localhost:5254/search?q=how+long+do+refunds+take&k=2"
curl "http://localhost:5254/search?q=I+forgot+my+password&k=2"
```

**What to notice:** `dryRun` shows the last ~40 words of one chunk reappearing at the start of the next (the overlap). After ingest, every search result carries a `source` like `acme-support-policy.md (p.1)` — those citations are what make Day 4's grounded answers trustworthy.
