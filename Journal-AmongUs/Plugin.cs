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

namespace SusJournal;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
[BepInProcess("Among Us.exe")]
[BepInDependency(ReactorPlugin.Id)]
public class SusJournalPlugin : BasePlugin
{
    public static object staticLock = new object();
    public static string staticJsonGameState = "{}";

    public override void Load()
    {
        this.AddComponent<SusJournalManager>();
    }
}

[RegisterInIl2Cpp]
public class SusJournalManager : MonoBehaviour
{
    public static ManualLogSource Logger;

    private readonly Dictionary<byte, string> playerNotes = new();
    private readonly HttpListener _httpListener = new HttpListener();

    private class PlayerState
    {
        public byte id { get; set; }
        public string name { get; set; }
        public Color32 color { get; set; }
        public string note { get; set; }

        public int colorId { get; set; }
    }

    private class TagData
    {
        public byte playerId { get; set; }
        public string tag { get; set; }
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
        if ((GameData.Instance == null || GameData.Instance.AllPlayers == null || GameData.Instance.AllPlayers.Count == 0) && playerNotes.Count > 0)
        {
            playerNotes.Clear();
        }

        if (GameData.Instance == null || GameData.Instance.AllPlayers == null || GameData.Instance.AllPlayers.Count == 0)
        {
            lock (SusJournalPlugin.staticLock)
            {
                SusJournalPlugin.staticJsonGameState = "{\"players\":[], \"roles\": []}";
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

        foreach (var playerControl in allPlayers)
        {
            if (playerControl == null || playerControl.Disconnected)
            {
                continue;
            }

            string note = null;
            if (playerNotes.TryGetValue(playerControl.PlayerId, out string existingNote))
            {
                note = existingNote;
            }

            // The missing line of code that adds the player to the list
            players.Add(new PlayerState
            {
                id = playerControl.PlayerId,
                name = playerControl.PlayerName,
                color = (Color32)playerControl.Color,
                note = note,
                colorId = playerControl.DefaultOutfit.ColorId
            });
        }

        var sb = new StringBuilder();
        sb.Append("{\"players\":[");
        for (int i = 0; i < players.Count; i++)
        {
            PlayerState player = players[i];
            sb.Append("{\"id\":").Append(player.id)
                .Append(",\"name\":\"").Append(EscapeJson(player.name)).Append("\"")
                .Append(",\"color\":{\"r\":").Append(player.color.r).Append(",\"g\":").Append(player.color.g)
                .Append(",\"b\":").Append(player.color.b).Append("}")
                .Append(",\"colorId\":").Append(player.colorId);
            if (player.note != null)
            {
                sb.Append(",\"note\":\"").Append(EscapeJson(player.note)).Append("\"");
            }
            sb.Append("}");
            if (i < players.Count - 1)
            {
                sb.Append(",");
            }
        }
        sb.Append("]");

        // Add the roles with their team types to the JSON
        sb.Append(",\"roles\":[");
        for (int i = 0; i < availableRoles.Count; i++)
        {
            var role = (dynamic)availableRoles[i];
            sb.Append("{\"name\":\"").Append(EscapeJson(role.name)).Append("\", \"teamType\":").Append((int)role.teamType).Append("}");
            if (i < availableRoles.Count - 1)
            {
                sb.Append(",");
            }
        }
        sb.Append("]");

        sb.Append("}");

        lock (SusJournalPlugin.staticLock)
        {
            SusJournalPlugin.staticJsonGameState = sb.ToString();
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

        if (request.Url.AbsolutePath == "/")
        {
            response.ContentType = "text/html";
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Journal_AmongUs.index.html";
            string htmlContent = "";

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        htmlContent = reader.ReadToEnd();
                    }
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
        else if (request.Url.AbsolutePath == "/api/gamestate")
        {
            response.ContentType = "application/json";
            string json;
            lock (SusJournalPlugin.staticLock)
            {
                json = SusJournalPlugin.staticJsonGameState;
            }
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        else if (request.Url.AbsolutePath == "/api/tagplayer" && request.HttpMethod == "POST")
        {
            try
            {
                string jsonBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    jsonBody = reader.ReadToEnd();
                }
                var data = DeserializeTagData(jsonBody);

                lock (SusJournalPlugin.staticLock)
                {
                    if (data.tag.Equals("Clear", StringComparison.OrdinalIgnoreCase))
                    {
                        playerNotes.Remove(data.playerId);
                        Logger.LogInfo($"Player {data.playerId} note cleared.");
                    }
                    else
                    {
                        playerNotes[data.playerId] = data.tag;
                        Logger.LogInfo($"Player {data.playerId} tagged as {data.tag}.");
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
        else if (request.Url.AbsolutePath == "/Character.png")
        {
            response.ContentType = "image/png";
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Journal_AmongUs.Character.png";
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    var buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
                else
                {
                    Logger.LogError($"Embedded resource '{resourceName}' not found.");
                    response.StatusCode = 404;
                }
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
                    data.playerId = byte.Parse(value);
                }
                if (key.Equals("tag", StringComparison.OrdinalIgnoreCase))
                {
                    data.tag = value;
                }
            }
        }
        return data;
    }
}