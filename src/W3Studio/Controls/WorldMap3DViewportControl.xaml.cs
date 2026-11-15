using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio.Controls;

public partial class WorldMap3DViewportControl : UserControl
{
    private bool _isInitialized;
    private bool _isEngineReady;
    private Task? _initTask;
    private BlpIconService? _blpService;
    private MapProject? _project;
    private bool _pendingMapLoad;
    private string _webResDir = string.Empty;
    private TaskCompletionSource<bool>? _loadTcs;
    private Action<int, string>? _activeLoadProgress;

    public event Action? MapLoadedSuccess;
    public event Action<int, string>? MapLoadProgress;
    public event Action<float, float, bool>? TerrainBrushDabReceived;
    public event Action? TerrainBrushStrokeEnded;
    public event Action<string?, int>? EntitySelected;
    public event Action<IReadOnlyList<ViewportEntityKey>>? EntitiesSelected;
    public event Action<string, int>? EntityPropertiesRequested;
    public event Action<string, int, float, float, float>? EntityMoved;
    public event Action<IReadOnlyList<ViewportEntityMoveItem>>? EntitiesMoved;
    public event Action<IReadOnlyList<ViewportEntityRotateItem>>? EntitiesRotated;
    public event Action<string, string, float, float, float, float, float, int, int>? EntityPlaceRequested;
    public event Action<string, int>? EntityDeleteRequested;
    public event Action<IReadOnlyList<ViewportEntityKey>>? EntitiesDeleteRequested;
    public event Action<string, float?, float?>? EntityClipboardRequested;
    public event Action<int>? RegionSelected;
    public event Action<int, float, float, float, float>? RegionUpdated;
    public event Action<float, float, float, float>? RegionCreateRequested;
    public event Action<int>? RegionDeleteRequested;
    public event Action? PlacementCancelled;
    private TaskCompletionSource<W3MapCamera?>? _captureCameraTcs;

