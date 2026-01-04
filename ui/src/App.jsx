import { useEffect, useState } from 'react';

function App() {
  const [url, setUrl] = useState('');
  const [videos, setVideos] = useState([]); // array of video URLs returned by API
  const [loading, setLoading] = useState(false);
  const [creatingTrailer, setCreatingTrailer] = useState(false);
  const [workspaceId, setWorkspaceId] = useState(null); // GUID returned by submit
  const [logs, setLogs] = useState('');

  const getVideoId = (videoUrl) => {
    try {
      const u = new URL(videoUrl);
      // youtu.be/{id}
      if (u.hostname.includes('youtu.be')) {
        return u.pathname.replace('/', '');
      }

      // youtube.com/watch?v={id}
      if (u.searchParams.has('v')) {
        return u.searchParams.get('v');
      }

      // embed or other forms: try last path segment
      const segs = u.pathname.split('/').filter(Boolean);
      return segs.length ? segs[segs.length - 1] : null;
    } catch {
      return null;
    }
  };

  const handleSubmit = async () => {
    setLoading(true);
    try {
      const response = await fetch('https://localhost:7127/api/youtube/submit', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(url)
      });

      if (!response.ok) {
        let message = '';
        try {
          const body = await response.json();
          // backend might return { message: "..."} or raw array on success
          message = body?.message ?? JSON.stringify(body);
        } catch {
          message = await response.text();
        }
        alert(`Error - status: ${response.status} message: ${message}`);
        return;
      }

      const data = await response.json();
      // Accept either legacy array response OR new { videos, id } response
      if (Array.isArray(data)) {
        setVideos(data);
        setWorkspaceId(null);
      } else if (data && Array.isArray(data.videos)) {
        setVideos(data.videos);
        setWorkspaceId(data.id ?? null);
      } else {
        alert('Error - status: 200 message: unexpected response format');
      }
    } catch (err) {
      alert(`Error - network error: ${err?.message ?? err}`);
    } finally {
      setLoading(false);
    }
  };

  const handleCreateTrailer = async () => {
    if (!videos || videos.length === 0) {
      alert('No videos to build trailer from.');
      return;
    }

    setCreatingTrailer(true);
    try {
      // include workspace id as query parameter if available
      const qs = workspaceId ? `?id=${encodeURIComponent(workspaceId)}` : '';
      const response = await fetch(`https://localhost:7127/api/youtube/trailer${qs}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(videos)
      });

      if (!response.ok) {
        let text = await response.text();
        alert(`Trailer creation failed: ${response.status} ${text}`);
        return;
      }

      const blob = await response.blob();
      const urlObj = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = urlObj;
      a.download = 'trailer.webm';
      document.body.appendChild(a);
      a.click();
      a.remove();
      window.URL.revokeObjectURL(urlObj);
    } catch (err) {
      alert(`Network error while creating trailer: ${err?.message ?? err}`);
    } finally {
      setCreatingTrailer(false);
    }
  };

  // Poll logs every 5 seconds when we have a workspaceId
  useEffect(() => {
    if (!workspaceId) {
      setLogs('');
      return;
    }

    let mounted = true;
    let timer = 0;

    const fetchLogs = async () => {
      try {
        const resp = await fetch(`https://localhost:7127/api/youtube/logs/${encodeURIComponent(workspaceId)}`);
        if (!resp.ok) {
          // keep existing logs but do not throw
          return;
        }
        const text = await resp.text();
        if (mounted) setLogs(text);
      } catch {
        // ignore network errors — polling will retry
      } finally {
        // schedule next poll
        timer = window.setTimeout(fetchLogs, 5000);
      }
    };

    fetchLogs();

    return () => {
      mounted = false;
      if (timer) window.clearTimeout(timer);
    };
  }, [workspaceId]);

  return (
    <div className="ytt-root">
      <style>{`
        :root {
          --bg: #1e1e1e;
          --panel: #252526;
          --muted: #858585;
          --text: #d4d4d4;
          --accent: #007acc;
          --accent-strong: #0a6fc3;
          --card-shadow: rgba(0,0,0,0.6);
          --border: rgba(255,255,255,0.06);
        }

        /* Ensure the app spans full available width */
        .ytt-root {
          width: 100%;
          box-sizing: border-box;
          background: linear-gradient(180deg, var(--bg) 0%, #171717 100%);
          min-height: 100vh;
          padding: 18px;
          color: var(--text);
          font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial;
        }

        .ytt-header {
          display: flex;
          align-items: center;
          gap: 12px;
          margin-bottom: 18px;
        }

        .ytt-title {
          font-size: 20px;
          margin: 0;
          display: flex;
          align-items: baseline;
          gap: 8px;
        }

        .ytt-sub {
          color: var(--muted);
          font-size: 12px;
        }

        /* Panel now stretches full width of its container */
        .ytt-panel {
          width: 100%;
          max-width: none;
          background: var(--panel);
          border: 1px solid var(--border);
          padding: 14px;
          border-radius: 8px;
          box-shadow: 0 6px 18px var(--card-shadow);
          box-sizing: border-box;
        }

        .ytt-controls {
          display: flex;
          gap: 8px;
          align-items: center;
          margin-bottom: 14px;
        }

        .ytt-input {
          flex: 1 1 auto;
          min-width: 0; /* allow flex children to shrink on small screens */
          background: transparent;
          border: 1px solid rgba(255,255,255,0.07);
          color: var(--text);
          padding: 8px 10px;
          border-radius: 6px;
          outline: none;
        }
        .ytt-input::placeholder { color: rgba(212,212,212,0.4); }

        .ytt-btn {
          background: transparent;
          color: var(--text);
          border: 1px solid rgba(255,255,255,0.06);
          padding: 8px 12px;
          border-radius: 6px;
          cursor: pointer;
          transition: all 120ms ease;
        }
        .ytt-btn:hover { transform: translateY(-1px); box-shadow: 0 4px 10px rgba(0,0,0,0.5); }
        .ytt-btn:active { transform: translateY(0); }

        .ytt-btn.primary {
          background: linear-gradient(180deg, var(--accent) 0%, var(--accent-strong) 100%);
          border: none;
          color: white;
        }
        .ytt-btn.primary[disabled], .ytt-btn[disabled] {
          opacity: 0.45;
          cursor: not-allowed;
          transform: none;
          box-shadow: none;
        }

        .ytt-videos {
          display: flex;
          flex-wrap: wrap;
          gap: 16px;
          justify-content: flex-start;
        }

        /* Make cards fluid while keeping a preferred width */
        .ytt-card {
          flex: 0 0 320px;
          width: 320px;
          background: linear-gradient(180deg, rgba(255,255,255,0.02), rgba(255,255,255,0.01));
          border: 1px solid rgba(255,255,255,0.04);
          padding: 10px;
          border-radius: 8px;
          box-sizing: border-box;
        }

        .ytt-card iframe {
          width: 100%;
          height: 180px;
          border-radius: 6px;
          border: 1px solid rgba(0,0,0,0.35);
        }

        .ytt-link {
          margin-top: 8px;
          display: block;
          color: var(--accent);
          text-decoration: none;
          word-break: break-word;
        }

        .ytt-logs {
          margin-top: 16px;
        }

        .ytt-textarea {
          width: 100%;
          height: 240px;
          background: #121212;
          color: var(--text);
          border: 1px solid rgba(255,255,255,0.04);
          padding: 12px;
          border-radius: 6px;
          font-family: monospace;
          font-size: 12px;
          white-space: pre-wrap;
          resize: vertical;
          box-sizing: border-box;
        }

        @media (max-width: 740px) {
          .ytt-controls { flex-direction: column; align-items: stretch; }
          .ytt-input { width: 100%; }
          .ytt-btn { width: 100%; }
          .ytt-card { width: 100%; flex: 1 1 auto; }
        }
      `}</style>

      <div className="ytt-header">
        <svg width="36" height="36" viewBox="0 0 24 24" fill="none" aria-hidden>
          <rect x="0.5" y="0.5" width="23" height="23" rx="4" fill="#0e60a8" />
          <path d="M9 8.5v7l6-3.5-6-3.5z" fill="white" />
        </svg>

        <div>
          <h1 className="ytt-title">YTT - YouTube To Trailer</h1>
          <div className="ytt-sub">Quickly compose short trailers from YouTube links</div>
        </div>
      </div>

      <div className="ytt-panel">
        <div className="ytt-controls">
          <input
            className="ytt-input"
            type="text"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder="Enter YouTube URL"
          />
          <button className="ytt-btn primary" onClick={handleSubmit} disabled={loading}>
            {loading ? 'Loading...' : 'Submit'}
          </button>

          <button
            className="ytt-btn"
            onClick={handleCreateTrailer}
            disabled={creatingTrailer || videos.length === 0}
            style={{ marginLeft: '4px' }}
          >
            {creatingTrailer ? 'Creating trailer...' : 'Create trailer'}
          </button>
        </div>

        <div>
          {videos.length === 0 ? (
            <p style={{ color: 'var(--muted)', margin: 0 }}>No videos to display.</p>
          ) : (
            <div className="ytt-videos">
              {videos.map((vurl, idx) => {
                const id = getVideoId(vurl);
                const src = id ? `https://www.youtube.com/embed/${id}` : vurl;
                return (
                  <div key={vurl + idx} className="ytt-card">
                    <iframe
                      src={src}
                      title={`youtube-player-${idx}`}
                      frameBorder="0"
                      allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
                      allowFullScreen
                    />
                    <a className="ytt-link" href={vurl} target="_blank" rel="noopener noreferrer">
                      {vurl}
                    </a>
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className="ytt-logs">
          <label style={{ display: 'block', marginBottom: '6px', color: 'var(--muted)' }}>
            Logs {workspaceId ? `(id: ${workspaceId})` : '(no id)'}
          </label>
          <textarea readOnly value={logs} className="ytt-textarea" />
        </div>
      </div>
    </div>
  );
}

export default App;