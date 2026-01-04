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
                // Include an id (GUID) that will be used by the frontend to poll logs and to create trailer in that folder.
                var id = Guid.NewGuid().ToString("N");
                return Ok(new { videos, id });
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
        // Added parameter cleanFiles (default false) to control whether temporary files are removed.
        // Added optional query parameter `id` to let caller supply directory name (GUID like string).
        [HttpPost("trailer")]
        public async Task<IActionResult> CreateTrailer([FromBody] string[] urls, [FromQuery] bool cleanFiles = false, [FromQuery] string id = null, CancellationToken cancellationToken = default)
        {
            if (urls == null || urls.Length == 0)
                return BadRequest("Provide an array of video URLs.");

            // Limit to a reasonable number to avoid long processing
            var maxVideos = 20;
            var list = urls.Take(maxVideos).ToArray();

            // sanitize/ensure id - use provided or generate new
            if (string.IsNullOrWhiteSpace(id))
                id = Guid.NewGuid().ToString("N");
            else
                id = new string(id.Where(char.IsLetterOrDigit).ToArray()); // basic sanitation

            var tempRoot = Path.Combine(Path.GetTempPath(), "ytt_trailer", id);
            Directory.CreateDirectory(tempRoot);

            var statusFile = Path.Combine(tempRoot, "status.txt");

            var downloadedFiles = new List<string>();
            var clipFiles = new List<string>();
            try
            {
                AppendStatus(statusFile, $"[INFO] Starting trailer creation for id={id} at {DateTime.UtcNow:O}");

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

                        // Persist process output into central status file
                        AppendStatus(statusFile, $"[ffmpeg-clip-{i}] ExitCode={rc.ExitCode}\nStdOut:\n{rc.StdOut}\nStdErr:\n{rc.StdErr}");

                        if (rc.ExitCode != 0)
                        {
                            // try fallback without -ss before input (safer for some formats)
                            ffmpegArgs = $"-i \"{downloaded}\" -ss 3 -t 4 -c:v libvpx-vp9 -b:v 0 -crf 30 -c:a libopus -b:a 128k -y \"{clipPath}\"";
                            rc = await RunProcessAsync("ffmpeg", ffmpegArgs, tempRoot, cancellationToken);
                            AppendStatus(statusFile, $"[ffmpeg-clip-{i}-fallback] ExitCode={rc.ExitCode}\nStdOut:\n{rc.StdOut}\nStdErr:\n{rc.StdErr}");
                        }

                        // Write ffmpeg stdout/stderr to per-clip log files for debugging
                        try
                        {
                            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                            var outLog = Path.Combine(tempRoot, $"ffmpeg_clip{i}_{stamp}_out.log");
                            var errLog = Path.Combine(tempRoot, $"ffmpeg_clip{i}_{stamp}_err.log");
                            System.IO.File.WriteAllText(outLog, rc.StdOut ?? string.Empty);
                            System.IO.File.WriteAllText(errLog, rc.StdErr ?? string.Empty);
                        }
                        catch { /* best-effort logging */ }

                        if (rc.ExitCode == 0 && System.IO.File.Exists(clipPath))
                        {
                            clipFiles.Add(clipPath);
                        }
                    }
                }

                if (clipFiles.Count == 0)
                {
                    AppendStatus(statusFile, "[ERROR] Failed to create any clips.");
                    return StatusCode(500, "Failed to create any clips.");
                }

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

                // persist concat logs into status file
                AppendStatus(statusFile, $"[ffmpeg-concat] ExitCode={concatRc.ExitCode}\nStdOut:\n{concatRc.StdOut}\nStdErr:\n{concatRc.StdErr}");

                // Write concat ffmpeg logs
                try
                {
                    var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    var outLog = Path.Combine(tempRoot, $"ffmpeg_concat_{stamp}_out.log");
                    var errLog = Path.Combine(tempRoot, $"ffmpeg_concat_{stamp}_err.log");
                    System.IO.File.WriteAllText(outLog, concatRc.StdOut ?? string.Empty);
                    System.IO.File.WriteAllText(errLog, concatRc.StdErr ?? string.Empty);
                }
                catch { /*best-effort*/ }

                if (concatRc.ExitCode != 0 || !System.IO.File.Exists(finalPath))
                {
                    AppendStatus(statusFile, "[ERROR] Failed to concatenate clips into trailer.");
                    return StatusCode(500, "Failed to concatenate clips into trailer.");
                }

                AppendStatus(statusFile, $"[INFO] Trailer created successfully at {DateTime.UtcNow:O}");

                // 5) Stream the file back
                var fs = System.IO.File.OpenRead(finalPath);
                // Return FileStreamResult; letting ASP.NET Core handle range requests may be beneficial for large files
                return File(fs, "video/webm", "trailer.webm");
            }
            catch (OperationCanceledException)
            {
                AppendStatus(statusFile, "[WARN] Request cancelled.");
                return StatusCode(499, "Request cancelled.");
            }
            catch (Exception ex)
            {
                AppendStatus(statusFile, $"[ERROR] Unexpected error while creating trailer: {ex}");
                return StatusCode(500, $"Unexpected error while creating trailer: {ex.Message}");
            }
            finally
            {
                // cleanup only when requested. Default is false so files are preserved for debugging.
                if (cleanFiles)
                {
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
        }

        // New endpoint: read aggregated logs/status for a given id (GUID folder name)
        [HttpGet("logs/{id}")]
        public IActionResult GetLogs(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest("Missing id.");

            id = new string(id.Where(char.IsLetterOrDigit).ToArray()); // basic sanitation
            var dir = Path.Combine(Path.GetTempPath(), "ytt_trailer", id);
            if (!Directory.Exists(dir))
                return NotFound();

            var statusFile = Path.Combine(dir, "status.txt");
            if (System.IO.File.Exists(statusFile))
            {
                var text = System.IO.File.ReadAllText(statusFile);
                return Content(text, "text/plain");
            }

            // Fallback: concatenate any log files present
            var logs = Directory.GetFiles(dir, "*.log").OrderBy(f => f).Select(f =>
            {
                try { return System.IO.File.ReadAllText(f); } catch { return string.Empty; }
            });
            var combined = string.Join(Environment.NewLine + "----" + Environment.NewLine, logs);
            return Content(combined, "text/plain");
        }

        private async Task<string> DownloadWithYtDlpAsync(string url, string destPrefix, CancellationToken cancellationToken)
        {
            // Prefer best video up to 1080p and merge to webm when possible.
            // Format selection: bestvideo with height <= 1080 + bestaudio, fallback to best.
            // Also instruct yt-dlp to merge output format to webm (requires ffmpeg).
            var outputTemplate = destPrefix + ".%(ext)s";
            var format = "bestvideo[height<=1080]+bestaudio/best[height<=1080]/best";
            var args = $"-f \"{format}\" --merge-output-format webm -o \"{outputTemplate}\" \"{url}\"";

            var workDir = Path.GetDirectoryName(destPrefix);
            var res = await RunProcessAsync("yt-dlp", args, workDir, cancellationToken);
            if (res.ExitCode != 0)
            {
                // write logs for yt-dlp as well
                try
                {
                    var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    var outLog = Path.Combine(workDir ?? Path.GetTempPath(), $"yt-dlp_{stamp}_out.log");
                    var errLog = Path.Combine(workDir ?? Path.GetTempPath(), $"yt-dlp_{stamp}_err.log");
                    System.IO.File.WriteAllText(outLog, res.StdOut ?? string.Empty);
                    System.IO.File.WriteAllText(errLog, res.StdErr ?? string.Empty);
                }
                catch { }
                // append to central status file if present
                try
                {
                    var statusFile = Path.Combine(workDir ?? Path.GetTempPath(), "status.txt");
                    AppendStatus(statusFile, $"[yt-dlp] ExitCode={res.ExitCode}\nStdOut:\n{res.StdOut}\nStdErr:\n{res.StdErr}");
                }
                catch { }
                return null;
            }

            // append success output to central status
            try
            {
                var statusFile = Path.Combine(workDir ?? Path.GetTempPath(), "status.txt");
                AppendStatus(statusFile, $"[yt-dlp] ExitCode={res.ExitCode}\nStdOut:\n{res.StdOut}\nStdErr:\n{res.StdErr}");
            }
            catch { }

            // find created file
            var dir = workDir;
            var prefix = Path.GetFileName(destPrefix) + ".";
            var files = Directory.GetFiles(dir, prefix + "*").OrderBy(f => new FileInfo(f).Length).ToArray();
            // choose the first matching file (there should be at least one)
            return files.Length > 0 ? files[0] : null;
        }

        private record ProcessResult(int ExitCode, string StdOut, string StdErr);

        private async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken)
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
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (s, e) =>
            {
                try
                {
                    tcs.TrySetResult(proc.ExitCode);
                }
                catch { }
            };

            try
            {
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                using (cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!proc.HasExited) proc.Kill(true);
                    }
                    catch { }
                }))
                {
                    // Wait for process exit and output read completion
                    await Task.WhenAll(tcs.Task, stdoutTask, stderrTask);
                    var exitCode = tcs.Task.Status == TaskStatus.RanToCompletion ? tcs.Task.Result : proc.HasExited ? proc.ExitCode : -1;
                    var stdOut = stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty;
                    var stdErr = stderrTask.IsCompleted ? stderrTask.Result : string.Empty;

                    // persist logs to workingDirectory for debugging (best-effort)
                    try
                    {
                        var dir = workingDirectory ?? Environment.CurrentDirectory;
                        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                        var name = Path.GetFileNameWithoutExtension(fileName) ?? fileName;
                        var outLog = Path.Combine(dir, $"{name}_{stamp}_out.log");
                        var errLog = Path.Combine(dir, $"{name}_{stamp}_err.log");
                        System.IO.File.WriteAllText(outLog, stdOut);
                        System.IO.File.WriteAllText(errLog, stdErr);
                    }
                    catch { /* ignore logging errors */ }

                    return new ProcessResult(exitCode, stdOut, stdErr);
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

        private void AppendStatus(string statusFilePath, string text)
        {
            try
            {
                var entry = $"[{DateTime.UtcNow:O}] {text}{Environment.NewLine}";
                System.IO.File.AppendAllText(statusFilePath, entry);
            }
            catch
            {
                // best-effort; ignore any IO errors to not fail main flow
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
                var channelId = items[0].GetProperty("snippet").GetProperty("channelId").GetString();
                return channelId;
            }
            return null;
        }

        // Placeholder: existing helper used earlier (not shown in snippet). Keep existing implementation.
        private async Task<string[]> GetRandomVideoUrlsForChannelAsync(string channelId, int count, string apiKey)
        {
            // Very small, focused implementation to keep compatibility with existing behavior.
            // This method should return an array of video URLs for the channel.
            // For brevity, using the search.list endpoint for recent uploads and picking random ones.
            var uri = $"https://www.googleapis.com/youtube/v3/search?part=snippet&channelId={Uri.EscapeDataString(channelId)}&maxResults=50&type=video&key={apiKey}";
            var resp = await _httpClient.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            var list = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    var vid = item.GetProperty("id").GetProperty("videoId").GetString();
                    if (!string.IsNullOrEmpty(vid))
                        list.Add($"https://www.youtube.com/watch?v={vid}");
                }
                catch { }
            }

            var rnd = new Random();
            return list.OrderBy(x => rnd.Next()).Take(count).ToArray();
        }
    }
}
