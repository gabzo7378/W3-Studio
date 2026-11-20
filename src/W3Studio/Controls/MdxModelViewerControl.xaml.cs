using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio.Controls;

public partial class MdxModelViewerControl : UserControl
{
    private bool _isInitialized;
    private bool _isEngineReady;
    private Task? _initTask;
    private BlpIconService? _blpService;
    private MapProject? _project;
    private string? _pendingModelPath;
    private int _pendingTeamColor;
    private string _webResDir = string.Empty;

    public event Action<string, List<string>>? ModelLoaded;
    public event Action<int>? AnimationChanged;

    public string? CurrentModelPath => _pendingModelPath;

    public MdxModelViewerControl()
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
        _initTask ??= InitializeInternalAsync();
        return _initTask;
    }

    private async Task InitializeInternalAsync()
    {
        try
        {
            if (WvModel.CoreWebView2 == null)
            {
                var env = await WpfGraphicsRecoveryService.GetSharedWebView2EnvironmentAsync();
                await WvModel.EnsureCoreWebView2Async(env);
            }

            var webResDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebResources");
            if (!Directory.Exists(webResDir))
            {
                var projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\WebResources"));
                if (Directory.Exists(projectDir))
                    webResDir = projectDir;
            }
            _webResDir = webResDir;

            if (WvModel.CoreWebView2 == null) return;

            // Intercept ALL requests on w3studio.local so we don't have conflicts with virtual host mapping
            WvModel.CoreWebView2.AddWebResourceRequestedFilter(
                "https://w3studio.local/*",
                CoreWebView2WebResourceContext.All);

            WvModel.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            WvModel.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            WvModel.Source = new Uri("https://w3studio.local/viewer.html");
            _isInitialized = true;
            BorderInitProgress.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            BorderInitProgress.Child = new TextBlock
            {
                Text = "Error inicializando WebView2: " + ex.Message,
                Foreground = System.Windows.Media.Brushes.Red
            };
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var path = uri.AbsolutePath;

            // 1. MPQ asset lookups (/mpq/...)
            if (path.StartsWith("/mpq/", StringComparison.OrdinalIgnoreCase))
            {
                var subPath = Uri.UnescapeDataString(path.Substring(5)).Replace('/', '\\');
                var bytes = _blpService?.FindRawFileBytes(subPath, _project);
                if (bytes != null && bytes.Length > 0)
                {
                    var mime = GetMimeType(subPath);
                    e.Response = WvModel.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(bytes),
                        200,
                        "OK",
                        $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS");
                    return;
                }

                e.Response = WvModel.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(),
                    404,
                    "Not Found",
                    "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
                return;
            }

            // 2. Local WebResources files (viewer.html, viewer.min.js, model_viewer.js, etc.)
            var cleanPath = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', '\\');
            var localFile = Path.Combine(_webResDir, cleanPath);
            if (File.Exists(localFile))
            {
                var fileBytes = File.ReadAllBytes(localFile);
                var mime = GetMimeType(localFile);
                e.Response = WvModel.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(fileBytes),
                    200,
                    "OK",
                    $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS");
                return;
            }

            e.Response = WvModel.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(),
                404,
                "Not Found",
                "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
        }
        catch
        {
            e.Response = WvModel.CoreWebView2.Environment.CreateWebResourceResponse(
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
            if (msgType == "ready")
            {
                _isEngineReady = true;
                if (!string.IsNullOrEmpty(_pendingModelPath))
                {
                    LoadModel(_pendingModelPath, _pendingTeamColor);
                }
            }
            else if (msgType == "model_loaded")
            {
                var path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var sequences = new List<string>();
                if (root.TryGetProperty("sequences", out var seqProp) && seqProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in seqProp.EnumerateArray())
                    {
                        var s = elem.GetString();
                        if (!string.IsNullOrEmpty(s)) sequences.Add(s);
                    }
                }
                ModelLoaded?.Invoke(path, sequences);
            }
            else if (msgType == "animation_changed")
            {
                var idx = root.TryGetProperty("index", out var ip) ? ip.GetInt32() : 0;
                AnimationChanged?.Invoke(idx);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MdxModelViewer.OnWebMessageReceived", "Malformed or unhandled WebView2 message.", ex);
        }
    }

    public void LoadModel(string mdxPath, int teamColor = 0)
    {
        _pendingModelPath = mdxPath;
        _pendingTeamColor = teamColor;

        if (!_isInitialized || !_isEngineReady || WvModel.CoreWebView2 == null)
            return;

        var json = JsonSerializer.Serialize(new
        {
            action = "load_model",
            path = mdxPath,
            teamColor = teamColor
        });

        WvModel.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void ClearModel()
    {
        _pendingModelPath = null;
        if (!_isInitialized || !_isEngineReady || WvModel.CoreWebView2 == null)
            return;

        var json = JsonSerializer.Serialize(new { action = "clear_model" });
        WvModel.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SetAnimation(int sequenceIndex)
    {
        if (!_isEngineReady || WvModel.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new
        {
            action = "set_animation",
            index = sequenceIndex
        });
        WvModel.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void SetTeamColor(int teamColor)
    {
        _pendingTeamColor = teamColor;
        if (!_isEngineReady || WvModel.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new
        {
            action = "set_team_color",
            color = teamColor
        });
        WvModel.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void ResetCamera()
    {
        if (!_isEngineReady || WvModel.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { action = "reset_camera" });
        WvModel.CoreWebView2.PostWebMessageAsJson(json);
    }
}
