using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OverlayPerf.Config;
using OverlayPerf.Fps;
using OverlayPerf.Hub;
using OverlayPerf.Models;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace OverlayPerf.Server;

/// <summary>API HTTP/WebSocket (Kestrel) : alimente l'application mobile et tout autre client.
/// Routes et formats identiques a l'agent Python, pour que la PWA existante fonctionne telle quelle.</summary>
public sealed class WebServer : IAsyncDisposable
{
    private const int WsPolicyViolation = 1008;
    private const int WsTryLater = 1013;

    private readonly AppConfig _config;
    private readonly MetricsHub _hub;
    private readonly FrameTimeTracker? _tracker;
    private readonly string _token;
    private readonly AuthThrottle _throttle;
    private readonly ILogger _log;
    private WebApplication? _app;

    public WebServer(AppConfig config, MetricsHub hub, FrameTimeTracker? tracker, string token, ILogger log)
    {
        _config = config;
        _hub = hub;
        _tracker = tracker;
        _token = token;
        _log = log;
        _throttle = new AuthThrottle(config.Server.MaxAuthFailures,
            lockout: TimeSpan.FromSeconds(config.Server.AuthLockoutSeconds));
    }

    public string Scheme => UseTls ? "https" : "http";
    private bool UseTls => _config.Server.TlsCert.Length > 0 && _config.Server.TlsKey.Length > 0;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public async Task StartAsync(CancellationToken cancellation)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Serilog.Log.Logger);
        builder.Services.AddCors();

        var server = _config.Server;
        var address = server.Host is "localhost" ? IPAddress.Loopback : IPAddress.Parse(server.Host);
        X509Certificate2? certificate = null;
        if (UseTls)
        {
            certificate = X509Certificate2.CreateFromPemFile(
                Environment.ExpandEnvironmentVariables(server.TlsCert),
                Environment.ExpandEnvironmentVariables(server.TlsKey));
            // Kestrel exige une cle privee exportable sous Windows : rechargement en PKCS#12.
            certificate = new X509Certificate2(certificate.Export(X509ContentType.Pkcs12));
        }
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, server.Port, listen =>
            {
                if (certificate is not null) listen.UseHttps(certificate);
            });
        });
        if (server.BehindProxy)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
                o.KnownNetworks.Clear();
                o.KnownProxies.Clear();
                if (server.TrustedProxies.Trim() != "*")
                {
                    foreach (var proxy in server.TrustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (IPAddress.TryParse(proxy.Trim(), out var ip)) o.KnownProxies.Add(ip);
                    }
                }
            });
        }

        var app = builder.Build();
        if (server.BehindProxy)
        {
            app.UseForwardedHeaders();
        }
        app.UseCors(policy => policy.AllowAnyOrigin().WithMethods("GET", "POST").AllowAnyHeader());
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        MapApi(app);
        MapWebApp(app);

        _app = app;
        await app.StartAsync(cancellation);
        _log.LogInformation("Serveur en ecoute sur {Scheme}://{Host}:{Port}/", Scheme, server.Host, server.Port);
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Arret du serveur");
        }
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // --- Authentification ------------------------------------------------------------

    private IResult? Authorize(HttpContext context)
    {
        var client = ClientOf(context);
        var wait = _throttle.RetryAfter(client);
        if (wait > 0)
        {
            context.Response.Headers.RetryAfter = ((int)wait + 1).ToString();
            return Results.Json(new { detail = "Trop d'echecs d'authentification : reessayez plus tard" }, statusCode: 429);
        }
        var presented = TokenAuth.Extract(
            name => context.Request.Headers[name].FirstOrDefault(),
            name => context.Request.Query[name].FirstOrDefault());
        if (!TokenAuth.Matches(_token, presented))
        {
            _throttle.RecordFailure(client);
            _log.LogWarning("Authentification refusee pour {Client} sur {Path}", client ?? "?", context.Request.Path);
            return Results.Json(new { detail = "Jeton absent ou invalide" }, statusCode: 401);
        }
        _throttle.RecordSuccess(client);
        return null;
    }

    private string? ClientOf(HttpContext context) =>
        TokenAuth.ClientAddress(context.Connection.RemoteIpAddress?.ToString(),
            name => context.Request.Headers[name].FirstOrDefault(), _config.Server.BehindProxy);

    private static List<string> ParseKeys(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();

    private List<string> Selection(string? raw)
    {
        var keys = ParseKeys(raw);
        return keys.Count > 0 ? keys : _config.Server.Metrics;
    }

    // --- Routes ------------------------------------------------------------------------

    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/health", () =>
        {
            var latest = _hub.Latest;
            return Results.Json(new
            {
                service = "overlay",
                version = Version,
                host = latest?.Host,
                auth_required = _token.Length > 0,
                poll_interval = _hub.PollIntervalSeconds,
                clients = _hub.SubscriberCount,
            });
        });

        app.MapGet("/api/sensors", (HttpContext ctx) =>
        {
            if (Authorize(ctx) is { } refused) return refused;
            var latest = _hub.Latest ?? _hub.Poll();
            return Results.Json(new
            {
                backends = _hub.DescribeBackends(),
                metrics = latest.Readings.Select(r => new
                {
                    key = r.Key, label = r.Label, unit = r.Unit, group = r.Group.Wire(), kind = r.Kind.Wire(),
                }),
            });
        });

        app.MapGet("/api/metrics", (HttpContext ctx, string? keys) =>
        {
            if (Authorize(ctx) is { } refused) return refused;
            var snapshot = _hub.Latest ?? _hub.Poll();
            return Json(snapshot.Filter(Selection(keys)).ToJson());
        });

        app.MapGet("/api/history", (HttpContext ctx, int limit = 60, string? keys = null) =>
        {
            if (Authorize(ctx) is { } refused) return refused;
            limit = Math.Clamp(limit, 1, 1000);
            var selection = Selection(keys);
            var snapshots = new JsonArray();
            foreach (var s in _hub.History(limit))
            {
                snapshots.Add(s.Filter(selection).ToJson());
            }
            return Json(new JsonObject { ["snapshots"] = snapshots });
        });

        // Selection de l'overlay a l'ecran, resolue en cles exactes : la page « Overlay » de
        // l'application mobile affiche exactement la meme chose, dans le meme ordre.
        app.MapGet("/api/overlay", (HttpContext ctx) =>
        {
            if (Authorize(ctx) is { } refused) return refused;
            var overlay = _config.Overlay;
            var latest = _hub.Latest;
            var keys = latest is not null
                ? latest.Filter(overlay.Metrics).Readings.Select(r => r.Key).ToList()
                : overlay.Metrics.Where(m => !m.EndsWith('*')).ToList();
            return Results.Json(new
            {
                keys,
                patterns = overlay.Metrics,
                position = overlay.Position,
                opacity = overlay.Opacity,
                columns = overlay.Columns,
                enabled = overlay.Enabled,
            });
        });

        app.MapGet("/api/fps", (HttpContext ctx) =>
        {
            if (Authorize(ctx) is { } refused) return refused;
            if (_tracker is null) return Results.Json(new { detail = "Suivi FPS desactive" }, statusCode: 503);
            var stats = _tracker.Stats();
            return Results.Json(new
            {
                fps = stats.Fps,
                frame_time_ms = stats.FrameTimeMs,
                low_1_percent = stats.Low1Percent,
                low_01_percent = stats.Low01Percent,
                frames = stats.FrameCount,
                application = stats.Application,
                stale = stats.Stale,
            });
        });

        app.MapPost("/api/fps/frame", async (HttpContext ctx) =>
        {
            if (Authorize(ctx) is { } refused) return refused;
            if (_tracker is null) return Results.Json(new { detail = "Suivi FPS desactive" }, statusCode: 503);
            FramePayload? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<FramePayload>(ctx.Request.Body, JsonOptions);
            }
            catch (JsonException)
            {
                return Results.Json(new { detail = "JSON invalide" }, statusCode: 422);
            }
            if (payload is null) return Results.Json(new { detail = "Corps vide" }, statusCode: 422);
            if (payload.FrameTimeMs is { } ft && (ft <= 0 || ft > 10_000))
                return Results.Json(new { detail = "frame_time_ms doit etre dans ]0, 10000]" }, statusCode: 422);
            if (payload.Application is { Length: > 120 })
                return Results.Json(new { detail = "application : 120 caracteres maximum" }, statusCode: 422);

            if (payload.FrameTimeMs is { } frameTime)
                _tracker.AddFrameTime(frameTime, payload.Application);
            else
                _tracker.AddFrame(payload.Timestamp, payload.Application);
            var stats = _tracker.Stats();
            return Results.Json(new { fps = stats.Fps, frames = stats.FrameCount }, statusCode: 202);
        });

        app.Map("/ws", HandleWebSocket);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static IResult Json(JsonNode node) =>
        Results.Text(node.ToJsonString(), "application/json", Encoding.UTF8);

    private sealed class FramePayload
    {
        public double? FrameTimeMs { get; set; }
        public double? Timestamp { get; set; }
        public string? Application { get; set; }
    }

    private async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("WebSocket attendu");
            return;
        }
        var client = ClientOf(context);
        if (_throttle.RetryAfter(client) > 0)
        {
            // Refus avant acceptation : le navigateur voit un echec de handshake.
            context.Response.StatusCode = 429;
            return;
        }
        var presented = TokenAuth.Extract(
            name => context.Request.Headers[name].FirstOrDefault(),
            name => context.Request.Query[name].FirstOrDefault());
        if (!TokenAuth.Matches(_token, presented))
        {
            _throttle.RecordFailure(client);
            _log.LogWarning("WebSocket refuse pour {Client} : jeton invalide", client ?? "?");
            context.Response.StatusCode = 401;
            return;
        }
        _throttle.RecordSuccess(client);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var selection = Selection(context.Request.Query["keys"].FirstOrDefault());
        var reader = _hub.Subscribe();
        _log.LogInformation("Client WebSocket connecte : {Client} ({Count} au total)", client ?? "?", _hub.SubscriberCount);
        var aborted = context.RequestAborted;
        try
        {
            if (_hub.Latest is { } latest)
            {
                await Send(socket, latest.Filter(selection).ToJson(), aborted);
            }
            // Lecture en parallele : detecte la fermeture cote telephone.
            var receive = ReceiveUntilClose(socket, aborted);
            while (socket.State == WebSocketState.Open && !aborted.IsCancellationRequested)
            {
                var next = reader.ReadAsync(aborted).AsTask();
                var done = await Task.WhenAny(next, receive);
                if (done == receive) break;
                await Send(socket, (await next).Filter(selection).ToJson(), aborted);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or System.Threading.Channels.ChannelClosedException)
        {
            // Coupure reseau brutale du telephone : rien a signaler.
        }
        finally
        {
            _hub.Unsubscribe(reader);
            _log.LogInformation("Client WebSocket parti : {Client}", client ?? "?");
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", CancellationToken.None); }
                catch { /* deja ferme */ }
            }
        }
    }

    private static async Task Send(WebSocket socket, JsonNode node, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    private static async Task ReceiveUntilClose(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
            }
        }
        catch (Exception)
        {
            // fermeture ou coupure : la boucle d'envoi s'arrete
        }
    }

    // --- Application mobile embarquee -------------------------------------------------

    private void MapWebApp(WebApplication app)
    {
        var provider = new ManifestEmbeddedFileProvider(typeof(WebServer).Assembly, "webapp");
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            RequestPath = "/app",
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });

        app.MapGet("/", async (HttpContext ctx) =>
        {
            var index = provider.GetFileInfo("index.html");
            if (!index.Exists)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { detail = "Interface web absente de l'executable" });
                return;
            }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            await using var stream = index.CreateReadStream();
            await stream.CopyToAsync(ctx.Response.Body);
        });
    }
}
