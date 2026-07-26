// All backend calls go through Vite's dev proxy (see vite.config.js), so from
// the browser's point of view everything is same-origin: /chat, /ingest.

/**
 * Send the conversation so far and stream the grounded answer back token-by-token.
 *
 * DAY 10 — MEMORY: we POST the whole message history (not just the latest
 * question) so follow-ups like "what about international?" resolve. An LLM has no
 * memory of its own — "memory" is just replaying the conversation into the prompt.
 *
 * The backend replies with Server-Sent Events (Content-Type: text/event-stream).
 * Each frame looks like:
 *     data: <payload>\n\n
 * Most frames are answer tokens. Four are special sentinels:
 *     data: [CITATIONS]<json>   → the sources behind the answer (sent once, at the end)
 *     data: [ESCALATE]<json>    → DAY 10: retrieval confidence too low; handed to a human
 *     data: [DONE]              → the stream is finished
 *     data: [error] <message>   → the backend hit a problem
 *
 * Why not the browser's built-in EventSource? Two reasons:
 *   1. EventSource AUTO-RECONNECTS when the server closes the connection — after
 *      our [DONE] it would silently re-fire the whole question again.
 *   2. It's awkward with custom sentinels and gives us no cancel handle.
 * So we read the raw byte stream with fetch + a ReadableStream reader and parse
 * the SSE frames ourselves. More code, but we control exactly what happens.
 *
 * `history` is the full conversation as [{ role: 'user'|'assistant', content }],
 * ending with the new user turn.
 * Callbacks: onToken(text), onCitations(array), onEscalate(summary), onDone(), onError(message).
 * `signal` is an optional AbortSignal to cancel an in-flight answer.
 */
export async function streamChat(history, { onToken, onCitations, onEscalate, onDone, onError, signal }) {
  let response
  try {
    response = await fetch('/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ messages: history }),
      signal,
    })
  } catch (err) {
    onError?.(`Could not reach the server: ${err.message}`)
    return
  }
  if (!response.ok || !response.body) {
    onError?.(`Server responded ${response.status}`)
    return
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  try {
    while (true) {
      const { value, done } = await reader.read()
      if (done) break

      // Network chunks can split a frame anywhere, so we accumulate bytes and
      // only act on COMPLETE frames. Frames are separated by a blank line ("\n\n").
      buffer += decoder.decode(value, { stream: true })
      const frames = buffer.split('\n\n')
      buffer = frames.pop() ?? '' // keep the trailing partial frame for the next read

      for (const frame of frames) {
        if (!frame.startsWith('data:')) continue
        // Drop "data:" and exactly ONE optional leading space (the SSE spec strips
        // a single space), which preserves the token's own leading space.
        const payload = frame.startsWith('data: ') ? frame.slice(6) : frame.slice(5)

        if (payload === '[DONE]') { onDone?.(); return }
        if (payload.startsWith('[CITATIONS]')) {
          try { onCitations?.(JSON.parse(payload.slice('[CITATIONS]'.length))) }
          catch { /* ignore malformed citation json */ }
          continue
        }
        if (payload.startsWith('[ESCALATE]')) {
          try { onEscalate?.(JSON.parse(payload.slice('[ESCALATE]'.length))) }
          catch { /* ignore malformed escalation json */ }
          continue
        }
        if (payload.startsWith('[error]')) { onError?.(payload); return }

        onToken?.(payload)
      }
    }
    onDone?.() // stream ended without an explicit [DONE]
  } catch (err) {
    if (err.name !== 'AbortError') onError?.(err.message)
  }
}

/**
 * Upload a document to the ingestion pipeline (Day 3).
 * Returns { file, pages, chunks, collection }.
 */
export async function ingestDocument(file) {
  const form = new FormData()
  form.append('file', file)
  const res = await fetch('/ingest', { method: 'POST', body: form })
  if (!res.ok) throw new Error(`Ingest failed (${res.status})`)
  return res.json()
}
