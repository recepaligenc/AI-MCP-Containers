using System.Collections.Concurrent;

namespace AiMcpContainers;

// Observations are bounded hints, never an inference gate or an authentication probe.
public sealed class ProviderHealth
{
    private sealed record Observation(string State, DateTimeOffset Until);
    private readonly ConcurrentDictionary<string, Observation> _observations = new();
    private readonly TimeProvider _clock;
    public ProviderHealth(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public void Observe(string provider, CodexResult result)
    {
        var now = _clock.GetUtcNow();
        if (result.Ok)
            _observations[provider] = new("ready", now.AddMinutes(1));
        else if (result.ErrorCode is "claude_auth_failed" or "claude_access_forbidden")
            _observations[provider] = new("authentication_failed", now.AddMinutes(1));
        else if (result.ErrorCode == "rate_limited")
        {
            var until = result.RetryAtUtc is { } retry && retry > now ? retry : now.AddMinutes(1);
            _observations[provider] = new("quota_limited", until < now.AddMinutes(15) ? until : now.AddMinutes(15));
        }
    }

    public string State(string provider) =>
        _observations.TryGetValue(provider, out var value) && value.Until > _clock.GetUtcNow()
            ? value.State : "unknown";

    public static bool ExecutableExists(string? binary)
    {
        if (string.IsNullOrWhiteSpace(binary)) return false;
        var paths = Path.IsPathFullyQualified(binary) ? new[] { binary }
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(p => Path.Combine(p, binary));
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            if (OperatingSystem.IsWindows()) return true;
            try
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }
}
