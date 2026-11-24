using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

internal sealed class PlayRecordingWebAccess
{
    internal const string EpochClaim = "play-recording-epoch";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromHours(8);

    private readonly SemaphoreSlim _gate = new(1);
    private readonly TimeProvider _clock;
    private volatile Grant? _current;

    private sealed record Grant(string Epoch, string GameId, string GameTitle, string? Code,
        DateTimeOffset CodeExpiresAt, DateTimeOffset ExpiresAt);

    public PlayRecordingWebAccess(TimeProvider clock) => _clock = clock;

    public PlayRecordingWebPermission? Permission
    {
        get
        {
            var grant = _current;
            var now = _clock.GetUtcNow();
            return grant is not null && now < grant.ExpiresAt
                ? new(grant.GameId, grant.GameTitle, now < grant.CodeExpiresAt ? grant.Code : null, grant.CodeExpiresAt)
                : null;
        }
    }

    public async Task IssueAsync(string gameId, string gameTitle)
    {
        await _gate.WaitAsync();
        try
        {
            var now = _clock.GetUtcNow();
            _current = new(Guid.NewGuid().ToString("N"), gameId, gameTitle,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), now + CodeLifetime, now + GrantLifetime);
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAsync()
    {
        await _gate.WaitAsync();
        try { _current = null; }
        finally { _gate.Release(); }
    }

    public async Task<(string Epoch, DateTimeOffset ExpiresAt)?> RedeemAsync(string candidate)
    {
        if (candidate.Length != 32) return null;

        await _gate.WaitAsync();
        try
        {
            var grant = _current;
            if (grant?.Code is not { } code || _clock.GetUtcNow() >= grant.CodeExpiresAt
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(code)))
                return null;

            _current = grant with { Code = null };
            return (grant.Epoch, grant.ExpiresAt);
        }
        finally { _gate.Release(); }
    }

    public bool IsCurrent(string? epoch)
    {
        var grant = _current;
        return grant is not null && grant.Epoch == epoch && _clock.GetUtcNow() < grant.ExpiresAt;
    }

    public async Task<T> UseAsync<T>(string? epoch, Func<bool> connected, Func<string, Task<T>> operation)
    {
        await _gate.WaitAsync();
        try
        {
            var grant = _current;
            if (grant is null || grant.Epoch != epoch || _clock.GetUtcNow() >= grant.ExpiresAt || !connected())
                throw new UnauthorizedAccessException();

            // Keep the grant pinned until the whole operation has reached its completion boundary.
            return await operation(grant.GameId);
        }
        finally { _gate.Release(); }
    }

    public static string? ReadEpoch(ClaimsPrincipal? user) => user?.Identity?.IsAuthenticated == true
        ? user.FindFirstValue(EpochClaim)
        : null;
}

/// <summary>The current PC grant. The single-use code, while available, is for local display only.</summary>
public sealed record PlayRecordingWebPermission(string GameId, string GameTitle, string? Code, DateTimeOffset CodeExpiresAt);
