using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

public static class PrintStationAuthentication
{
    public const string Scheme = "NubArca.PrintStation";
    public const string Header = "X-NubArca-Print-Credential";
    public const string StationIdClaim = "nubarca:print-station-id";
    public const string OwnerIdClaim = "nubarca:print-owner-id";
}

public sealed class PrintStationAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AppDbContext _db;

    public PrintStationAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppDbContext db) : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(PrintStationAuthentication.Header, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var credential = values.ToString();
        var separator = credential.IndexOf('.');
        if (separator <= 0 || separator == credential.Length - 1
            || !Guid.TryParseExact(credential[..separator], "N", out var stationId))
        {
            return AuthenticateResult.Fail("Invalid print-station credential.");
        }

        var station = await _db.PrintStations.AsNoTracking()
            .Where(x => x.Id == stationId && x.Enabled && x.RevokedAt == null
                && x.DesiredState != PrintDesiredStates.Disabled && x.CredentialHash != null)
            .Select(x => new { x.Id, x.OwnerUserId, x.CredentialHash })
            .SingleOrDefaultAsync(Context.RequestAborted);
        if (station is null || !PrintSecurity.FixedTimeEquals(station.CredentialHash!, credential))
        {
            return AuthenticateResult.Fail("Invalid print-station credential.");
        }

        var identity = new ClaimsIdentity([
            new Claim(PrintStationAuthentication.StationIdClaim, station.Id.ToString("D")),
            new Claim(PrintStationAuthentication.OwnerIdClaim, station.OwnerUserId.ToString("D")),
        ], PrintStationAuthentication.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), PrintStationAuthentication.Scheme));
    }
}
