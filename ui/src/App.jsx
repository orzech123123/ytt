import { useState } from 'react';

function App() {
  const [url, setUrl] = useState('');
  const [videos, setVideos] = useState([]); // array of video URLs returned by API
  const [loading, setLoading] = useState(false);
  const [creatingTrailer, setCreatingTrailer] = useState(false);

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
      // data should be an array of urls; replace existing players with returned list
      if (Array.isArray(data)) {
        setVideos(data);
      } else if (data && Array.isArray(data.videos)) {
        setVideos(data.videos);
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
      const response = await fetch('https://localhost:7127/api/youtube/trailer', {
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

  return (
    <div style={{ padding: '20px' }}>
      <h1>YouTube Linker</h1>

      <div style={{ marginBottom: '12px' }}>
        <input
          type="text"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          placeholder="Enter YouTube URL"
          style={{ width: '60%', marginRight: '8px' }}
        />
        <button onClick={handleSubmit} disabled={loading}>
          {loading ? 'Loading...' : 'Submit'}
        </button>

        {/* New action: Create trailer from listed videos */}
        <button
          onClick={handleCreateTrailer}
          disabled={creatingTrailer || videos.length === 0}
          style={{ marginLeft: '8px' }}
        >
          {creatingTrailer ? 'Creating trailer...' : 'Create trailer'}
        </button>
      </div>

      <div>
        {videos.length === 0 ? (
          <p>No videos to display.</p>
        ) : (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '16px' }}>
            {videos.map((vurl, idx) => {
              const id = getVideoId(vurl);
              const src = id ? `https://www.youtube.com/embed/${id}` : vurl;
              return (
                <div key={vurl + idx} style={{ width: '320px' }}>
                  <iframe
                    width="320"
                    height="180"
                    src={src}
                    title={`youtube-player-${idx}`}
                    frameBorder="0"
                    allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
                    allowFullScreen
                  />
                  <div style={{ marginTop: '6px', wordBreak: 'break-word' }}>
                    <a href={vurl} target="_blank" rel="noopener noreferrer">
                      {vurl}
                    </a>
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}

export default App;