import { useEffect, useRef, useState } from 'react'
import { streamChat, ingestDocument } from './api'
import './App.css'

export default function App() {
  const [messages, setMessages] = useState([]) // { role, text, citations }
  const [input, setInput] = useState('')
  const [isStreaming, setIsStreaming] = useState(false)
  const [upload, setUpload] = useState(null) // { status: 'busy'|'ok'|'error', text }
  const abortRef = useRef(null)
  const scrollRef = useRef(null)

  // Keep the transcript scrolled to the newest tokens as they stream in.
  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight })
  }, [messages])

  async function handleAsk(e) {
    e.preventDefault()
    const question = input.trim()
    if (!question || isStreaming) return

    // Push the user's message + an empty assistant message we'll stream into.
    setMessages((prev) => [
      ...prev,
      { role: 'user', text: question },
      { role: 'assistant', text: '', citations: null },
    ])
    setInput('')
    setIsStreaming(true)

    const controller = new AbortController()
    abortRef.current = controller

    // Helper: mutate only the LAST message (the assistant one we just added).
    const patchLast = (fn) =>
      setMessages((prev) => {
        const next = [...prev]
        next[next.length - 1] = fn(next[next.length - 1])
        return next
      })

    await streamChat(question, {
      signal: controller.signal,
      onToken: (t) => patchLast((m) => ({ ...m, text: m.text + t })),
      onCitations: (c) => patchLast((m) => ({ ...m, citations: c })),
      onError: (msg) =>
        patchLast((m) => ({ ...m, text: (m.text || '') + `\n\n⚠️ ${msg}` })),
      onDone: () => {},
    })

    setIsStreaming(false)
    abortRef.current = null
  }

  function handleStop() {
    abortRef.current?.abort()
    setIsStreaming(false)
  }

  async function handleFile(e) {
    const file = e.target.files?.[0]
    if (!file) return
    setUpload({ status: 'busy', text: `Ingesting ${file.name}…` })
    try {
      const r = await ingestDocument(file)
      setUpload({
        status: 'ok',
        text: `Ingested ${r.file}: ${r.pages} page(s) → ${r.chunks} chunk(s).`,
      })
    } catch (err) {
      setUpload({ status: 'error', text: err.message })
    } finally {
      e.target.value = '' // allow re-uploading the same file
    }
  }

  return (
    <div className="app">
      <header className="header">
        <h1>SupportPilot</h1>
        <p>Ask about the ingested docs — answers are grounded and cited.</p>
      </header>

      <section className="uploader">
        <label className="upload-btn">
          + Upload document
          <input type="file" accept=".pdf,.txt,.md" onChange={handleFile} hidden />
        </label>
        {upload && <span className={`upload-status ${upload.status}`}>{upload.text}</span>}
      </section>

      <main className="transcript" ref={scrollRef}>
        {messages.length === 0 && (
          <div className="empty">
            Try: <em>“how long do refunds take?”</em>
          </div>
        )}
        {messages.map((m, i) => (
          <Message key={i} message={m} streaming={isStreaming && i === messages.length - 1} />
        ))}
      </main>

      <form className="composer" onSubmit={handleAsk}>
        <input
          type="text"
          value={input}
          placeholder="Ask a question…"
          onChange={(e) => setInput(e.target.value)}
          disabled={isStreaming}
        />
        {isStreaming ? (
          <button type="button" className="stop" onClick={handleStop}>Stop</button>
        ) : (
          <button type="submit" disabled={!input.trim()}>Ask</button>
        )}
      </form>
    </div>
  )
}

function Message({ message, streaming }) {
  const [active, setActive] = useState(null) // highlighted citation number
  const isUser = message.role === 'user'

  return (
    <div className={`msg ${message.role}`}>
      <div className="bubble">
        {isUser ? (
          message.text
        ) : (
          <AnswerText text={message.text} onCite={setActive} active={active} />
        )}
        {streaming && !isUser && <span className="cursor">▋</span>}
      </div>

      {message.citations?.length > 0 && (
        <ol className="citations">
          {message.citations.map((c) => (
            <li
              key={c.n}
              className={active === c.n ? 'cited-active' : ''}
              onMouseLeave={() => setActive(null)}
            >
              <span className="cite-n">[{c.n}]</span> {c.source}
              <span className="cite-score"> · score {c.score}</span>
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

// Render answer text, turning inline [n] markers into clickable buttons that
// highlight the matching source in the citation list below.
function AnswerText({ text, onCite, active }) {
  const parts = text.split(/(\[\d+\])/g)
  return parts.map((part, i) => {
    const match = /^\[(\d+)\]$/.exec(part)
    if (!match) return <span key={i}>{part}</span>
    const n = Number(match[1])
    return (
      <button
        key={i}
        className={`cite-marker ${active === n ? 'active' : ''}`}
        onClick={() => onCite(n)}
        title={`Jump to source ${n}`}
      >
        [{n}]
      </button>
    )
  })
}
