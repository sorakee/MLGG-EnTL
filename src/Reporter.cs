using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Best.HTTP;
using Best.HTTP.Shared;
using HarmonyLib;

namespace GGXEnPatch;

/// <summary>
/// [Report]: helps the translators, in the background, through the patch's report service (ReportUrl).
///
/// ShareScenes: the game downloads a story scene from its own storage by a signed link. When that scene is
/// on the service's wanted list (scenes the translators have no copy of), its id and link are sent, and the
/// service downloads the scene itself. Nothing else about the request, the player or the account is sent.
///
/// SendUntranslatedUi: the rows UiText writes to ui-untranslated.tsv (menu text with no English yet, already
/// stripped of names, chat, news and story) are sent in small batches; ui-untranslated.sent remembers which.
///
/// On launch the plugin reads the service's config: the wanted list, and a switch per kind that lets the
/// service stop all sending without a plugin release. Failures are quiet (a warning in the BepInEx log).
/// </summary>
public static class Reporter
{
    static readonly HttpClient Http = CreateClient();
    // Since the 2026-10-27 build the path has a language folder (ja/); only Japanese scenes are wanted.
    static readonly Regex ScenePath = new(@"^/production-private/(?:ja/)?master-data/scenes/(\d{8})\.bin$");
    const string SceneHost = "assets.muvluv-girls-garden.com";
    const int UiBatch = 200;
    static readonly TimeSpan UiEvery = TimeSpan.FromMinutes(2);

    static string baseUrl;
    static string sentPath;
    static volatile bool scenesOn, uiOn, configRead;
    static HashSet<string> wanted = new();
    static readonly object Gate = new();
    static readonly Dictionary<string, string> seenScenes = new();  // id -> link, seen before the config came
    static readonly HashSet<string> sentScenes = new();
    static readonly ConcurrentQueue<string[]> uiQueue = new();
    static HashSet<string> sentUi;

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GGXEnPatch/{Plugin.Version}");
        return http;
    }

    public static void Start(string dir, string url)
    {
        baseUrl = url.TrimEnd('/');
        sentPath = Path.Combine(dir, "ui-untranslated.sent");
        Task.Run(Run);  // a method group: an async lambda here fails to compile against the interop (CS0656)
    }

    static async Task Run()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(baseUrl + "/v1/config"));
            var root = doc.RootElement;
            var ids = root.TryGetProperty("wanted", out var w) ? w.EnumerateArray().Select(e => e.GetString()).ToHashSet() : new();
            List<KeyValuePair<string, string>> early;
            lock (Gate)
            {
                wanted = ids;
                scenesOn = Plugin.ShareScenes.Value && root.TryGetProperty("scenes", out var s) && s.GetBoolean();
                uiOn = Plugin.SendUntranslatedUi.Value && root.TryGetProperty("ui", out var u) && u.GetBoolean();
                configRead = true;
                early = seenScenes.ToList();
                seenScenes.Clear();
            }
            foreach (var (id, link) in early) Scene(id, link);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Report service unreachable ({e.Message}); nothing is sent this session.");
            return;
        }
        var wait = TimeSpan.FromSeconds(30);  // first batch soon: rows kept from earlier sessions, short sessions
        while (uiOn)
        {
            await Task.Delay(wait);
            wait = UiEvery;
            try
            {
                await SendUi();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Sending untranslated text failed: {e.Message}");
            }
        }
    }

    /// <summary>A scene link the game requested: send it if the translators want that scene.</summary>
    static void Scene(string id, string link)
    {
        lock (Gate)
        {
            if (!configRead)
            {
                if (seenScenes.Count < 50) seenScenes[id] = link;
                return;
            }
            if (!scenesOn || !wanted.Contains(id) || !sentScenes.Add(id)) return;
        }
        Task.Run(new ScenePost { Id = id, Link = link }.Run);  // lambdas here fail to compile (CS0656)
    }

    sealed class ScenePost
    {
        public string Id, Link;
        public Task Run() => PostScene(Id, Link);
    }

    static async Task PostScene(string id, string link)
    {
        try
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = id, ["url"] = link });
            using var res = await Http.PostAsync(baseUrl + "/v1/scene", new StringContent(body, Encoding.UTF8, "application/json"));
            if (res.IsSuccessStatusCode) Plugin.Logger.LogInfo($"Shared scene {id} with the translators. Thank you!");
            else Plugin.Logger.LogWarning($"Sharing scene {id}: the report service said {(int)res.StatusCode}.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Sharing scene {id} failed: {e.Message}");
        }
    }

    /// <summary>A ui-untranslated.tsv row (escaped text, label path), new or kept from earlier: queued unless
    /// sent before. Main thread.</summary>
    internal static void QueueUi(string escaped, string path)
    {
        if (!Plugin.SendUntranslatedUi.Value || sentPath == null) return;
        try
        {
            sentUi ??= File.Exists(sentPath) ? File.ReadAllLines(sentPath).ToHashSet() : new HashSet<string>();
            if (!sentUi.Contains(escaped)) uiQueue.Enqueue(new[] { escaped, path });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"ui-untranslated.sent: {e.Message}");
        }
    }

    /// <summary>Forget sent rows that are no longer in the log (translated by now). Main thread.</summary>
    internal static void KeepSent(ICollection<string> logged)
    {
        try
        {
            if (sentPath == null || !File.Exists(sentPath)) return;
            lock (Gate)
            {
                var sent = File.ReadAllLines(sentPath);
                var keep = sent.Where(logged.Contains).ToArray();
                if (keep.Length < sent.Length) File.WriteAllLines(sentPath, keep, new UTF8Encoding(false));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"ui-untranslated.sent: {e.Message}");
        }
    }

    static async Task SendUi()
    {
        var rows = new List<string[]>();
        while (rows.Count < UiBatch && uiQueue.TryDequeue(out var row)) rows.Add(row);
        if (rows.Count == 0) return;
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["plugin"] = Plugin.Version, ["rows"] = rows });
        using var res = await Http.PostAsync(baseUrl + "/v1/ui", new StringContent(body, Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode)
        {
            if ((int)res.StatusCode == 403) uiOn = false;  // switched off on the service
            throw new HttpRequestException($"the report service said {(int)res.StatusCode}");
        }
        lock (Gate) File.AppendAllLines(sentPath, rows.Select(r => r[0]), new UTF8Encoding(false));
    }

    [HarmonyPatch(typeof(HTTPManager), "SendRequest")]
    [HarmonyPrefix]
    static void BeforeSend(HTTPRequest request)
    {
        try
        {
            var uri = request?.CurrentUri ?? request?.Uri;
            if (uri == null || !uri.Host.Equals(SceneHost, StringComparison.OrdinalIgnoreCase)) return;
            var m = ScenePath.Match(uri.AbsolutePath);
            if (m.Success && uri.Query.Contains("sig=")) Scene(m.Groups[1].Value, uri.AbsoluteUri);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }
}
