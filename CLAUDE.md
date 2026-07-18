# SupportPilot — 3-Week AI Learning & Build Plan

> **Purpose of this file:** Project context + roadmap. Drop this in the repo root so AI coding assistants (Copilot / Claude) understand the project goals, stack, and current phase.

---

## 1. The Real-World Problem

Small businesses and support teams drown in repetitive customer questions ("Where's my order?", "What's your refund policy?", "How do I reset X?"). The answers exist — buried in PDFs, help docs, and internal systems — but a human must search, read, and type a reply every time. First-response times stretch to hours; support staff burn out on copy-paste work.

**Why AI fixes it:** An LLM alone can't answer (it doesn't know the company's policies and will hallucinate). But:

- **RAG (Retrieval-Augmented Generation)** grounds the LLM in the company's actual documents.
- **Function calling** lets it fetch live data (e.g., order status) from internal APIs.
- **Escalation logic** hands off to a human when confidence is low.

That combination — retrieve real knowledge + take real actions + escalate when unsure — is exactly what companies are building right now.

## 2. The Project: "SupportPilot" — AI Customer Support Assistant

**Core flow:**

1. User asks a question in a React chat UI.
2. ASP.NET Core backend retrieves relevant chunks from uploaded company docs (RAG) and answers **with citations**, streamed token-by-token.
3. If the question needs live data ("where's order #1234?"), the model **calls a function** hitting a mock Orders API.
4. If confidence is low, it generates a structured **escalation summary** for a human agent.
5. Deployed on Azure with Docker + GitHub Actions CI/CD.

**Skills covered:** prompt engineering, embeddings, vector search, RAG, function calling / tool use, agentic flow, streaming, guardrails, evaluation, cloud deployment.

## 3. Tech Stack

