# Day 4 — Interview Revision: The RAG Loop (Grounded Generation)

> **What Day 4 delivered:** the payoff. `/chat` now **retrieves** relevant chunks, **grounds** the model in them, **streams** the answer, and appends **citations**. The same refund question that made Day 1's model hedge now returns "refunds are processed within 5–7 business days [1]" with a source. Unanswerable questions get "I don't know based on the available documents" instead of a hallucination.
>
> **Run it:** ingest a doc (Day 3), then `GET /chat?q=how+long+do+refunds+take` — streams a grounded, cited answer.

---

## Topic map

```mermaid
flowchart TD
    D4((Day 4)):::root

    D4 --> C[Concepts]:::grp
    D4 --> B[Build]:::grp

    C --> C1[RAG vs fine-tuning]
    C --> C2[Grounding prompt]
    C --> C3[Anti-hallucination / I-don't-know]
    C --> C4[Citations]
    C --> C5[Retrieve → Augment → Generate]

    B --> B1[/chat: retrieve top-K]
    B --> B2[Build context block]
    B --> B3[StreamOllamaAnswer helper]
    B --> B4[CITATIONS event]

    click C1 "#concept-qa" "RAG vs fine-tuning"
    click C2 "#concept-qa" "Grounding prompt"
    click C3 "#concept-qa" "Anti-hallucination"
    click C4 "#concept-qa" "Citations"
    click C5 "#concept-qa" "R-A-G"
    click B1 "#code-walkthrough" "retrieve"
    click B2 "#code-walkthrough" "context"
    click B3 "#code-walkthrough" "stream helper"
    click B4 "#code-walkthrough" "citations"

    classDef root fill:#1e3a5f,stroke:#0d1b2a,color:#fff;
    classDef grp fill:#2d6a4f,stroke:#1b4332,color:#fff;
```

---

## Concept Q&A

**Why RAG instead of fine-tuning?** *(the flagship interview question)*
Fine-tuning bakes knowledge into weights — expensive, slow to update, and it still *hallucinates* because the model doesn't know what it doesn't know. RAG keeps knowledge **external**: retrieve the relevant text at query time and put it in the prompt. Benefits: (1) update knowledge by re-ingesting docs, not retraining; (2) **citations** — you can point to the source; (3) **less hallucination** — the model reads rather than recalls; (4) cheaper. Fine-tuning is for *behavior/format/tone*; RAG is for *knowledge*. Often you use both.

**What does the grounding prompt actually say, and why does each part matter?**
Three rules: (1) *"Answer using ONLY the context below"* — stops the model using unreliable training knowledge; (2) *"If it's not in the context, say 'I don't know based on the available documents'"* — an explicit escape hatch so it refuses instead of inventing; (3) *"cite the source number like [1]"* — forces traceability. The context block is the retrieved chunks, each numbered. This is 90% of RAG quality — the retrieval finds the text, but the prompt decides whether the model trusts it.

**How does the "I don't know" guardrail work — and why is it essential?**
The system prompt explicitly instructs refusal when the answer isn't in context. Tested live: "who is the CEO of Acme Gadgets?" (not in the docs) → "I don't know based on the available documents." Essential because a support bot that *confidently makes up* a refund window or a shipping time is worse than useless — it creates liability. Refusing is the correct behavior. (Day 10 upgrades this into a human **escalation**.)

**How do citations get produced end-to-end?**
Each retrieved chunk is numbered `[1]`, `[2]`… in the context block, so the model can reference them inline. Separately, after the answer streams, the endpoint emits a structured `[CITATIONS]` SSE event listing each source's filename, page, and cosine score. The frontend (Day 5) parses that to render clickable citations. The metadata that makes this possible was stored back on Day 3.

**Spell out R-A-G in this codebase.**
**R**etrieve — embed the question, Qdrant returns top-K chunks (`VectorStore.SearchAsync`). **A**ugment — build a system prompt that embeds those chunks as context. **G**enerate — stream the model's answer from that augmented prompt (`StreamOllamaAnswer`). Retrieval quality caps the whole thing: no matter how good the model, if the wrong chunk is retrieved, the answer is wrong.

---

## Code walkthrough

All in `Program.cs`, `/chat` endpoint (Day 1's naive version was replaced):

| Step | Code | Notes |
|---|---|---|
| **1. Retrieve** | `EnsureCollectionAsync` → `EmbedAsync(q)` → `SearchAsync(DocsCollection, …, k ?? 5)` | Same retrieval as `/search`. EnsureCollection avoids a 404 if nothing's ingested. |
| **2. Ground** | Loop hits into a numbered `StringBuilder` context block; assemble `systemPrompt` = persona + rules + context | The rules ("ONLY the context", "else I don't know", "cite [n]") are the heart of RAG. |
| **3. Generate** | `StreamOllamaAnswer(messages, model, response, …)` | Extracted helper — reuses Day 1's SSE/NDJSON streaming; does NOT emit `[DONE]` so the caller can send citations first. |
| **4. Citations** | Emit `data: [CITATIONS]<json>` then `data: [DONE]` | Sentinel-prefixed, same style as `[DONE]`; frontend parses it. |

**Refactor worth noting:** the Ollama streaming logic moved out of `/chat` into a reusable `StreamOllamaAnswer` helper — a small "deep module" the RAG loop calls. `/chat`'s body now reads as the four RAG steps, not HTTP plumbing.

**One-sentence flow to recite:** *embed the question, retrieve the top-K chunks from Qdrant, build a system prompt that says "answer only from this numbered context or admit you don't know, and cite sources," stream the model's answer, then emit the citation list.*

---

## Talking points

- **"RAG turns the LLM's job from *recall* into *reading comprehension*."** The model can't recall your refund policy (it was never trained on it) but it's excellent at reading a paragraph and answering from it. That reframing is the whole reason RAG works — and a crisp way to explain it in an interview.

- **The prompt is the product.** Retrieval was built on Days 2–3; Day 4 is almost entirely a *prompt* ("only from context, else I don't know, cite sources") plus wiring. Small change, huge behavior difference — Day 1 hedged, Day 4 answers and refuses correctly.

- **Refusing is a feature.** I demonstrated the model declining to name a CEO that isn't in the docs. In support, a confident wrong answer is a liability; "I don't know" is the safe, correct default — and the seed of escalation (Day 10).

- **Citations = trust.** Grounded answers you can't verify are still risky. Emitting `filename (p.N)` per answer lets a human check the source — the difference between a toy and something a business would deploy.

---

## Reproduce-it cheatsheet

```bash
# Prereqs: Qdrant + Ollama up, and at least one doc ingested (Day 3):
curl -X POST "http://localhost:5254/ingest" -F "file=@sample-docs/acme-support-policy.md"

# Answerable question → grounded answer + [CITATIONS] event
curl -N "http://localhost:5254/chat?q=how+long+do+refunds+take"

# Unanswerable question → "I don't know based on the available documents."
curl -N "http://localhost:5254/chat?q=who+is+the+CEO+of+Acme+Gadgets"
```

**What to notice in the raw stream:** answer tokens arrive as `data: <token>`; then a single `data: [CITATIONS][{...}]` event with sources + scores; then `data: [DONE]`. The `[1]` inline in the answer maps to the first citation. Ask something outside the docs and watch it refuse instead of inventing — that's the guardrail.
