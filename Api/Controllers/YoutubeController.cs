using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

        // Android Chrome High-Definition Mobile Spec:
        // 1080p 30fps CFR, H.264 Main Profile L4.1, CRF 18 (visually lossless), GOP 60 (2s keyframes), AAC 192k 48kHz, +faststart
        private const int TargetWidth = 1920;
        private const int TargetHeight = 1080;
        private const int TargetFps = 30;
        private const int GopSize = 60; // Exact keyframe every 2.0s at 30fps
        private const string VideoProfile = "main";
        private const string VideoLevel = "4.1";
        private const string AudioRate = "48000";
        private const string AudioChannels = "2";
        private const string AudioBitrate = "192k";
        private const string VideoMaxrate = "6000k";
        private const string VideoBufsize = "12000k";

        // 15 seconds total trailer: 5 clips x 3 seconds each
        private const int ClipDurationSeconds = 3;

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
                if (Uri.TryCreate("https://" + url, UriKind.Absolute, out uri) == false)
                {
                    return BadRequest("Invalid URL format.");
                }
            }

            string channelId = null;

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0].Equals("channel", StringComparison.OrdinalIgnoreCase))
            {
                channelId = segments[1];
            }

            if (string.IsNullOrEmpty(channelId) && segments.Length >= 2 && segments[0].Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                var username = segments[1];
                channelId = await ResolveChannelIdByUsernameAsync(username, apiKey);
            }

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

            if (string.IsNullOrEmpty(channelId))
            {
                string videoId = null;
                if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase))
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

            try
            {
                // Returns 5 random videos to make a 15s trailer (5 clips x 3s)
                var videos = await GetRandomVideoUrlsForChannelAsync(channelId, 5, apiKey);
                return Ok(new { videos });
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

        [HttpPost("trailer")]
        public async Task<IActionResult> CreateTrailer([FromBody] string[] urls, [FromQuery] bool cleanFiles = false, [FromQuery] string id = null, CancellationToken cancellationToken = default)
        {
            if (urls == null || urls.Length == 0)
                return BadRequest("Provide an array of video URLs.");

            // Take up to 5 videos for a 15-second trailer (5 clips x 3s each)
            var maxVideos = 5;
            var list = urls.Take(maxVideos).ToArray();

            if (string.IsNullOrWhiteSpace(id))
                id = Guid.NewGuid().ToString("N");
            else
                id = new string(id.Where(char.IsLetterOrDigit).ToArray());

            var tempRoot = Path.Combine(Path.GetTempPath(), "ytt_trailer", id);
            Directory.CreateDirectory(tempRoot);

            var statusFile = Path.Combine(tempRoot, "status.txt");
            var clipFiles = new List<string>();

            try
            {
                AppendStatus(statusFile, $"[INFO] Starting 15s mobile trailer creation for id={id} ({list.Length} videos requested) at {DateTime.UtcNow:O}");

                // 1) Fast download and cut 3s clip from each video
                for (var i = 0; i < list.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = list[i];
                    var prefix = Path.Combine(tempRoot, $"downloaded{i}");

                    var downloaded = await DownloadWithYtDlpAsync(url, prefix, cancellationToken);
                    if (downloaded == null || !System.IO.File.Exists(downloaded))
                    {
                        AppendStatus(statusFile, $"[WARN] Skipping video {i} ({url}) - download failed.");
                        continue;
                    }

                    var clipPath = Path.Combine(tempRoot, $"clip{i}.mp4");
                    var clipOk = await EncodeSingleClipAsync(downloaded, clipPath, tempRoot, statusFile, i, cancellationToken);

                    if (clipOk && System.IO.File.Exists(clipPath))
                    {
                        clipFiles.Add(clipPath);
                    }
                    else
                    {
                        AppendStatus(statusFile, $"[WARN] Skipping clip {i} - encoding failed.");
                    }
                }

                if (clipFiles.Count == 0)
                {
                    AppendStatus(statusFile, "[ERROR] Failed to create any valid clips.");
                    return StatusCode(500, "Failed to create any valid clips.");
                }

                AppendStatus(statusFile, $"[INFO] Prepared {clipFiles.Count} / {list.Length} clips ({clipFiles.Count * ClipDurationSeconds}s total duration).");

                var finalPath = Path.Combine(tempRoot, "trailer.mp4");

                // 2) Fast filter_complex concatenation
                var concatOk = await ConcatClipsAsync(clipFiles, finalPath, tempRoot, statusFile, cancellationToken);

                if (!concatOk || !System.IO.File.Exists(finalPath))
                {
                    AppendStatus(statusFile, "[ERROR] Failed to concatenate clips into final trailer.");
                    return StatusCode(500, "Failed to concatenate clips into trailer.");
                }

                AppendStatus(statusFile, $"[INFO] 15s Trailer created successfully ({clipFiles.Count} clips included) at {DateTime.UtcNow:O}");

                var fs = System.IO.File.OpenRead(finalPath);
                return File(fs, "video/mp4", "trailer.mp4", enableRangeProcessing: true);
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
                if (cleanFiles)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (Directory.Exists(tempRoot))
                                Directory.Delete(tempRoot, true);
                        }
                        catch { }
                    });
                }
            }
        }

        [HttpGet("logs/{id}")]
        public IActionResult GetLogs(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest("Missing id.");

            id = new string(id.Where(char.IsLetterOrDigit).ToArray());
            var dir = Path.Combine(Path.GetTempPath(), "ytt_trailer", id);
            if (!Directory.Exists(dir))
                return NotFound();

            var statusFile = Path.Combine(dir, "status.txt");
            if (System.IO.File.Exists(statusFile))
            {
                var text = System.IO.File.ReadAllText(statusFile);
                return Content(text, "text/plain");
            }

            var logs = Directory.GetFiles(dir, "*.log").OrderBy(f => f).Select(f =>
            {
                try { return System.IO.File.ReadAllText(f); } catch { return string.Empty; }
            });
            var combined = string.Join(Environment.NewLine + "----" + Environment.NewLine, logs);
            return Content(combined, "text/plain");
        }

        // ---------------------------------------------------------------
        // High-Quality Clip Encoding (3s per clip, H.264 Main L4.1 1080p CFR 30fps)
        // ---------------------------------------------------------------

        private async Task<bool> EncodeSingleClipAsync(string sourcePath, string clipPath, string workDir, string statusFile, int clipIndex, CancellationToken cancellationToken)
        {
            var vf = $"scale={TargetWidth}:{TargetHeight}:flags=lanczos:force_original_aspect_ratio=decrease,pad={TargetWidth}:{TargetHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={TargetFps},setpts=PTS-STARTPTS";
            var af = $"aresample={AudioRate}:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS";

            var commonEncFlags =
                $"-fps_mode cfr -r {TargetFps} -c:v libx264 -profile:v {VideoProfile} -level {VideoLevel} -preset medium -crf 18 " +
                $"-maxrate {VideoMaxrate} -bufsize {VideoBufsize} -g {GopSize} -keyint_min {GopSize} -flags +cgop -sc_threshold 0 -pix_fmt yuv420p " +
                $"-c:a aac -b:a {AudioBitrate} -ar {AudioRate} -ac {AudioChannels} -movflags +faststart -y \"{clipPath}\"";

            // Try 1: Cut 3s clip starting at 3s with video + audio
            var args1 = $"-ss 3 -t {ClipDurationSeconds} -i \"{sourcePath}\" -vf \"{vf}\" -af \"{af}\" {commonEncFlags}";
            var rc = await RunProcessAsync("ffmpeg", args1, workDir, cancellationToken);
            AppendStatus(statusFile, $"[ffmpeg-clip-{clipIndex}-try1] ExitCode={rc.ExitCode}");

            if (rc.ExitCode == 0 && System.IO.File.Exists(clipPath) && new FileInfo(clipPath).Length > 1000)
                return true;

            // Try 2: Video-only + generated silent audio fallback (for videos lacking audio tracks)
            var args2 = $"-ss 3 -t {ClipDurationSeconds} -i \"{sourcePath}\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate={AudioRate} " +
                        $"-filter_complex \"[0:v]{vf}[v];[1:a]{af}[a]\" -map \"[v]\" -map \"[a]\" -shortest {commonEncFlags}";
            rc = await RunProcessAsync("ffmpeg", args2, workDir, cancellationToken);
            AppendStatus(statusFile, $"[ffmpeg-clip-{clipIndex}-silent-fallback] ExitCode={rc.ExitCode}");

            return rc.ExitCode == 0 && System.IO.File.Exists(clipPath) && new FileInfo(clipPath).Length > 1000;
        }

        private async Task<bool> ConcatClipsAsync(List<string> clipFiles, string finalPath, string workDir, string statusFile, CancellationToken cancellationToken)
        {
            var inputsSb = new StringBuilder();
            for (var i = 0; i < clipFiles.Count; i++)
            {
                inputsSb.Append($"-i \"{clipFiles[i]}\" ");
            }

            var filterSb = new StringBuilder();
            for (var i = 0; i < clipFiles.Count; i++)
            {
                filterSb.Append($"[{i}:v][{i}:a]");
            }
            filterSb.Append($"concat=n={clipFiles.Count}:v=1:a=1[vraw][araw];");
            filterSb.Append($"[vraw]fps={TargetFps},format=yuv420p,setpts=PTS-STARTPTS[outv];");
            filterSb.Append($"[araw]aresample={AudioRate}:async=1,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS[outa]");

            var args =
                $"{inputsSb}-filter_complex \"{filterSb}\" -map \"[outv]\" -map \"[outa]\" " +
                $"-fps_mode cfr -r {TargetFps} -pix_fmt yuv420p " +
                $"-c:v libx264 -profile:v {VideoProfile} -level {VideoLevel} -preset medium -crf 18 " +
                $"-maxrate {VideoMaxrate} -bufsize {VideoBufsize} -g {GopSize} -keyint_min {GopSize} -flags +cgop -sc_threshold 0 " +
                $"-c:a aac -b:a {AudioBitrate} -ar {AudioRate} -ac {AudioChannels} -movflags +faststart -y \"{finalPath}\"";

            var rc = await RunProcessAsync("ffmpeg", args, workDir, cancellationToken);
            AppendStatus(statusFile, $"[ffmpeg-concat] ExitCode={rc.ExitCode}");

            return rc.ExitCode == 0 && System.IO.File.Exists(finalPath);
        }

        // ---------------------------------------------------------------
        // Robust Download Helper (with multi-client fallback for HTTP 403 prevention)
        // ---------------------------------------------------------------

        private async Task<string> DownloadWithYtDlpAsync(string url, string destPrefix, CancellationToken cancellationToken)
        {
            var outputTemplate = destPrefix + ".%(ext)s";
            var extractorArgs = "--extractor-args \"youtube:player_client=android,web,mweb\"";

            // Tier 1: Download 1080p split streams or best combined format
            var format1 = "bestvideo[height<=1080]+bestaudio/bestvideo+bestaudio/b[height<=1080]/18/22/best";
            var args1 = $"{extractorArgs} -f \"{format1}\" --merge-output-format mp4 --no-playlist -o \"{outputTemplate}\" \"{url}\"";

            var workDir = Path.GetDirectoryName(destPrefix);
            var ytDlpPath = FindExecutablePath("yt-dlp");
            var denoPath = default(string);

            try
            {
                if (!string.IsNullOrEmpty(ytDlpPath))
                {
                    var ytDir = Path.GetDirectoryName(ytDlpPath);
                    if (!string.IsNullOrEmpty(ytDir))
                    {
                        var candidateExe = Path.Combine(ytDir, "deno.exe");
                        var candidateNoExt = Path.Combine(ytDir, "deno");
                        if (System.IO.File.Exists(candidateExe))
                            denoPath = candidateExe;
                        else if (System.IO.File.Exists(candidateNoExt))
                            denoPath = candidateNoExt;
                    }
                }

                if (string.IsNullOrEmpty(denoPath))
                {
                    denoPath = FindExecutablePath("deno");
                }

                if (!string.IsNullOrEmpty(denoPath))
                {
                    args1 = $"--js-runtimes deno:\"{denoPath}\" {args1}";
                }
            }
            catch { }

            var fileNameToRun = !string.IsNullOrEmpty(ytDlpPath) ? ytDlpPath : "yt-dlp";
            var res = await RunProcessAsync(fileNameToRun, args1, workDir, cancellationToken);

            // Tier 2: Universal fallback format if primary format fails
            if (res.ExitCode != 0)
            {
                var jsOpt = !string.IsNullOrEmpty(denoPath) ? $"--js-runtimes deno:\"{denoPath}\" " : "";
                var fallbackArgs = $"{jsOpt}{extractorArgs} -f \"best\" --no-playlist -o \"{outputTemplate}\" \"{url}\"";
                res = await RunProcessAsync(fileNameToRun, fallbackArgs, workDir, cancellationToken);
            }

            // Tier 3: Absolute fallback
            if (res.ExitCode != 0)
            {
                var jsOpt = !string.IsNullOrEmpty(denoPath) ? $"--js-runtimes deno:\"{denoPath}\" " : "";
                var fallbackArgs2 = $"{jsOpt}-f \"worstvideo+worstaudio/worst\" --no-playlist -o \"{outputTemplate}\" \"{url}\"";
                res = await RunProcessAsync(fileNameToRun, fallbackArgs2, workDir, cancellationToken);
            }

            if (res.ExitCode != 0)
            {
                return null;
            }

            var dir = workDir ?? Environment.CurrentDirectory;
            var prefix = Path.GetFileName(destPrefix) + ".";
            var files = Directory.GetFiles(dir, prefix + "*")
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToArray();

            if (files.Length == 0)
                return null;

            return files[0];
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
                try { tcs.TrySetResult(proc.ExitCode); } catch { }
            };

            try
            {
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                using (cancellationToken.Register(() =>
                {
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                }))
                {
                    await Task.WhenAll(tcs.Task, stdoutTask, stderrTask);
                    var exitCode = tcs.Task.Status == TaskStatus.RanToCompletion ? tcs.Task.Result : proc.HasExited ? proc.ExitCode : -1;
                    var stdOut = stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty;
                    var stdErr = stderrTask.IsCompleted ? stderrTask.Result : string.Empty;

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
                    catch { }

                    return new ProcessResult(exitCode, stdOut, stdErr);
                }
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
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
            catch { }
        }

        private async Task<string> ResolveChannelIdByUsernameAsync(string username, string apiKey)
        {
            var uri = $"https://www.googleapis.com/youtube/v3/channels?part=id&forUsername={Uri.EscapeDataString(username)}&key={apiKey}";
            var resp = await _httpClient.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() > 0)
                return items[0].GetProperty("id").GetString();
            return null;
        }

        private async Task<string> ResolveChannelIdByQueryAsync(string query, string apiKey)
        {
            var uri = $"https://www.googleapis.com/youtube/v3/search?part=snippet&type=channel&maxResults=1&q={Uri.EscapeDataString(query)}&key={apiKey}";
            var resp = await _httpClient.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() > 0)
                return items[0].GetProperty("snippet").GetProperty("channelId").GetString();
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
                return items[0].GetProperty("snippet").GetProperty("channelId").GetString();
            return null;
        }

        private async Task<string[]> GetRandomVideoUrlsForChannelAsync(string channelId, int count, string apiKey)
        {
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

        private string FindExecutablePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var requested = name;

            if (Environment.OSVersion.Platform == PlatformID.Win32NT &&
                (requested.Equals("deno", StringComparison.OrdinalIgnoreCase) || requested.Equals("deno.exe", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(userProfile))
                    {
                        var candidate = Path.Combine(userProfile, ".deno", "bin", "deno.exe");
                        if (System.IO.File.Exists(candidate))
                            return candidate;
                    }
                }
                catch { }
            }

            if (Path.IsPathRooted(requested) && System.IO.File.Exists(requested))
                return requested;

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            var pathext = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
                : new[] { string.Empty };

            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                foreach (var ext in pathext)
                {
                    var candidate = Path.Combine(dir, requested + ext);
                    try { if (System.IO.File.Exists(candidate)) return candidate; } catch { }
                }

                var candNoExt = Path.Combine(dir, requested);
                try { if (System.IO.File.Exists(candNoExt)) return candNoExt; } catch { }
            }

            foreach (var ext in pathext)
            {
                var cur = Path.Combine(Environment.CurrentDirectory, requested + ext);
                try { if (System.IO.File.Exists(cur)) return cur; } catch { }
            }

            var curNoExt = Path.Combine(Environment.CurrentDirectory, requested);
            try { if (System.IO.File.Exists(curNoExt)) return curNoExt; } catch { }

            return null;
        }
    }
}