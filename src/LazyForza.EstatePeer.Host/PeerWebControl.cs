using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;
using Microsoft.AspNetCore.StaticFiles;

namespace LazyForza.EstatePeer.Host;

/// <summary>Native RaceServer routes and exact embedded Web assets, sharing the room's authority.</summary>
internal sealed class PeerWebControl : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly RaceCoordinator coordinator;
    private readonly RaceServerConfigurationStore configuration;
    private readonly HostedTrackPackageStore tracks;
    private readonly HostedOrganizerLogoStore logos;
    private readonly RaceBroadcastService broadcasts;
    private readonly object bootstrapSync = new();
    private string? bootstrap;
    private DateTimeOffset bootstrapExpires;
    internal Uri Origin => new(app.Urls.Single());

    private PeerWebControl(WebApplication app, RaceCoordinator coordinator, RaceServerConfigurationStore configuration)
    {
        this.app = app; this.coordinator = coordinator; this.configuration = configuration;
        tracks = app.Services.GetRequiredService<HostedTrackPackageStore>();
        logos = app.Services.GetRequiredService<HostedOrganizerLogoStore>();
        broadcasts = app.Services.GetRequiredService<RaceBroadcastService>();
    }

    internal static async Task<PeerWebControl> StartAsync(PeerHostStart settings, RaceCoordinator coordinator,
        RaceWebSocketRegistry sockets, CancellationToken token)
    {
        var options = new RaceServerOptions { DataDirectory = Path.Combine(settings.DataDirectory, "Control"), ServerName = settings.RoomName };
        var configuration = new RaceServerConfigurationStore(options);
        if (!configuration.IsConfigured)
        {
            var setup = configuration.ConfigureInitial(new(settings.Password, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                settings.RoomName, settings.RaceLaps, settings.SectorCount));
            if (!setup.Success) throw new InvalidDataException(setup.Error);
            configuration.SaveRoomSettings(coordinator.RoomSettings());
        }
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = options.DataDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 5 * 1024 * 1024;
            kestrel.Limits.MaxConcurrentConnections = 32;
            kestrel.Listen(IPAddress.Loopback, 0);
        });
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(sockets);
        builder.Services.AddSingleton<HostedTrackPackageStore>();
        builder.Services.AddSingleton<HostedOrganizerLogoStore>();
        builder.Services.AddSingleton<RaceRuleTemplateStore>();
        builder.Services.AddSingleton<RaceEventProjectStore>();
        builder.Services.AddSingleton<RaceEventLifecycle>();
        builder.Services.AddSingleton<RaceBroadcastService>();
        builder.Services.AddSingleton<RaceWebSocketHandler>();
        builder.Services.AddSingleton(new IngressProtection(new IngressOptions()));
        builder.Services.AddSingleton(new AdminSessionStore(configuration.AuthenticateControlAccount));
        builder.Services.AddHostedService<RaceEventProjectSyncService>();
        var app = builder.Build();
        var control = new PeerWebControl(app, coordinator, configuration);
        try
        {
            if (control.tracks.Current is null)
            {
                await using var source = File.OpenRead(settings.TrackPackagePath);
                await control.tracks.SaveAsync(source, "track.lfzestate", token);
            }
            await app.Services.GetRequiredService<RaceEventLifecycle>().RecoverAsync(token);
            app.Use(async (context, next) =>
            {
                var origin = $"http://127.0.0.1:{context.Connection.LocalPort}";
                if (context.Request.Host.Value != $"127.0.0.1:{context.Connection.LocalPort}" ||
                    context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                    (context.Request.Method is not ("GET" or "HEAD") && context.Request.Headers.Origin != origin))
                { context.Response.StatusCode = 403; return; }
                if (context.Request.Path == "/ws") { context.Response.StatusCode = 404; return; }
                context.Response.Headers.CacheControl = "no-store";
                await next(context);
            });
            app.MapGet("/peer-bootstrap", () => Results.Content(
                "<!doctype html><html lang=\"zh\"><meta charset=\"utf-8\"><title>LazyForza</title><p>正在打开赛事总控…</p><script src=\"/peer-bootstrap.js\"></script></html>", "text/html"));
            app.MapGet("/peer-bootstrap.js", () => Results.Text(
                "const token=location.hash.slice(1);history.replaceState(null,'','/peer-bootstrap');fetch('/peer-session',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token})}).then(r=>{if(!r.ok)throw Error();location.replace('/');}).catch(()=>{document.querySelector('p').textContent='入口已失效，请从客户端重新打开总控。';});",
                "text/javascript"));
            app.MapPost("/peer-session", (BootstrapRequest request, HttpContext context, AdminSessionStore sessions) =>
            {
                lock (control.bootstrapSync)
                {
                    if (control.bootstrap is null || control.bootstrapExpires <= DateTimeOffset.UtcNow || request.Token != control.bootstrap)
                        return Results.Unauthorized();
                    control.bootstrap = null;
                }
                var account = configuration.ListControlAccounts().First(item => item.Role == RaceControlRole.SuperAdmin);
                var session = sessions.Create(new(account.Id, account.Name, RaceControlRole.SuperAdmin));
                context.Response.Cookies.Append(AdminSessionStore.CookieName, session,
                    new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12) });
                return Results.Ok();
            });
            PeerControlApplication.Map(app, options);
            var assembly = typeof(PeerControlApplication).Assembly;
            var assets = assembly.GetManifestResourceNames().Where(name => name.StartsWith("PeerControl/", StringComparison.Ordinal))
                .ToDictionary(name => name[12..], StringComparer.Ordinal);
            var types = new FileExtensionContentTypeProvider();
            app.MapGet("/{**asset}", (string? asset) =>
            {
                asset = string.IsNullOrEmpty(asset) ? "index.html" : asset;
                if (!assets.TryGetValue(asset, out var resource)) return Results.NotFound();
                types.TryGetContentType(asset, out var type);
                return Results.Stream(assembly.GetManifestResourceStream(resource)!, type ?? "application/octet-stream");
            });
            await app.StartAsync(token);
            return control;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    internal string OpenUrl()
    {
        lock (bootstrapSync)
        {
            bootstrap = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            bootstrapExpires = DateTimeOffset.UtcNow.AddMinutes(2);
            return new Uri(Origin, "peer-bootstrap#" + bootstrap).AbsoluteUri;
        }
    }

    internal HostedTrackPackageMetadata? TrackMetadata
    {
        get { var room = coordinator.RoomSettings(); return tracks.Matching(room.TrackId, room.TrackRevision, room.TrackPackageHash); }
    }
    internal Task<byte[]?> ReadTrackAsync(CancellationToken token)
    {
        var room = coordinator.RoomSettings();
        return tracks.ReadAsync(room.TrackId, room.TrackRevision, room.TrackPackageHash, token);
    }
    internal HostedOrganizerLogoMetadata? LogoMetadata => logos.Current;
    internal Task<byte[]?> ReadLogoAsync(CancellationToken token) => logos.ReadAsync(token);
    internal RaceSessionSnapshot WithLogo(RaceSessionSnapshot snapshot) => broadcasts.WithOrganizerLogo(snapshot);

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); }
    }
    private sealed record BootstrapRequest(string Token);
}
