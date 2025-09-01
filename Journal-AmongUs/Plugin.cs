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
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2CppInterop.Runtime;

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
        [JsonPropertyName("id")]
        public byte ID { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = null!;

        [JsonPropertyName("color")]
        public string Color { get; init; } = null!;

        [JsonPropertyName("colorName")]
        public string ColorName { get; init; } = null!;

        [JsonPropertyName("note")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; init; }
    }

    private class RoleState
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = null!;
        [JsonPropertyName("teamType")]
        public int TeamType { get; init; }
    }

    private class GameState
    {
        [JsonPropertyName("players")]
        public List<PlayerState> Players { get; init; } = new();

        [JsonPropertyName("roles")]
        public List<RoleState> Roles { get; init; } = new();
    }

    private class TagData
    {
        [JsonPropertyName("playerId")]
        public byte PlayerId { get; set; }
        [JsonPropertyName("tag")]
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

    private void FixedUpdate()
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

        foreach (var playerInfo in allPlayers)
        {
            if (playerInfo == null || playerInfo.Disconnected || playerInfo.IsIncomplete)
            {
                continue;
            }

            string? note = null;
            if (_playerNotes.TryGetValue(playerInfo.PlayerId, out string? existingNote))
            {
                note = existingNote;
            }

            players.Add(new PlayerState
            {
                ID = playerInfo.PlayerId,
                Name = playerInfo.PlayerName,
                Color = playerInfo.Color.ToHtmlStringRGBA(),
                ColorName = playerInfo.ColorName,
                Note = note,
            });
        }

        var roles = new List<RoleState>();
        if (RoleManager.Instance != null && RoleManager.Instance.AllRoles != null)
        {
            foreach (var role in RoleManager.Instance.AllRoles)
            {
                if (role.NiceName.Equals("Strmiss", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                roles.Add(new RoleState
                {
                    Name = role.NiceName,
                    TeamType = (int)role.TeamType
                });
            }
        }

        var gameState = new GameState
        {
            Players = players,
            Roles = roles
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        string json = JsonSerializer.Serialize(gameState, jsonOptions);

        lock (SusJournalPlugin.StaticLock)
        {
            SusJournalPlugin.StaticJsonGameState = json;
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


    private TagData DeserializeTagData(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TagData>(json) ?? throw new Exception("Deserialized object is null");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to deserialize JSON: {ex}");
            throw;
        }
    }
}