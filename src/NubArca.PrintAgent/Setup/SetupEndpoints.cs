using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NubArca.PrintAgent.Networking;

namespace NubArca.PrintAgent.Setup;

public sealed record SetupConnectBody(string? Ssid, string? Password);

/// <summary>What a phone may send. Anything else is refused before it reaches nmcli.</summary>
public static class SetupValidation
{
    /// <summary>1–32 bytes, no control characters, and never something nmcli could read as an option.</summary>
    public static bool IsValidSsid(string? ssid) =>
        !string.IsNullOrEmpty(ssid)
        && Encoding.UTF8.GetByteCount(ssid) <= 32
        && !ssid.Any(char.IsControl)
        && !ssid.StartsWith('-')
        && !string.IsNullOrWhiteSpace(ssid);

    /// <summary>Empty for an open network, a WPA passphrase (8–63 printable ASCII), or a 64-digit hex key.</summary>
    public static bool IsValidPassword(string? password) =>
        string.IsNullOrEmpty(password)
        || (password.Length is >= 8 and <= 63 && password.All(c => c is >= ' ' and <= '~'))
        || (password.Length == 64 && password.All(Uri.IsHexDigit));
}

/// <summary>
/// The Print Box's local page: status, visible networks, and one form to join a
/// network. Nothing else — no account, no server settings, no logs, no shell.
///
/// The request body is never logged, and no response ever carries a password:
/// the only thing that leaves with the SSID is whether joining it worked.
/// </summary>
public static class SetupEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public static WebApplication Build(NetworkProvisioningService network, PrintBoxStatusService status,
        ILoggerFactory loggers, IPAddress address, int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggers);
        builder.Services.AddSingleton(network);
        builder.Services.AddSingleton(status);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 4096;
            if (address.Equals(IPAddress.Any)) kestrel.ListenAnyIP(port);
            else kestrel.Listen(address, port);
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.CacheControl = "no-store";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        app.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/setup.css", () => Asset("setup.css", "text/css; charset=utf-8"));
        app.MapGet("/setup.js", () => Asset("setup.js", "text/javascript; charset=utf-8"));

        app.MapGet("/setup/status", async (PrintBoxStatusService box, NetworkProvisioningService net,
            CancellationToken ct) =>
        {
            net.NoteUserActivity();
            return Results.Json(await box.GetAsync(ct), Json);
        });

        app.MapGet("/setup/wifi", async (NetworkProvisioningService net, CancellationToken ct) =>
            Results.Json(await net.RefreshNetworksAsync(ct), Json));

        app.MapPost("/setup/wifi/connect", async (HttpContext context, NetworkProvisioningService net) =>
        {
            // JSON only: a cross-site form cannot send it without a CORS
            // preflight this server never answers.
            if (!context.Request.HasJsonContentType()) return Refuse("invalid_request");
            SetupConnectBody? body;
            try
            {
                body = await context.Request.ReadFromJsonAsync<SetupConnectBody>(Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                return Refuse("invalid_request");
            }
            catch (BadHttpRequestException)
            {
                return Refuse("invalid_request");
            }
            if (body is null) return Refuse("invalid_request");
            if (!SetupValidation.IsValidSsid(body.Ssid)) return Refuse("invalid_ssid");
            if (!SetupValidation.IsValidPassword(body.Password)) return Refuse("invalid_password");

            return net.RequestConnect(body.Ssid!, string.IsNullOrEmpty(body.Password) ? null : body.Password) switch
            {
                ConnectRequest.Accepted => Results.Json(new { state = "connecting", ssid = body.Ssid }, Json,
                    statusCode: StatusCodes.Status202Accepted),
                ConnectRequest.Busy => Results.Json(new { error = "busy" }, Json,
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new { error = "not_in_setup_mode" }, Json,
                    statusCode: StatusCodes.Status409Conflict),
            };
        });
        return app;
    }

    private static IResult Refuse(string error) =>
        Results.Json(new { error }, Json, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Asset(string name, string contentType)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup/" + name);
        if (stream is null) return Results.NotFound();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Results.Bytes(memory.ToArray(), contentType);
    }
}

/// <summary>Hosts the setup page inside the agent process, on the configured port.</summary>
public sealed class SetupWebHost : BackgroundService
{
    private readonly NetworkProvisioningService _network;
    private readonly PrintBoxStatusService _status;
    private readonly ILoggerFactory _loggers;
    private readonly NetworkProvisioningOptions _options;
    private readonly ILogger<SetupWebHost> _logger;

    public SetupWebHost(NetworkProvisioningService network, PrintBoxStatusService status,
        ILoggerFactory loggers, NetworkProvisioningOptions options, ILogger<SetupWebHost> logger)
    {
        _network = network; _status = status; _loggers = loggers; _options = options; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WebApplication? app = null;
        try
        {
            app = SetupEndpoints.Build(_network, _status, _loggers, IPAddress.Any, _options.WebPort);
            await app.StartAsync(stoppingToken);
            _logger.LogInformation("Print Box setup page listening on port {Port}.", _options.WebPort);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A busy port must not stop printing.
            _logger.LogError("Print Box setup page could not start ({ExceptionType}: {Message}).",
                ex.GetType().Name, ex.Message);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
            }
        }
    }
}
