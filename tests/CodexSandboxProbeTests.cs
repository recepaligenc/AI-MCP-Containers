using System.Diagnostics;
using AiMcpContainers;
using Xunit;

namespace AiMcpContainers.Tests;

public class CodexSandboxProbeTests
{
    /// <summary>A stand-in "codex" binary: a shell script that receives `sandbox -- /bin/true`.</summary>
    private static string Script(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-codex-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Probe_pins_each_restricted_policy_instead_of_trusting_configuration()
    {
        // The policy must be named on the command line: `-c sandbox_mode="danger-full-access"`
        // (or a future config default) makes `codex sandbox` succeed without any sandbox.
        var log = Path.Combine(Path.GetTempPath(), $"probe-log-{Guid.NewGuid():N}");
        var codex = Script($"echo \"$*\" >> {log}\n" +
            "case \"$*\" in 'sandbox -c sandbox_mode=\"read-only\" -- /bin/true'|'sandbox -c sandbox_mode=\"workspace-write\" -- /bin/true') exit 0;; esac; exit 9");
        try
        {
            Assert.True(CodexSandboxProbe.Probe(codex).Available);
            Assert.Equal(new[]
            {
                "sandbox -c sandbox_mode=\"read-only\" -- /bin/true",
                "sandbox -c sandbox_mode=\"workspace-write\" -- /bin/true",
            }, File.ReadAllLines(log));
        }
        finally { File.Delete(codex); File.Delete(log); }
    }

    [Fact]
    public void Probe_is_unavailable_when_any_requestable_policy_fails()
    {
        var codex = Script("case \"$3\" in 'sandbox_mode=\"read-only\"') exit 0;; esac; echo 'bwrap: nope' >&2; exit 1");
        try
        {
            var r = CodexSandboxProbe.Probe(codex);
            Assert.False(r.Available);
            Assert.Contains("(workspace-write) exited 1", r.Detail);
        }
        finally { File.Delete(codex); }
    }

    [Fact]
    public void Probe_never_waits_on_stdin()
    {
        var codex = Script("cat > /dev/null; exit 0");
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.True(CodexSandboxProbe.Probe(codex, timeoutMs: 5000).Available);
            Assert.True(clock.ElapsedMilliseconds < 4000, $"probe took {clock.ElapsedMilliseconds} ms");
        }
        finally { File.Delete(codex); }
    }

    [Fact]
    public void Probe_passes_codex_home_when_configured()
    {
        var codex = Script("[ \"$CODEX_HOME\" = /probe/home ] && exit 0; exit 9");
        try
        {
            Assert.True(CodexSandboxProbe.Probe(codex, codexHome: "/probe/home").Available);
            Assert.False(CodexSandboxProbe.Probe(codex, codexHome: "/elsewhere").Available);
        }
        finally { File.Delete(codex); }
    }

    [Fact]
    public void Probe_reports_the_sandbox_error_without_the_path_alias_warning()
    {
        var codex = Script(
            "echo 'WARNING: proceeding, even though we could not create PATH aliases: Permission denied (os error 13)' >&2\n" +
            "echo 'bwrap: No permissions to create new namespace' >&2\nexit 1");
        try
        {
            var r = CodexSandboxProbe.Probe(codex);
            Assert.False(r.Available);
            Assert.Contains("exited 1", r.Detail);
            Assert.Contains("bwrap: No permissions to create new namespace", r.Detail);
            Assert.DoesNotContain("PATH aliases", r.Detail);
        }
        finally { File.Delete(codex); }
    }

    [Fact]
    public void Probe_detail_is_bounded()
    {
        var codex = Script("head -c 200000 /dev/zero | tr '\\0' x >&2; exit 1");
        try { Assert.True(CodexSandboxProbe.Probe(codex).Detail.Length < 400); }
        finally { File.Delete(codex); }
    }

    [Fact]
    public void Probe_drains_a_chatty_stdout_instead_of_timing_out()
    {
        // 200 KB is more than a pipe buffer: an undrained stdout would block the child.
        var codex = Script("head -c 200000 /dev/zero; exit 0");
        try { Assert.True(CodexSandboxProbe.Probe(codex, timeoutMs: 5000).Available); }
        finally { File.Delete(codex); }
    }

    [Fact]
    public void Probe_reports_unavailable_when_codex_is_missing()
    {
        var r = CodexSandboxProbe.Probe("/nonexistent/codex-for-test");
        Assert.False(r.Available);
        Assert.Contains("could not be started", r.Detail);
    }

    [Fact]
    public void Probe_times_out_as_unavailable_and_does_not_wait_for_the_child()
    {
        var codex = Script("exec sleep 30");
        try
        {
            var clock = Stopwatch.StartNew();
            var r = CodexSandboxProbe.Probe(codex, timeoutMs: 300);
            Assert.False(r.Available);
            Assert.Contains("did not finish", r.Detail);
            Assert.True(clock.ElapsedMilliseconds < 5000, $"probe took {clock.ElapsedMilliseconds} ms");
        }
        finally { File.Delete(codex); }
    }

    [Theory]
    [InlineData("available", true)]
    [InlineData("unavailable", false)]
    [InlineData("UNAVAILABLE", false)]
    public void Operator_override_skips_the_probe(string setting, bool expected)
    {
        var r = CodexSandboxProbe.Resolve(setting, () => throw new InvalidOperationException("probe must not run"));
        Assert.Equal(expected, r.Available);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    public void Auto_runs_the_probe(string? setting)
    {
        var ran = false;
        var r = CodexSandboxProbe.Resolve(setting, () => { ran = true; return new(false, "fake"); });
        Assert.True(ran);
        Assert.False(r.Available);
    }

    [Fact]
    public void Unknown_setting_fails_startup()
        => Assert.Throws<InvalidOperationException>(() => CodexSandboxProbe.Resolve("maybe", () => new(true, "x")));

    [Fact]
    public void Environment_variable_takes_precedence_over_appsettings()
    {
        var both = new Dictionary<string, string?> { ["AI_MCP_CODEX_INNER_SANDBOX"] = "unavailable", ["AiMcp:InnerSandbox"] = "available" };
        var onlyFile = new Dictionary<string, string?> { ["AiMcp:InnerSandbox"] = "available" };
        Assert.Equal("unavailable", CodexSandboxProbe.Setting(k => both.GetValueOrDefault(k)));
        Assert.Equal("available", CodexSandboxProbe.Setting(k => onlyFile.GetValueOrDefault(k)));
        Assert.Null(CodexSandboxProbe.Setting(_ => null));
    }
}
