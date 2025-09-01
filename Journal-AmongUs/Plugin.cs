using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using Reactor;
using System.IO;
using System.Reflection;
using BepInEx.Unity.IL2CPP;
using Reactor.Utilities.Attributes;
using System.Diagnostics;
using Reactor.Utilities.Extensions;

namespace SusJournal;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
[BepInProcess("Among Us.exe")]
[BepInDependency(ReactorPlugin.Id)]
public class SusJournalPlugin : BasePlugin
{
    public static readonly object StaticLock = new object();
    public static string StaticJsonGameState = "{}";

    public override void Load()
    {
        this.AddComponent<SusJournalManager>();
    }
}

[RegisterInIl2Cpp]
public class SusJournalManager : MonoBehaviour
{
    public static ManualLogSource Logger { get; private set; } = null!;

    private readonly Dictionary<byte, string> _playerNotes = new();
    private readonly HttpListener _httpListener = new HttpListener();

    private class PlayerState
    {
        public byte ID { get; init; }
        public string Name { get; init; } = null!;
        public Color32 Color { get; init; }
        public string ColorName { get; init; } = null!;
        public string? Note { get; init; }
    }

    private class TagData
    {
        public byte PlayerId { get; set; }
        public string Tag { get; set; } = "";
    }

    public SusJournalManager(IntPtr ptr) : base(ptr) { }

    private void Start()
    {
        Logger = BepInEx.Logging.Logger.CreateLogSource("SusJournal");

        var thread = new Thread(StartHttpServer);
        thread.IsBackground = true;
        thread.Start();

        Logger.LogInfo("SusJournal network client loaded and HTTP server started on http://localhost:8080. Open a browser to view the UI.");

        Process.Start(new ProcessStartInfo("http://localhost:8080") { UseShellExecute = true });
    }

    private void Update()
    { 
        UpdateGameState();
    }

