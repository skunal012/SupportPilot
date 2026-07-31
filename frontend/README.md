# SupportPilot — frontend

React + Vite chat UI for [SupportPilot](../README.md). Three things here are worth
knowing about:

- **SSE streaming, parsed by hand.** `src/api.js` reads the backend's
  `text/event-stream` with `fetch` + a `ReadableStream` reader rather than the
  browser's `EventSource` — `EventSource` auto-reconnects when the server closes
  the stream, which would silently re-fire the whole question after `[DONE]`, and
  it gives no cancel handle. Tokens patch the last message as they arrive.
- **Citations.** After the answer, the backend emits its sources as a
  sentinel-prefixed frame (`[CITATIONS]<json>`). `[n]` markers in the text are
  clickable and highlight the matching source, with its filename, page, and
  retrieval score.
- **Escalation cards.** When retrieval confidence is too low, the backend sends
  `[ESCALATE]<json>` — a structured handoff summary — instead of an answer, and
  it renders as a card with the suggested team.

## Develop

```bash
npm install
npm run dev        # http://localhost:5173
```

The backend must be running on `:5254` (see the [root README](../README.md)).
Vite proxies `/chat`, `/ingest`, and `/search` to it, so from the browser's point
of view everything is same-origin and there's no CORS to configure.

## Build

```bash
npm run build      # -> dist/
```

In production the built assets are served by nginx, which also reverse-proxies
the API — see `Dockerfile` and `nginx.conf`.
