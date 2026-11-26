using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A permission prompt answered in Claude Code's own window: Claude Code keeps the PermissionRequest hook running, so the
/// card must leave the phone when Claude Code reports it went on (PostToolUse, Stop, UserPromptSubmit).
/// </summary>
[Collection("Approval request expiry")] // shares the static PhoneLastSeen with ApprovalExpiryTests
public sealed class ApprovalAnsweredOnComputerTests : IDisposable
{
    private readonly long _phone = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-approval-continued-").FullName;
    private readonly PushStore _events;
    private readonly ApprovalStore _store;

    public ApprovalAnsweredOnComputerTests()
    {
        _events = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json"));
        _store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, _phone);
        _events.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Task<string?> Ask(string id, string session, string tool, string detail, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _store.AskAsync(new Approval(id, "Claude Code", session, "p", "/work/p", tool, detail, null, now, now + 60_000), ct);
    }

    private static JsonElement Event(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task PostToolUseWithdrawsOnlyTheCardOfThatRequest()
    {
        using var abort = new CancellationTokenSource();
        var answered = Ask("a", "s1", "Run a command", "rm notes.txt", abort.Token);
        var other = Ask("b", "s1", "Run a command", "git push", abort.Token);
        Assert.Equal(2, _store.Snapshot().Count);

        var n = ClaudeHook.Continued(Event("""
            {"hook_event_name":"PostToolUse","session_id":"s1","tool_name":"Bash","tool_input":{"command":"rm notes.txt"}}
            """), _store);

        Assert.Equal(1, n);
        Assert.Null(await answered.WaitAsync(TimeSpan.FromSeconds(5))); // no decision: Claude Code already has its answer
        Assert.Equal(["b"], _store.Snapshot().Select(a => a.Id));
        abort.Cancel();
        await other.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("UserPromptSubmit")]
    public async Task TheTurnMovingOnWithdrawsEveryCardOfTheSessionOnly(string ev)
    {
        using var abort = new CancellationTokenSource();
        var mine = Ask("a", "s1", "Run a command", "rm notes.txt", abort.Token);
        var otherSession = Ask("b", "s2", "Run a command", "rm notes.txt", abort.Token);

        Assert.Equal(1, ClaudeHook.Continued(Event($$"""{"hook_event_name":"{{ev}}","session_id":"s1"}"""), _store));

        Assert.Null(await mine.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["b"], _store.Snapshot().Select(a => a.Id));
        abort.Cancel();
        await otherSession.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void InstallAddsTheContinuedHooksAndRemoveTakesThemOut()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"my-own-stop-hook"}]}]}}""");

        Assert.Null(ClaudeHook.Configure(true, 18765, 30, path));
        var hooks = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        foreach (var ev in ClaudeHook.ContinuedEvents)
            Assert.Contains(hooks[ev]!.AsArray(), g => g!["hooks"]!.AsArray().Any(h => ClaudeHook.IsOurs(h, ClaudeHook.ContinuedEndpoint)));
        Assert.Equal(2, hooks["Stop"]!.AsArray().Count); // the user's own Stop hook stays

        Assert.Null(ClaudeHook.Configure(false, 18765, 30, path));
        hooks = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        Assert.Null(hooks["PostToolUse"]);
        Assert.Null(hooks["UserPromptSubmit"]);
        Assert.Equal("my-own-stop-hook", (string?)hooks["Stop"]![0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public async Task AWithdrawnCardCanNoLongerBeAllowed()
    {
        var card = Ask("a", "s1", "Run a command", "rm notes.txt", CancellationToken.None);
        Assert.Equal(1, _store.Withdraw("Claude Code", "s1"));

        Assert.False(_store.Decide("a", "allow")); // a late tap on the phone loses the race
        Assert.Null(await card.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(_store.Snapshot());
    }

    [Fact]
    public void AnInstallFromBeforeTheContinuedHooksIsOfferedAgainAndInstallUpgradesIt()
    {
        var path = Path.Combine(_dir, "settings.json");
        Assert.Null(ClaudeHook.Configure(true, 18765, 30, path));
        Assert.True(ClaudeHook.Installed(path));

        // the file as an older PhoneBox left it: only the PermissionRequest hook
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var ev in ClaudeHook.ContinuedEvents) root["hooks"]!.AsObject().Remove(ev);
        File.WriteAllText(path, root.ToJsonString());
        Assert.False(ClaudeHook.Installed(path)); // the settings show "Install hook" again

        Assert.Null(ClaudeHook.Configure(true, 18765, 30, path));
        Assert.True(ClaudeHook.Installed(path));
        var hooks = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        Assert.Single(hooks["PermissionRequest"]!.AsArray()); // replaced, not added twice
    }

    [Fact]
    public void TheContinuedHooksNeverFailWhenPhoneBoxIsClosed()
    {
        var path = Path.Combine(_dir, "settings.json");
        Assert.Null(ClaudeHook.Configure(true, 18765, 30, path));
        var hooks = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        foreach (var ev in ClaudeHook.ContinuedEvents)
            Assert.EndsWith(" || exit 0", (string?)hooks[ev]![0]!["hooks"]![0]!["command"]);
        // the permission hook itself is unchanged: its exit code is how Claude Code falls back to the terminal
        Assert.DoesNotContain("exit 0", (string?)hooks["PermissionRequest"]![0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public void AUsersMalformedEventIsKeptAndDoesNotBlockInstallOrRemove()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"hooks":{"Stop":"not a list"}}""");

        Assert.Null(ClaudeHook.Configure(true, 18765, 30, path));
        var hooks = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        Assert.Equal("not a list", (string?)hooks["Stop"]);
        Assert.NotNull(hooks["PostToolUse"]);
        Assert.True(ClaudeHook.Installed(path));

        Assert.Null(ClaudeHook.Configure(false, 18765, 30, path));
        hooks = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        Assert.Equal("not a list", (string?)hooks["Stop"]);
        Assert.Null(hooks["PermissionRequest"]);
    }
}