    public void UpdateGameState()
    {
        if ((GameData.Instance == null || GameData.Instance.AllPlayers == null || GameData.Instance.AllPlayers.Count == 0) && _playerNotes.Count > 0)
        {
            _playerNotes.Clear();
        }

        if (GameData.Instance == null || GameData.Instance.AllPlayers == null || GameData.Instance.AllPlayers.Count == 0)
        {
            lock (SusJournalPlugin.StaticLock)
            {
                SusJournalPlugin.StaticJsonGameState = "{\"players\":[], \"roles\": []}";
            }
            return;
        }

        var players = new List<PlayerState>();
        var allPlayers = GameData.Instance.AllPlayers;

        // Create a list of available roles with their TeamType
        var availableRoles = new List<object>();
        if (RoleManager.Instance != null && RoleManager.Instance.AllRoles != null)
        {
            foreach (var role in RoleManager.Instance.AllRoles)
            {
                // Skip the "Strmiss" role as requested
                if (role.NiceName.Equals("Strmiss", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                availableRoles.Add(new { name = role.NiceName, teamType = role.TeamType });
            }
        }

        foreach (var playerInfo in allPlayers)
        {
            if (playerInfo == null || playerInfo.Disconnected)
            {
                continue;
            }

            string? note = null;
            if (_playerNotes.TryGetValue(playerInfo.PlayerId, out string? existingNote))
            {
                note = existingNote;
            }

            // The missing line of code that adds the player to the list
            players.Add(new PlayerState
            {
                ID = playerInfo.PlayerId,
                Name = playerInfo.PlayerName,
                Color = playerInfo.Color,
                ColorName = playerInfo.ColorName,
                Note = note,
            });
        }

        var sb = new StringBuilder();
        sb.Append("{\"players\":[");
        for (int i = 0; i < players.Count; i++)
        {
            PlayerState player = players[i];
            sb.Append("{\"id\":").Append(player.ID)
                .Append(",\"name\":\"").Append(EscapeJson(player.Name)).Append('"')
                .Append(",\"color\":\"").Append(player.Color.ToHtmlStringRGBA()).Append('"')
                .Append(",\"colorName\":\"").Append(EscapeJson(player.ColorName)).Append('"');
            if (player.Note != null)
            {
                sb.Append(",\"note\":\"").Append(EscapeJson(player.Note)).Append('"');
            }
            sb.Append('}');
            if (i < players.Count - 1)
            {
                sb.Append(',');
            }
        }
        sb.Append(']');

        // Add the roles with their team types to the JSON
        sb.Append(",\"roles\":[");
        for (int i = 0; i < availableRoles.Count; i++)
        {
            var role = (dynamic)availableRoles[i];
            sb.Append("{\"name\":\"").Append(EscapeJson(role.name)).Append("\", \"teamType\":").Append((int)role.teamType).Append("}");
            if (i < availableRoles.Count - 1)
            {
                sb.Append(',');
            }
        }
        sb.Append(']');

        sb.Append('}');

        lock (SusJournalPlugin.StaticLock)
        {
            SusJournalPlugin.StaticJsonGameState = sb.ToString();
        }
    }

    private void StartHttpServer()
    {
        try
        {
            _httpListener.Prefixes.Add("http://localhost:8080/");
            _httpListener.Start();

            while (_httpListener.IsListening)
            {
                var context = _httpListener.GetContext();
                Task.Run(() => HandleRequest(context));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"HTTP Listener failed: {ex}");
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        response.AppendHeader("Access-Control-Allow-Origin", "*");
        response.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.AppendHeader("Access-Control-Allow-Headers", "Content-Type");

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            response.Close();
            return;
        }

        if (request.Url?.AbsolutePath == "/")
        {
            response.ContentType = "text/html";
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "SusJournal.index.html";
            string htmlContent = "";

            using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using StreamReader reader = new StreamReader(stream);
                    htmlContent = reader.ReadToEnd();
                }
                else
                {
                    var availableResources = assembly.GetManifestResourceNames();
                    Logger.LogError($"Embedded resource '{resourceName}' not found.");
                    Logger.LogWarning("Available resources:");
                    foreach (var res in availableResources)
                    {
                        Logger.LogWarning($"- {res}");
                    }
                }
            }

            var buffer = Encoding.UTF8.GetBytes(htmlContent);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        else if (request.Url?.AbsolutePath == "/api/gamestate")
        {
            response.ContentType = "application/json";
            string json;
            lock (SusJournalPlugin.StaticLock)
            {
                json = SusJournalPlugin.StaticJsonGameState;
            }
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        else if (request.Url?.AbsolutePath == "/api/tagplayer" && request.HttpMethod == "POST")
        {
            try
            {
                string jsonBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    jsonBody = reader.ReadToEnd();
                }
                var data = DeserializeTagData(jsonBody);

                lock (SusJournalPlugin.StaticLock)
                {
                    if (data.Tag.Equals("Clear", StringComparison.OrdinalIgnoreCase))
                    {
                        _playerNotes.Remove(data.PlayerId);
                        Logger.LogInfo($"Player {data.PlayerId} note cleared.");
                    }
                    else
                    {
                        _playerNotes[data.PlayerId] = data.Tag;
                        Logger.LogInfo($"Player {data.PlayerId} tagged as {data.Tag}.");
                    }
                }
                UpdateGameState();
                response.StatusCode = 200;
                response.StatusDescription = "OK";
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to tag player: {ex}");
                response.StatusCode = 500;
                response.StatusDescription = "Internal Server Error";
            }
        }
        else if (request.Url?.AbsolutePath == "/Character.png")
        {
            response.ContentType = "image/png";
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "SusJournal.Character.png";
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                var buffer = new byte[stream.Length];
                var read = stream.Read(buffer, 0, buffer.Length);
                response.ContentLength64 = read;
                response.OutputStream.Write(buffer, 0, read);
            }
            else
            {
                Logger.LogError($"Embedded resource '{resourceName}' not found.");
                response.StatusCode = 404;
            }
        }
        else
        {
            response.StatusCode = 404;
        }
        response.Close();
    }

    private string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private TagData DeserializeTagData(string json)
    {
        var data = new TagData();
        var parts = json.Trim('{', '}').Split(',');
        foreach (var part in parts)
        {
            var kv = part.Split(':');
            if (kv.Length == 2)
            {
                string key = kv[0].Trim().Trim('"');
                string value = kv[1].Trim().Trim('"');
                if (key.Equals("playerId", StringComparison.OrdinalIgnoreCase))
                {
                    data.PlayerId = byte.Parse(value);
                }
                if (key.Equals("tag", StringComparison.OrdinalIgnoreCase))
                {
                    data.Tag = value;
                }
            }
        }
        return data;
    }
}