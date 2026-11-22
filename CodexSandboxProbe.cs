using System.Diagnostics;
using System.Text;

namespace AiMcpContainers;

/// <summary>
/// Whether codex's OWN sandbox (the one behind client-requested "read-only" / "workspace-write")
/// can start in this process's environment.
///
/// On Linux codex enforces those modes with bubblewrap, and bubblewrap needs to create a user
/// namespace. An unprivileged container cannot ("bwrap: No permissions to create new namespace"),
/// and codex's legacy Landlock path refuses to run without bubblewrap. When that happens codex
/// still answers — it just cannot run a single command, so a review request comes back as a
/// normal-looking answer that read nothing. The probe lets the bridge refuse such a request
/// up front instead (ChatRequestValidator, code "sandbox_unavailable").
///
/// The probe asks the configured codex binary itself to run /bin/true under each restricted policy
/// a client may request, naming the policy explicitly (`codex sandbox -c sandbox_mode="read-only"
/// -- /bin/true`, then workspace-write), so it exercises the same sandbox implementation and copy of
/// bubblewrap a request would, whatever any configuration says. Measured 2026-10-19 with codex-cli
/// 0.162: both exit 1 with the namespace error in the codex container and exit 0 on the host;
/// `-c sandbox_mode="danger-full-access"` exits 0 in the container, which is why the policy is pinned.
/// </summary>
public static class CodexSandboxProbe
{
    public sealed record Result(bool Available, string Detail);

    private const int MaxDetail = 300;

    /// <summary>The restricted policies a client may request (ChatRequestValidator.AllowedSandboxes).</summary>
    public static readonly IReadOnlyList<string> ProbedModes = new[] { "read-only", "workspace-write" };

    /// <summary>The setting, environment variable first (same precedence as every other bridge setting).</summary>
    public static string? Setting(Func<string, string?> config)
        => config("AI_MCP_CODEX_INNER_SANDBOX") ?? config("AiMcp:InnerSandbox");

    /// <summary>Resolve the setting: "auto" (default) runs <paramref name="probe"/>; "available"/"unavailable" are operator overrides.</summary>
    public static Result Resolve(string? setting, Func<Result> probe)
    {
        switch ((setting ?? "auto").Trim().ToLowerInvariant())
        {
            case "" or "auto":
                return probe();
            case "available":
                return new(true, "operator override: available");
            case "unavailable":
                return new(false, "operator override: unavailable");
            default:
                throw new InvalidOperationException(
                    "AI_MCP_CODEX_INNER_SANDBOX must be one of: auto, available, unavailable.");
        }
    }

    /// <summary>Available only when every restricted policy a client may request can run /bin/true.</summary>
    public static Result Probe(string codexBinary, string? codexHome = null, int timeoutMs = 10_000)
    {
        foreach (var mode in ProbedModes)
        {
            var r = ProbeMode(codexBinary, codexHome, mode, timeoutMs);
            if (!r.Available)
                return r;
        }
        return new(true, $"codex sandbox ran /bin/true under {string.Join(" and ", ProbedModes)}");
    }

    private static Result ProbeMode(string codexBinary, string? codexHome, string mode, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = codexBinary,
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "sandbox", "-c", $"sandbox_mode=\"{mode}\"", "--", "/bin/true" })
            psi.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(codexHome))
            psi.Environment["CODEX_HOME"] = codexHome;

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new(false, $"codex could not be started ({e.Message})");
        }
        if (proc is null)
            return new(false, "codex could not be started");

        using (proc)
        {
            proc.StandardInput.Close(); // like the runner: the child never waits on our stdin
            // Drain both pipes so a chatty binary can never block on a full pipe and look like a hang.
            var stdout = DrainAsync(proc.StandardOutput, keep: 0);
            var stderr = DrainAsync(proc.StandardError, keep: 4096);
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                proc.WaitForExit(2000);
                return new(false, $"codex sandbox ({mode}) did not finish within {timeoutMs} ms");
            }
            if (proc.ExitCode == 0)
                return new(true, $"codex sandbox ran /bin/true under {mode}");
            var reason = stderr.Wait(1000)
                ? string.Join(" ", stderr.Result.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("WARNING: proceeding, even though we could not create PATH aliases", StringComparison.Ordinal)))
                : "";
            if (reason.Length > MaxDetail) reason = reason[..MaxDetail];
            return new(false, $"codex sandbox ({mode}) exited {proc.ExitCode}: {reason}");
        }
    }

    /// <summary>Read a pipe to its end, keeping at most <paramref name="keep"/> characters.</summary>
    private static async Task<string> DrainAsync(StreamReader reader, int keep)
    {
        var buffer = new char[4096];
        var kept = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            if (kept.Length < keep)
                kept.Append(buffer, 0, Math.Min(read, keep - kept.Length));
        return kept.ToString();
    }
}
