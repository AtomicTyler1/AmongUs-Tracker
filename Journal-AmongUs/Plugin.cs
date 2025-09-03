using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using System.IO;
using System.Reflection;
using BepInEx.Unity.IL2CPP;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmongUs.Data.Settings;
using AmongUs.AnimationTestScene;

namespace SusJournal;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
[BepInProcess("Among Us.exe")]
public class SusJournalPlugin : BasePlugin
{
    public override void Load()
    {
        AddComponent<SusJournalManager>();
    }
}

public class SusJournalManager : MonoBehaviour
{
    public static ManualLogSource Logger { get; private set; } = null!;

    private static object StaticLock { get; } = new();

    private static string _gameStateJson = "";

    private readonly Dictionary<byte, string> _playerNotes = new();
    private readonly HttpListener _httpListener = new();

    private byte[] _cachedHtml = [];
    private byte[] _cachedPng = [];

    // ReSharper disable UnusedAutoPropertyAccessor.Local
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
        public List<PlayerState> Players { get; init; } = [];

        [JsonPropertyName("roles")]
        public List<RoleState> Roles { get; init; } = [];
    }

    private class TagData
    {
        [JsonPropertyName("playerId")]
        public byte PlayerId { get; init; }
        [JsonPropertyName("tag")]
        public string Tag { get; init; } = "";
    }
    // ReSharper restore UnusedAutoPropertyAccessor.Local

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
            lock (StaticLock)
            {
                _gameStateJson = """{"players":[],"roles":[]}""";
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

            if (MeetingHud.Instance != null && playerInfo.IsDead || MeetingHud.Instance != null && MeetingHud.Instance.CurrentState == MeetingHud.VoteStates.Proceeding && MeetingHud.Instance.exiledPlayer == playerInfo)
            {
                if (GameOptionsManager.Instance.currentNormalGameOptions.ConfirmImpostor && playerInfo.Role.TeamType == RoleTeamTypes.Impostor)
                {
                    _playerNotes[playerInfo.PlayerId] = "Imposter";
                    note = "Imposter";
                }
                else
                {
                    _playerNotes[playerInfo.PlayerId] = "Dead";
                    note = "Dead";
                }
            }

            players.Add(new PlayerState
            {
                ID = playerInfo.PlayerId,
                Name = playerInfo.PlayerName,
                Color = ColorUtility.ToHtmlStringRGBA(playerInfo.Color),
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

        lock (StaticLock)
        {
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
            _gameStateJson = JsonSerializer.Serialize(gameState, jsonOptions);
        }
    }

    private void StartHttpServer()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var htmlResource = "SusJournal.index.html";

            using (Stream? stream = assembly.GetManifestResourceStream(htmlResource))
            {
                if (stream != null)
                {
                    using StreamReader reader = new StreamReader(stream);
                    var html = reader.ReadToEnd();
                    _cachedHtml = Encoding.UTF8.GetBytes(html);
                }
                else
                {
                    var availableResources = assembly.GetManifestResourceNames();
                    Logger.LogError($"Embedded resource '{htmlResource}' not found.");
                    Logger.LogWarning("Available resources:");
                    foreach (var res in availableResources)
                    {
                        Logger.LogWarning($"- {res}");
                    }

                    _cachedHtml = "<html><body><h1>Resource not found</h1><p>The embedded resource 'index.html' was not found.</p></body></html>"u8.ToArray();
                }
            }

            var pngResource = "SusJournal.Character.png";
            using (Stream? stream = assembly.GetManifestResourceStream(pngResource))
            {
                if (stream != null)
                {
                    using MemoryStream ms = new MemoryStream();
                    stream.CopyTo(ms);
                    _cachedPng = ms.ToArray();
                }
                else
                {
                    var availableResources = assembly.GetManifestResourceNames();
                    Logger.LogError($"Embedded resource '{pngResource}' not found.");
                    Logger.LogWarning("Available resources:");
                    foreach (var res in availableResources)
                    {
                        Logger.LogWarning($"- {res}");
                    }

                    _cachedPng = [];
                }
            }
            
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
            response.ContentLength64 = _cachedHtml.Length;
            response.OutputStream.Write(_cachedHtml, 0, _cachedHtml.Length);
        }
        else if (request.Url?.AbsolutePath == "/api/gamestate")
        {
            response.ContentType = "application/json";
            string json;
            lock (StaticLock)
            {
                json = _gameStateJson;
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

                lock (StaticLock)
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
            response.ContentLength64 = _cachedPng.Length;
            response.OutputStream.Write(_cachedPng, 0, _cachedPng.Length);
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