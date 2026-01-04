using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class YoutubeController : ControllerBase
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly IConfiguration _configuration;

        public YoutubeController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpPost("submit")]
        public async Task<IActionResult> PostUrl([FromBody] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest("You must supply a YouTube channel or video URL in the request body.");
            }

            var apiKey = _configuration["YouTubeApiKey"] ?? Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return BadRequest("Missing YouTube API key. Set configuration key 'YouTubeApiKey' or environment variable 'YOUTUBE_API_KEY'.");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                // Try to tolerate addresses without scheme
                if (Uri.TryCreate("https://" + url, UriKind.Absolute, out uri) == false)
                {
                    return BadRequest("Invalid URL format.");
                }
            }

            string channelId = null;

            // 1) If URL is a channel URL: /channel/{id}
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0].Equals("channel", StringComparison.OrdinalIgnoreCase))
            {
                channelId = segments[1];
            }

            // 2) If URL is a user URL: /user/{username} -> resolve with channels.list?forUsername=
            if (string.IsNullOrEmpty(channelId) && segments.Length >= 2 && segments[0].Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                var username = segments[1];
                channelId = await ResolveChannelIdByUsernameAsync(username, apiKey);
            }

            // 3) If URL is a handle or custom: /@handle or /c/{name} -> try search by the last segment
            if (string.IsNullOrEmpty(channelId) && segments.Length >= 1)
            {
                var first = segments[0];
                if (first.StartsWith("@"))
                {
                    var handle = first.TrimStart('@');
                    channelId = await ResolveChannelIdByQueryAsync(handle, apiKey);
                }
                else if (first.Equals("c", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
                {
                    var custom = segments[1];
                    channelId = await ResolveChannelIdByQueryAsync(custom, apiKey);
                }
            }

            // 4) If URL is a video URL (watch?v= or youtu.be/{id}) -> get video details to find channelId
            if (string.IsNullOrEmpty(channelId))
            {
                string videoId = null;
                if ((uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase)))
                {
                    var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    videoId = q["v"];
                }
                else if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
                {
                    var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segs.Length >= 1)
                        videoId = segs[0];
                }

                if (!string.IsNullOrEmpty(videoId))
                {
                    channelId = await ResolveChannelIdByVideoIdAsync(videoId, apiKey);
                }
            }

            if (string.IsNullOrEmpty(channelId))
            {
                return BadRequest("Could not resolve a channel ID from the provided URL.");
            }

            // Return 3 random videos from the channel (not the 3 most recent)
            try
            {
                var videos = await GetRandomVideoUrlsForChannelAsync(channelId, 3, apiKey);
                return Ok(videos);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, $"Error communicating with YouTube API: {ex.Message}");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Unexpected error: {ex.Message}");
            }
        }

        // NEW: Create trailer from provided list of video URLs.
        // This implementation calls external tools: `yt-dlp` to download and `ffmpeg` to cut + concat.
        // Requirements: yt-dlp and ffmpeg must be installed and available in PATH on the server.
        // Behaviour: prefer best video up to 1080p and output WebM clips/trailer.
        [HttpPost("trailer")]
        public async Task<IActionResult> CreateTrailer([FromBody] string[] urls, CancellationToken cancellationToken)
        {
            if (urls == null || urls.Length == 0)
                return BadRequest("Provide an array of video URLs.");

            // Limit to a reasonable number to avoid long processing
            var maxVideos = 20;
            var list = urls.Take(maxVideos).ToArray();

            var tempRoot = Path.Combine(Path.GetTempPath(), "ytt_trailer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var downloadedFiles = new List<string>();
            var clipFiles = new List<string>();
            try
            {
                // 1) Download each video using yt-dlp (needs to be installed on server)
                for (var i = 0; i < list.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = list[i];
                    var prefix = Path.Combine(tempRoot, $"downloaded{i}");
                    var downloaded = await DownloadWithYtDlpAsync(url, prefix, cancellationToken);
                    if (downloaded != null)
                    {
                        downloadedFiles.Add(downloaded);

                        // 2) Cut a short segment: from 3s to 7s (duration 4s). Output WebM clips using libvpx-vp9 + libopus.
                        var clipPath = Path.Combine(tempRoot, $"clip{i}.webm");
                        var ffmpegArgs = $"-ss 3 -t 4 -i \"{downloaded}\" -c:v libvpx-vp9 -b:v 0 -crf 30 -c:a libopus -b:a 128k -y \"{clipPath}\"";
                        var rc = await RunProcessAsync("ffmpeg", ffmpegArgs, tempRoot, cancellationToken);
                        if (rc != 0)
                        {
                            // try fallback without -ss before input (safer for some formats)
                            ffmpegArgs = $"-i \"{downloaded}\" -ss 3 -t 4 -c:v libvpx-vp9 -b:v 0 -crf 30 -c:a libopus -b:a 128k -y \"{clipPath}\"";
                            rc = await RunProcessAsync("ffmpeg", ffmpegArgs, tempRoot, cancellationToken);
                        }

                        if (rc == 0 && System.IO.File.Exists(clipPath))
                        {
                            clipFiles.Add(clipPath);
                        }
                    }
                }

                if (clipFiles.Count == 0)
                    return StatusCode(500, "Failed to create any clips.");

                // 3) Create concat list
                var listFile = Path.Combine(tempRoot, "concat_list.txt");
                using (var sw = new StreamWriter(listFile, false))
                {
                    foreach (var clip in clipFiles)
                    {
                        // ffmpeg concat demuxer expects paths in single quotes; ensure proper escaping
                        sw.WriteLine($"file '{clip.Replace("'", "'\\''")}'");
                    }
                }

                // 4) Concat into final trailer (WebM)
                var finalPath = Path.Combine(tempRoot, "trailer.webm");
                var concatArgs = $"-f concat -safe 0 -i \"{listFile}\" -c copy -y \"{finalPath}\"";
                // Use stream copy because clips were encoded with same codecs (libvpx-vp9 / libopus).
                var concatRc = await RunProcessAsync("ffmpeg", concatArgs, tempRoot, cancellationToken);
                if (concatRc != 0 || !System.IO.File.Exists(finalPath))
                {
                    return StatusCode(500, "Failed to concatenate clips into trailer.");
                }

                // 5) Stream the file back
                var fs = System.IO.File.OpenRead(finalPath);
                // Return FileStreamResult; letting ASP.NET Core handle range requests may be beneficial for large files
                return File(fs, "video/webm", "trailer.webm");
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, "Request cancelled.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Unexpected error while creating trailer: {ex.Message}");
            }
            finally
            {
                // cleanup: try best-effort to delete temporary files (do not block response)
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (Directory.Exists(tempRoot))
                            Directory.Delete(tempRoot, true);
                    }
                    catch { /* ignore cleanup errors */ }
                });
            }
        }

        private async Task<string> DownloadWithYtDlpAsync(string url, string destPrefix, CancellationToken cancellationToken)
        {
            // Prefer best video up to 1080p and merge to webm when possible.
            // Format selection: bestvideo with height <= 1080 + bestaudio, fallback to best.
            // Also instruct yt-dlp to merge output format to webm (requires ffmpeg).
            var outputTemplate = destPrefix + ".%(ext)s";
            var format = "bestvideo[height<=1080]+bestaudio/best[height<=1080]/best";
            var args = $"-f \"{format}\" --merge-output-format webm -o \"{outputTemplate}\" \"{url}\"";

            var rc = await RunProcessAsync("yt-dlp", args, Path.GetDirectoryName(destPrefix), cancellationToken);
            if (rc != 0)
            {
                return null;
            }

            // find created file
            var dir = Path.GetDirectoryName(destPrefix);
            var prefix = Path.GetFileName(destPrefix) + ".";
            var files = Directory.GetFiles(dir, prefix + "*").OrderBy(f => new FileInfo(f).Length).ToArray();
            // choose the first matching file (there should be at least one)
            return files.Length > 0 ? files[0] : null;
        }

        private async Task<int> RunProcessAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = /*workingDirectory ??*/ Environment.CurrentDirectory
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (s, e) =>
            {
                tcs.TrySetResult(proc.ExitCode);
            };

            try
            {
                proc.Start();

                // Drain output/error to avoid deadlocks
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var outTask = proc.StandardOutput.ReadToEndAsync();
                        var errTask = proc.StandardError.ReadToEndAsync();
                        await Task.WhenAll(outTask, errTask);
                        // You can log outTask.Result and errTask.Result if needed
                    }
                    catch { }
                }, cancellationToken);

                using (cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!proc.HasExited) proc.Kill(true);
                    }
                    catch { }
                }))
                {
                    return await tcs.Task;
                }
            }
            catch
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(true);
                }
                catch { }
                throw;
            }
        }

        private async Task<string> ResolveChannelIdByUsernameAsync(string username, string apiKey)
        {
            var uri = $"https://www.googleapis.com/youtube/v3/channels?part=id&forUsername={Uri.EscapeDataString(username)}&key={apiKey}";
            var resp = await _httpClient.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() > 0)
            {
                return items[0].GetProperty("id").GetString();
            }
            return null;
        }

        private async Task<string> ResolveChannelIdByQueryAsync(string query, string apiKey)
        {
            // Search for channel by query (handles, custom names)
            var uri = $"https://www.googleapis.com/youtube/v3/search?part=snippet&type=channel&maxResults=1&q={Uri.EscapeDataString(query)}&key={apiKey}";
            var resp = await _httpClient.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() > 0)
            {
                var channelId = items[0].GetProperty("snippet").GetProperty("channelId").GetString();
                return channelId;
            }
            return null;
        }

        private async Task<string> ResolveChannelIdByVideoIdAsync(string videoId, string apiKey)
        {
            var uri = $"https://www.googleapis.com/youtube/v3/videos?part=snippet&id={Uri.EscapeDataString(videoId)}&key={apiKey}";
            var resp = await _httpClient.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() > 0)
            {
                return items[0].GetProperty("snippet").GetProperty("channelId").GetString();
            }
            return null;
        }

        private async Task<string[]> GetLatestVideoUrlsForChannelAsync(string channelId, int maxResults, string apiKey)
        {
            var uri = $"https://www.googleapis.com/youtube/v3/search?part=snippet&channelId={Uri.EscapeDataString(channelId)}&order=date&type=video&maxResults={maxResults}&key={apiKey}";
            var resp = await _httpClient.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            var urls = items.EnumerateArray()
                .Select(item =>
                {
                    // item.id.videoId contains the id for search results
                    if (item.TryGetProperty("id", out var idEl) && idEl.TryGetProperty("videoId", out var vidEl))
                    {
                        var vid = vidEl.GetString();
                        return $"https://www.youtube.com/watch?v={vid}";
                    }
                    return null;
                })
                .Where(s => s != null)
                .ToArray();

            return urls;
        }

        // New: fetch up to 50 recent videos and pick 'count' random ones from that set.
        // Note: YouTube Data API's search.maxResults is capped at 50; this returns random picks from that window.
        private async Task<string[]> GetRandomVideoUrlsForChannelAsync(string channelId, int count, string apiKey)
        {
            const int apiMax = 50;
            var uri = $"https://www.googleapis.com/youtube/v3/search?part=snippet&channelId={Uri.EscapeDataString(channelId)}&order=date&type=video&maxResults={apiMax}&key={apiKey}";
            var resp = await _httpClient.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");

            var list = items.EnumerateArray()
                .Select(item =>
                {
                    if (item.TryGetProperty("id", out var idEl) && idEl.TryGetProperty("videoId", out var vidEl))
                    {
                        var vid = vidEl.GetString();
                        return $"https://www.youtube.com/watch?v={vid}";
                    }
                    return null;
                })
                .Where(s => s != null)
                .ToList();

            if (list.Count == 0)
                return Array.Empty<string>();

            // If requested count >= available, return all (shuffled)
            var rand = Random.Shared;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }

            return list.Take(Math.Min(count, list.Count)).ToArray();
        }
    }
}