    public WorldMap3DViewportControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await EnsureEngineInitializedAsync();
    }

    public void SetContext(BlpIconService blpService, MapProject? project)
    {
        _blpService = blpService;
        _project = project;
    }

    public Task EnsureEngineInitializedAsync()
    {
        if (_isInitialized) return Task.CompletedTask;
        if (_initTask == null || _initTask.IsFaulted || (_initTask.IsCompleted && !_isInitialized))
            _initTask = InitializeInternalAsync();
        return _initTask;
    }

    private async Task InitializeInternalAsync()
    {
        try
        {
            if (WvMap.CoreWebView2 == null)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var webViewEnvDir = Path.Combine(appData, "W3Studio", "WebView2MapProfile");
                Directory.CreateDirectory(webViewEnvDir);

                var env = await CoreWebView2Environment.CreateAsync(null, webViewEnvDir);
                await WvMap.EnsureCoreWebView2Async(env);
            }

            var webResDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebResources");
            if (!Directory.Exists(webResDir))
            {
                var projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\WebResources"));
                if (Directory.Exists(projectDir))
                    webResDir = projectDir;
            }
            _webResDir = webResDir;

            if (WvMap.CoreWebView2 == null) return;

            // Intercept ALL requests under w3studio.local so we don't have conflicts with virtual host mapping
            WvMap.CoreWebView2.AddWebResourceRequestedFilter(
                "https://w3studio.local/*",
                CoreWebView2WebResourceContext.All);

            WvMap.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            WvMap.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            WvMap.CoreWebView2.NavigationCompleted += (_, _) => BorderInitProgress.Visibility = Visibility.Collapsed;

            WvMap.Source = new Uri("https://w3studio.local/map_viewer.html");
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            TxtInitTitle.Text = "Error al inicializar el visor 3D";
            TxtInitDetail.Text = ex.Message;
            PrgInitViewport.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var path = uri.AbsolutePath;

            // 1. Current map payload (/currentmap.w3x)
            if (path.Equals("/currentmap.w3x", StringComparison.OrdinalIgnoreCase))
            {
                if (_project != null && !string.IsNullOrEmpty(_project.FilePath) && File.Exists(_project.FilePath))
                {
                    try
                    {
                        using var fs = new FileStream(_project.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var ms = new MemoryStream();
                        fs.CopyTo(ms);
                        e.Response = WvMap.CoreWebView2.Environment.CreateWebResourceResponse(
                            new MemoryStream(ms.ToArray()),
                            200,
                            "OK",
                            "Content-Type: application/octet-stream\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS");
                        return;
                    }
                    catch
                    {
                    }
                }

                e.Response = WvMap.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(),
                    404,
                    "Not Found",
                    "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
                return;
            }

            // 2. MPQ asset lookups (/mpq/...)
            if (path.StartsWith("/mpq/", StringComparison.OrdinalIgnoreCase))
            {
                var subPath = Uri.UnescapeDataString(path.Substring(5)).Replace('/', '\\').Trim();
                var fileName = Path.GetFileName(subPath);
                if (string.IsNullOrWhiteSpace(subPath) ||
                    subPath.StartsWith(".", StringComparison.Ordinal) ||
                    fileName.Equals("none.blp", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("_.blp", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("0.blp", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(".mdx", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("none.mdx", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = WvMap.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(),
                        404,
                        "Not Found",
                        "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
                    return;
                }
                string? tileset = null;
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = part.Split('=', 2);
                        if (kv.Length == 2 && kv[0].Equals("tileset", StringComparison.OrdinalIgnoreCase))
                        {
                            tileset = Uri.UnescapeDataString(kv[1]);
                            break;
                        }
                    }
                }
                var bytes = _blpService?.FindRawFileBytes(subPath, _project, tileset);
                if (bytes != null && bytes.Length > 0)
                {
                    var mime = GetMimeType(subPath);
                    e.Response = WvMap.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(bytes),
                        200,
                        "OK",
                        $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS");
                    return;
                }

                e.Response = WvMap.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(),
                    404,
                    "Not Found",
                    "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
                return;
            }

            // 3. Local WebResources files (map_viewer.html, viewer.min.js, map_viewer.js, etc.)
            var cleanPath = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', '\\');
            var localFile = Path.Combine(_webResDir, cleanPath);
            if (File.Exists(localFile))
            {
                var fileBytes = File.ReadAllBytes(localFile);
                var mime = GetMimeType(localFile);
                e.Response = WvMap.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(fileBytes),
                    200,
                    "OK",
                    $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS");
                return;
            }

            e.Response = WvMap.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(),
                404,
                "Not Found",
                "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
        }
        catch
        {
            e.Response = WvMap.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(),
                500,
                "Internal Error",
                "Access-Control-Allow-Origin: *");
        }
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".slk" or ".txt" => "text/plain; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;

            var msgType = typeProp.GetString();
            if (msgType is "map_engine_ready" or "map_engine_script_loaded")
            {
                _isEngineReady = true;
                BorderInitProgress.Visibility = Visibility.Collapsed;
                if (_pendingMapLoad && _project != null)
                {
                    _pendingMapLoad = false;
                    LoadMap(_project);
                }
            }
            else if (msgType == "map_load_progress")
            {
                int pct = root.TryGetProperty("percent", out var pctProp) ? pctProp.GetInt32() : 85;
                string txt = root.TryGetProperty("text", out var txtProp) ? (txtProp.GetString() ?? string.Empty) : string.Empty;
                _activeLoadProgress?.Invoke(pct, txt);
                MapLoadProgress?.Invoke(pct, txt);
            }
            else if (msgType == "map_loaded_success")
            {
                _activeLoadProgress?.Invoke(100, "Terreno y unidades listos.");
                _loadTcs?.TrySetResult(true);
                MapLoadedSuccess?.Invoke();
            }
            else if (msgType == "map_load_error")
            {
                _loadTcs?.TrySetResult(false);
            }
            else if (msgType == "terrain_brush_dab")
            {
                if (root.TryGetProperty("wx", out var wxProp) && root.TryGetProperty("wy", out var wyProp))
                {
                    float wx = (float)wxProp.GetDouble();
                    float wy = (float)wyProp.GetDouble();
                    bool isStart = root.TryGetProperty("isStrokeStart", out var stProp) && stProp.GetBoolean();
                    TerrainBrushDabReceived?.Invoke(wx, wy, isStart);
                }
            }
            else if (msgType == "terrain_brush_end")
            {
                TerrainBrushStrokeEnded?.Invoke();
            }
            else if (msgType == "region_selected")
            {
                int cnum = root.TryGetProperty("creationNumber", out var cProp) ? cProp.GetInt32() : -1;
                RegionSelected?.Invoke(cnum);
            }
            else if (msgType == "region_updated")
            {
                int cnum = root.GetProperty("creationNumber").GetInt32();
                float l = (float)root.GetProperty("left").GetDouble();
                float b = (float)root.GetProperty("bottom").GetDouble();
                float r = (float)root.GetProperty("right").GetDouble();
                float t = (float)root.GetProperty("top").GetDouble();
                RegionUpdated?.Invoke(cnum, l, b, r, t);
            }
            else if (msgType == "region_create_request")
            {
                float l = (float)root.GetProperty("left").GetDouble();
                float b = (float)root.GetProperty("bottom").GetDouble();
                float r = (float)root.GetProperty("right").GetDouble();
                float t = (float)root.GetProperty("top").GetDouble();
                RegionCreateRequested?.Invoke(l, b, r, t);
            }
            else if (msgType == "region_delete_request")
            {
                int cnum = root.GetProperty("creationNumber").GetInt32();
                RegionDeleteRequested?.Invoke(cnum);
            }
            else if (msgType == "camera_state_captured")
            {
                var cam = new W3MapCamera
                {
                    TargetX = (float)root.GetProperty("targetX").GetDouble(),
                    TargetY = (float)root.GetProperty("targetY").GetDouble(),
                    ZOffset = (float)root.GetProperty("zOffset").GetDouble(),
                    Rotation = (float)root.GetProperty("rotation").GetDouble(),
                    AngleOfAttack = (float)root.GetProperty("angleOfAttack").GetDouble(),
                    Distance = (float)root.GetProperty("distance").GetDouble(),
                    FieldOfView = 70f,
                    FarClip = 5000f,
                    NearClip = 100f
                };
                _captureCameraTcs?.TrySetResult(cam);
            }
            else if (msgType == "entity_selected")
            {
                if (root.TryGetProperty("entity", out var entProp) && entProp.ValueKind == JsonValueKind.Object)
                {
                    var kind = entProp.TryGetProperty("kind", out var kProp) ? kProp.GetString() : null;
                    int cnum = entProp.TryGetProperty("creationNumber", out var cProp) ? cProp.GetInt32() : -1;
                    EntitySelected?.Invoke(kind, cnum);
                }
                else
                {
                    EntitySelected?.Invoke(null, -1);
                }
            }
            else if (msgType == "entity_moved")
            {
                var kind = root.GetProperty("kind").GetString() ?? "unit";
                int cnum = root.GetProperty("creationNumber").GetInt32();
                float x = (float)root.GetProperty("x").GetDouble();
                float y = (float)root.GetProperty("y").GetDouble();
                float z = (float)root.GetProperty("z").GetDouble();
                EntityMoved?.Invoke(kind, cnum, x, y, z);
            }
            else if (msgType == "entity_place_request")
            {
                var kind = root.GetProperty("kind").GetString() ?? "unit";
                var rawcode = root.GetProperty("rawcode").GetString() ?? string.Empty;
                float x = (float)root.GetProperty("x").GetDouble();
                float y = (float)root.GetProperty("y").GetDouble();
                float z = (float)root.GetProperty("z").GetDouble();
                float angle = root.TryGetProperty("angle", out var aProp) ? (float)aProp.GetDouble() : (float)(270 * Math.PI / 180.0);
                float scale = root.TryGetProperty("scale", out var sProp) ? (float)sProp.GetDouble() : 1.0f;
                int player = root.TryGetProperty("player", out var pProp) ? pProp.GetInt32() : 0;
                int variation = root.TryGetProperty("variation", out var vProp) ? vProp.GetInt32() : 0;
                EntityPlaceRequested?.Invoke(kind, rawcode, x, y, z, angle, scale, player, variation);
            }
            else if (msgType == "entity_delete_request")
            {
                var kind = root.GetProperty("kind").GetString() ?? "unit";
                int cnum = root.GetProperty("creationNumber").GetInt32();
                EntityDeleteRequested?.Invoke(kind, cnum);
            }
            else if (msgType == "entities_selected" && root.TryGetProperty("entities", out var selArr) && selArr.ValueKind == JsonValueKind.Array)
            {
                var keys = new List<ViewportEntityKey>();
                foreach (var item in selArr.EnumerateArray())
                {
                    var k = item.GetProperty("kind").GetString() ?? "unit";
                    int cn = item.GetProperty("creationNumber").GetInt32();
                    keys.Add(new ViewportEntityKey(k, cn));
                }
                EntitiesSelected?.Invoke(keys);
            }
            else if (msgType == "entity_properties_request")
            {
                var kind = root.GetProperty("kind").GetString() ?? "unit";
                int cnum = root.GetProperty("creationNumber").GetInt32();
                EntityPropertiesRequested?.Invoke(kind, cnum);
            }
            else if (msgType == "entities_moved" && root.TryGetProperty("entities", out var mvArr) && mvArr.ValueKind == JsonValueKind.Array)
            {
                var items = new List<ViewportEntityMoveItem>();
                foreach (var item in mvArr.EnumerateArray())
                {
                    items.Add(new ViewportEntityMoveItem(
                        item.GetProperty("kind").GetString() ?? "unit",
                        item.GetProperty("creationNumber").GetInt32(),
                        (float)item.GetProperty("x").GetDouble(),
                        (float)item.GetProperty("y").GetDouble(),
                        (float)item.GetProperty("z").GetDouble()));
                }
                if (items.Count > 0) EntitiesMoved?.Invoke(items);
            }
            else if (msgType == "entities_rotated" && root.TryGetProperty("entities", out var rotArr) && rotArr.ValueKind == JsonValueKind.Array)
            {
                var items = new List<ViewportEntityRotateItem>();
                foreach (var item in rotArr.EnumerateArray())
                {
                    items.Add(new ViewportEntityRotateItem(
                        item.GetProperty("kind").GetString() ?? "unit",
                        item.GetProperty("creationNumber").GetInt32(),
                        (float)item.GetProperty("angle").GetDouble()));
                }
                if (items.Count > 0) EntitiesRotated?.Invoke(items);
            }
            else if (msgType == "entities_delete_request" && root.TryGetProperty("entities", out var delArr) && delArr.ValueKind == JsonValueKind.Array)
            {
                var keys = new List<ViewportEntityKey>();
                foreach (var item in delArr.EnumerateArray())
                {
                    keys.Add(new ViewportEntityKey(
                        item.GetProperty("kind").GetString() ?? "unit",
                        item.GetProperty("creationNumber").GetInt32()));
                }
                if (keys.Count > 0) EntitiesDeleteRequested?.Invoke(keys);
            }
            else if (msgType == "entity_clipboard_request")
            {
                var action = root.GetProperty("action").GetString() ?? string.Empty;
                float? wx = root.TryGetProperty("wx", out var wxEl) && wxEl.ValueKind == JsonValueKind.Number ? (float)wxEl.GetDouble() : null;
                float? wy = root.TryGetProperty("wy", out var wyEl) && wyEl.ValueKind == JsonValueKind.Number ? (float)wyEl.GetDouble() : null;
                EntityClipboardRequested?.Invoke(action, wx, wy);
            }
            else if (msgType == "placement_cancelled")
            {
                PlacementCancelled?.Invoke();
            }
        }
        catch
        {
        }
    }

    public async Task LoadMapAsync(MapProject? project, Action<int, string>? onProgress = null, int timeoutMs = 15000)
    {
        _activeLoadProgress = onProgress;
        _loadTcs?.TrySetResult(false);

        if (project == null || string.IsNullOrEmpty(project.FilePath) || !File.Exists(project.FilePath))
        {
            LoadMap(project);
            _activeLoadProgress = null;
            return;
        }

        onProgress?.Invoke(78, "Inicializando motor 3D WebGL...");
        await Task.WhenAny(EnsureEngineInitializedAsync(), Task.Delay(Math.Max(timeoutMs, 10000)));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadTcs = tcs;
        onProgress?.Invoke(80, "Inicializando terreno 3D y modelos del mapa...");
        LoadMap(project);

        var delayTask = Task.Delay(timeoutMs);
        await Task.WhenAny(tcs.Task, delayTask);
        if (ReferenceEquals(_loadTcs, tcs))
            _loadTcs = null;
        _activeLoadProgress = null;
    }

    public void LoadMap(MapProject? project)
    {
        _project = project;

        if (project == null || string.IsNullOrEmpty(project.FilePath) || !File.Exists(project.FilePath))
        {
            if (_isEngineReady && WvMap.CoreWebView2 != null)
            {
                var clearJson = JsonSerializer.Serialize(new { action = "clear_map" });
                WvMap.CoreWebView2.PostWebMessageAsJson(clearJson);
            }
            return;
        }

        if (!_isInitialized || !_isEngineReady || WvMap.CoreWebView2 == null)
        {
            _pendingMapLoad = true;
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            action = "load_map",
            url = "https://w3studio.local/currentmap.w3x",
            title = string.IsNullOrEmpty(project.MapName) ? project.FileName : project.MapName
        });

        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void ResetCamera()
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "reset_camera" });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SetLightingPreset(string preset)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null || string.IsNullOrWhiteSpace(preset)) return;
        var json = JsonSerializer.Serialize(new { action = "set_lighting", preset });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SetTerrainBrush(bool active, int size, bool isSquare)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new
        {
            action = "set_terrain_brush",
            active,
            size,
            isSquare
        });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public Task ReloadW3eAsync(byte[] w3eBytes)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null || w3eBytes == null || w3eBytes.Length == 0)
            return Task.CompletedTask;

        var json = JsonSerializer.Serialize(new
        {
            action = "reload_w3e",
            base64 = Convert.ToBase64String(w3eBytes)
        });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
        return Task.CompletedTask;
    }

    public Task PatchW3eCornersAsync(TerrainDabResult dab)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null || dab == null || !dab.Modified || dab.Patches.Count == 0)
            return Task.CompletedTask;

        var patches = new object[dab.Patches.Count];
        for (int i = 0; i < dab.Patches.Count; i++)
        {
            var p = dab.Patches[i];
            patches[i] = new
            {
                x = p.X,
                y = p.Y,
                g = p.GroundHeightRaw,
                w = p.WaterRaw,
                f = p.FlagsAndTex,
                v = p.Variations,
                c = p.CliffAndLayer
            };
        }

        var json = JsonSerializer.Serialize(new
        {
            action = "patch_w3e_corners",
            minX = dab.MinX,
            maxX = dab.MaxX,
            minY = dab.MinY,
            maxY = dab.MaxY,
            cliffsChanged = dab.CliffsChanged,
            patches
        });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
        return Task.CompletedTask;
    }

    public void SetEditorMode(string mode, object? placeSpec = null)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "set_editor_mode", mode, placeSpec });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SetPathingOptions(bool ignorePathing)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "set_pathing_options", ignorePathing });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SetCustomObjectOverrides(object overrides)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null || overrides == null) return;
        var json = JsonSerializer.Serialize(new { action = "set_object_overrides", overrides });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SelectEntity(string kind, int creationNumber, bool focus = false, float distance = 1650f)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "select_entity", kind, creationNumber, focus, distance });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SelectEntities(IEnumerable<ViewportEntityKey> entities, bool focus = false, float distance = 1650f)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var payload = entities.Select(e => new { kind = e.Kind, creationNumber = e.CreationNumber }).ToArray();
        var json = JsonSerializer.Serialize(new { action = "select_entities", entities = payload, focus, distance });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void FocusWorldPoint(float x, float y, float distance = 1650f)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "focus_world_point", x, y, distance });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SpawnLiveEntity(string kind, object opts)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "spawn_entity", kind, opts });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void UpdateLiveEntity(string kind, int creationNumber, object opts)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "update_entity", kind, creationNumber, opts });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void DeleteLiveEntity(string kind, int creationNumber)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "delete_entity", kind, creationNumber });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SetLayerVisibility(
        bool showUnits,
        bool showDoodads,
        bool showRegions,
        bool showGrid,
        bool showPathing = false,
        bool showBounds = false,
        bool showWeather = true,
        string? weatherOverride = null)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new
        {
            action = "set_layer_visibility",
            showUnits,
            showDoodads,
            showRegions,
            showGrid,
            showPathing,
            showBounds,
            showWeather,
            weatherOverride
        });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SyncRegions(System.Collections.Generic.IEnumerable<W3MapRegion> regions)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var dto = new System.Collections.Generic.List<object>();
        foreach (var r in regions)
        {
            dto.Add(new
            {
                creationNumber = r.CreationNumber,
                name = r.Name,
                left = r.Left,
                bottom = r.Bottom,
                right = r.Right,
                top = r.Top,
                colorR = r.ColorR,
                colorG = r.ColorG,
                colorB = r.ColorB
            });
        }
        var json = JsonSerializer.Serialize(new { action = "set_regions", regions = dto });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SelectRegion(int creationNumber, bool focus = false, float distance = 2200f)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "select_region", creationNumber, focus, distance });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void FocusCameraPreset(W3MapCamera camera)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null || camera == null) return;
        var json = JsonSerializer.Serialize(new
        {
            action = "focus_camera_preset",
            camera = new
            {
                targetX = camera.TargetX,
                targetY = camera.TargetY,
                zOffset = camera.ZOffset,
                rotation = camera.Rotation,
                angleOfAttack = camera.AngleOfAttack,
                distance = camera.Distance
            }
        });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
    }

    public async Task<W3MapCamera?> CaptureCurrentCameraAsync(int timeoutMs = 1500)
    {
        if (!_isEngineReady || WvMap.CoreWebView2 == null)
            return null;

        var tcs = new TaskCompletionSource<W3MapCamera?>();
        _captureCameraTcs = tcs;
        var json = JsonSerializer.Serialize(new { action = "request_camera_state", requestId = Guid.NewGuid().ToString("N") });
        WvMap.CoreWebView2.PostWebMessageAsJson(json);
        await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (ReferenceEquals(_captureCameraTcs, tcs))
            _captureCameraTcs = null;
        return tcs.Task.IsCompleted ? await tcs.Task : null;
    }
}

public sealed record ViewportEntityKey(string Kind, int CreationNumber);
public sealed record ViewportEntityMoveItem(string Kind, int CreationNumber, float X, float Y, float Z);
public sealed record ViewportEntityRotateItem(string Kind, int CreationNumber, float AngleRadians);