| Layer | Choice | Notes |
|---|---|---|
| Backend | ASP.NET Core (C#) | Existing skill |
| LLM Framework | **Semantic Kernel** | Microsoft's official LLM framework for .NET — the honest LangChain equivalent for this stack |
| LLM Provider | OpenAI API **or** Azure OpenAI | Azure OpenAI = stronger signal for .NET/Azure employers, more setup friction. Decide before Day 1. |
| Vector DB | Qdrant (Docker) or Postgres + pgvector | Qdrant is easiest to start |
| Frontend | React.js | Existing skill; new part is consuming SSE streams |
| Deployment | Docker + Azure Container Apps + GitHub Actions | Existing skills |

> **Honesty rule:** Only list on the resume what is actually used in this project. Use Semantic Kernel, not LangChain (LangChain is Python/JS-first).

---

## Week 1 — Foundations + Core RAG

### Day 1 — LLM basics + first API call
- **Learn:** tokens, temperature, system vs. user prompts, why LLMs hallucinate.
- **Build:** minimal ASP.NET Core endpoint calling the chat API; get **streaming** working (Server-Sent Events).

### Days 2–3 — Embeddings + vector search (take two days — this is the concept interviewers probe hardest)
- **Learn:** what embeddings are, cosine similarity, what a vector DB does, chunking trade-offs.
- **Build:** embed ~10 sample sentences, store in Qdrant (Docker), run a similarity search. Then build the **ingestion pipeline**: upload endpoint → extract PDF text → chunk (~500 tokens, 50 overlap) → embed → store with metadata (filename, page).

### Day 4 — The RAG loop with Semantic Kernel
- **Build:** question → embed → retrieve top-5 chunks → grounded prompt ("Answer ONLY from the context below; say 'I don't know' otherwise") → answer with source citations. Wire through Semantic Kernel.

### Day 5 — React chat frontend
- **Build:** chat UI with streaming token display, file-upload panel, clickable citations.

### Days 6–7 (weekend) — Consolidate
- Fix bugs. Seed with a realistic fake company's docs (write a refund policy, shipping FAQ, product manual — makes the demo feel real). Push to GitHub with clean commit history.

---

## Week 2 — Agentic Features, Quality, Shipping

### Days 8–9 — Function calling (two days, saner pace)
- **Build:** mock `GET /orders/{id}` API; register it as a tool so the model decides *when* to call it. Test mixed questions ("what's your refund policy, and where is order 1042?").

### Day 10 — Memory + escalation
- **Build:** multi-turn conversation history (follow-ups like "what about international shipping?" work). Escalation path: when retrieval confidence is low, generate a structured handoff summary instead of guessing.

### Day 11 — Guardrails + evaluation (v1)
- Write ~15 test questions with known-correct answers, including 3 the docs *can't* answer (expected: "I don't know"). Measure correctness and groundedness. Put the eval table in the README.

### Day 12 — Ship it
- Dockerize frontend + backend, deploy to **Azure Container Apps**, set up GitHub Actions CI/CD.

### Days 13–14 — Make it presentable
- README with architecture diagram, 60–90 second demo GIF, eval results. This is what recruiters actually click.

---

## Week 3 — Differentiators (what separates this from "another RAG tutorial project")

### Day 15 — Hybrid search + re-ranking
- Pure vector search misses exact keywords ("order #1042", SKUs). Add keyword (BM25) search alongside vector search, merge, re-rank. The #1 "how would you improve RAG?" interview answer — actually implemented.

### Day 16 — Query rewriting + conversation-aware retrieval
- "What about international?" retrieves badly without context. Have the LLM rewrite follow-ups into standalone queries before retrieval. Small feature, big quality jump.

### Day 17 — Admin analytics dashboard
- React page: questions asked, % answered vs. escalated, most-cited documents, unanswered-question log (= "what docs should the company write next"). Reframes the project from "chatbot" to "product."

### Day 18 — Structured outputs: auto-triage workflow
- New endpoint: paste a raw customer email → get structured JSON (category, sentiment, urgency, suggested reply, needs-human flag). One of the most-used LLM patterns in industry.

### Day 19 — Harden evals + cost/latency awareness
- Grow test set to ~30 questions. Add a cheap LLM-as-judge scoring script. Log token usage per request with a cost estimate.

### Day 20 — Write it up publicly
- LinkedIn post or short blog: "What I learned building a RAG support assistant in .NET" — architecture diagram, one hard problem hit, how it was solved.

### Day 21 — Mock interview day
- Rehearse out loud: 2-minute architecture walkthrough; justify every choice (why Qdrant, why 500-token chunks, why hybrid search); answer "what would you do differently at 10x scale?"
- **Rule:** if you can't explain a line on your resume fluently, cut it or re-learn it.

> **Warning for Week 3:** Do NOT add more tools (second vector DB, second framework, voice input). Depth on one system beats a longer buzzword list — every Week 3 item deepens the *same* project.

---

## Resume Output (earned after completion)

**Skills line:**

> AI & LLM: GitHub Copilot · OpenAI API / Azure OpenAI · Semantic Kernel · RAG · Embeddings & Vector Search (Qdrant) · LLM Function Calling

**Project bullets:**

- Built an AI customer-support assistant (ASP.NET Core, React, Semantic Kernel) using RAG over company documents, with source citations and streaming responses.
- Implemented LLM function calling to fetch live order data, with automatic human-escalation summaries for low-confidence queries.
- Designed a chunking/embedding ingestion pipeline (Qdrant) and improved retrieval accuracy with hybrid (vector + keyword) search, re-ranking, and LLM-based query rewriting for multi-turn conversations.
- Built an analytics dashboard tracking answer rates, escalations, and content gaps; instrumented per-query token cost and an automated evaluation pipeline (30-case test suite with LLM-as-judge scoring).
- Containerized and deployed to Azure Container Apps with GitHub Actions CI/CD.

---

## Open Decision (make before Day 1)

**OpenAI API direct vs. Azure OpenAI:**

- **OpenAI direct** — simpler signup, pay-as-you-go (~$5 covers the whole project).
- **Azure OpenAI** — requires an Azure subscription + model deployment setup, but stronger resume signal for .NET/Azure-stack employers. With 3 weeks, there is slack to absorb the setup friction — recommended if targeting Microsoft-stack roles.

## Interview Prep Cheat-Sheet (know cold by Day 21)

- Why RAG instead of fine-tuning?
- How did you choose chunk size and overlap, and what trade-offs did you observe?
- How does function calling work end-to-end (schema → model decision → execution → result injection)?
- Why hybrid search? What does re-ranking add?
- How do you measure answer quality? What is LLM-as-judge and its limits?
- What would you improve at 10x scale? (caching, re-ranking models, async ingestion, cost controls)

---

## Agent skills

### Issue tracker

Issues live in this repo's GitHub Issues, managed via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default canonical labels (needs-triage, needs-info, ready-for-agent, ready-for-human, wontfix). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.