using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace GGXEnPatch;

/// <summary>
/// [Update] AutoUpdate: on launch, in the background, reads each channel's manifest (pack/publish.py)
/// and brings translations/ up to date: the channel's base pack when the manifest names another one,
/// then the update packs not here yet; update packs the manifest no longer lists are deleted. Files
/// are checked against the manifest's SHA-256 and written under a temporary name first, and the store
/// reloads them on its own. A channel whose local base is a dev pack (a local build) is left alone.
/// Only the manifest URLs are contacted; nothing about the player or the game is sent.
///
/// Manifest: {"format": 1, "channel": "main",
///   "base": {"id": "2026-10-25", "file": "translations.json", "url": "...json.gz", "sha256": "..."},
///   "updates": [{"id": "2026-11-01", "seq": 1, "file": "update-main-2026-11-01.json", "url": "...", "sha256": "..."}],
///   "plugin": {"version": "0.5.0", "url": "https://..."}}
/// sha256 is of the uncompressed JSON; a url ending in .gz is gunzipped.
/// </summary>
public static class Updater
{
    static readonly HttpClient Http = CreateClient();
    static bool pluginNoticeShown;  // one notice, though every channel's manifest names the plugin version

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GGXEnPatch/{Plugin.Version}");
        return http;
    }

    public static void Start(string dir, IEnumerable<string> manifestUrls)
    {
        var job = new Job
        {
            Dir = dir,
            Urls = manifestUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct().ToList(),
        };
        Task.Run(job.Run);  // a method group: an async lambda here fails to compile against the interop (CS0656)
    }

    /// <summary>Deletes a channel's downloaded packs (base and updates; not dev or player packs).</summary>
    public static void RemoveChannel(string dir, string channel)
    {
        foreach (var pack in TranslationStore.PackFiles(dir).Select(PackInfo.Read)
                     .Where(p => p.Channel == channel && (p.Kind == "base" || p.IsUpdate)))
        {
            File.Delete(pack.Path);
            Plugin.Logger.LogInfo($"Translations ({channel}): removed {Path.GetFileName(pack.Path)}.");
        }
    }

    sealed class Job
    {
        public string Dir;
        public List<string> Urls;

        public async Task Run()
        {
            foreach (var url in Urls)
            {
                try
                {
                    await Update(Dir, url);
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"Translation update from {url} failed: {e.Message}");
                }
            }
        }
    }

    static async Task Update(string dir, string url)
    {
        using var manifest = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = manifest.RootElement;
        var channel = root.GetProperty("channel").GetString();
        var local = TranslationStore.PackFiles(dir).Select(PackInfo.Read).Where(p => p.Channel == channel).ToList();
        // The old headerless translations.json counts as a base to replace; a player's own packs are never touched.
        var localBase = local.FirstOrDefault(p => !p.IsUpdate && !p.IsOwn);
        if (localBase?.Kind == "dev")
        {
            Plugin.Logger.LogInfo($"Translations ({channel}): local dev pack, not updating.");
            return;
        }

        var changed = new List<string>();
        var remoteBase = root.GetProperty("base");
        var baseId = remoteBase.GetProperty("id").GetString();
        var baseFile = SafeName(remoteBase.GetProperty("file").GetString());
        if (localBase == null || localBase.Id != baseId)
        {
            await Download(remoteBase, Path.Combine(dir, baseFile));
            if (localBase != null && !Path.GetFileName(localBase.Path).Equals(baseFile, StringComparison.OrdinalIgnoreCase))
                File.Delete(localBase.Path);
            changed.Add($"base {baseId}");
        }

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var update in root.GetProperty("updates").EnumerateArray())
        {
            var file = SafeName(update.GetProperty("file").GetString());
            wanted.Add(file);
            var id = update.GetProperty("id").GetString();
            var have = local.FirstOrDefault(p => p.IsUpdate && p.Id == id && p.Base == baseId);
            if (have != null && Path.GetFileName(have.Path).Equals(file, StringComparison.OrdinalIgnoreCase)) continue;
            await Download(update, Path.Combine(dir, file));
            changed.Add($"update {id}");
        }
        foreach (var stale in local.Where(p => p.IsUpdate && !wanted.Contains(Path.GetFileName(p.Path))))
            File.Delete(stale.Path);

        Plugin.Logger.LogInfo(changed.Count == 0
            ? $"Translations ({channel}): up to date (base {baseId})."
            : $"Translations ({channel}): installed {string.Join(", ", changed)}. Story text updates now; " +
              "menus and game data text fully update after a restart.");

        if (!pluginNoticeShown && root.TryGetProperty("plugin", out var plugin)
            && Version.TryParse(plugin.GetProperty("version").GetString(), out var latest)
            && Version.TryParse(Plugin.Version, out var mine) && latest > mine)
        {
            pluginNoticeShown = true;
            Plugin.Logger.LogWarning($"GGX EN Patch {latest} is out (this is {mine}): " +
                                     (plugin.TryGetProperty("url", out var page) ? page.GetString() : "see the releases page"));
        }
    }

    /// <summary>Fetches entry "url" into path, checked against entry "sha256"; replaces path only when valid.</summary>
    static async Task Download(JsonElement entry, string path)
    {
        var url = entry.GetProperty("url").GetString();
        var bytes = await Http.GetByteArrayAsync(url);
        if (url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var output = new MemoryStream();
            await input.CopyToAsync(output);
            bytes = output.ToArray();
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(path)}: checksum mismatch, not installed");
        var part = path + ".part";
        await File.WriteAllBytesAsync(part, bytes);
        File.Move(part, path, overwrite: true);
    }

    /// <summary>A manifest names files, never paths.</summary>
    static string SafeName(string file)
    {
        var name = Path.GetFileName(file ?? "");
        if (name.Length == 0 || name != file || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"bad file name in manifest: {file}");
        return name;
    }
}
