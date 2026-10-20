using System.Text.Json;
using System.Text.Json.Nodes;
using Clotch.Core.Credentials;
using Clotch.Core.Detection;
using Clotch.Core.Gating;
using Clotch.Core.Local;
using Clotch.Core.Usage;

// clotch-diag: one-shot JSON dump of what Clotch sees on this machine. Tokens are never printed.
// Flags: --no-network  skip the usage request (detection + local transcripts only)
// Exit code: 0 = notch would be shown, 2 = notch would stay hidden.

var noNetwork = args.Contains("--no-network");
var time = TimeProvider.System;
var env = DetectionEnvironment.Current();
var detection = new DetectionService(env).Detect();
var credsSource = new CredentialsReader(ClaudePaths.CredentialsFile(env.ConfigDir));
var creds = credsSource.Read();

MonitorState state;
using (var http = UsageClient.CreateHttpClient())
using (var monitor = new UsageMonitor(credsSource, new UsageClient(http, time), time, new MonitorOptions()))
{
    state = noNetwork
        ? MonitorState.Initial with { Status = creds is null ? MonitorStatus.NoCredentials : MonitorStatus.Ok, SubscriptionType = creds?.SubscriptionType }
        : await monitor.PollOnceAsync(CancellationToken.None);
}

var snap = state.LastGood;
var local = new LocalUsageScanner(ClaudePaths.ProjectsDir(env.ConfigDir), time)
    .Scan(snap?.FiveHour?.ResetsAt?.AddHours(-5));
var gate = Gate.Evaluate(detection, state);

var report = new JsonObject
{
    ["configDir"] = env.ConfigDir,
    ["claudeCode"] = new JsonObject
    {
        ["installed"] = detection.ClaudeCode.Installed,
        ["path"] = detection.ClaudeCode.ExecutablePath,
        ["version"] = detection.ClaudeCode.Version,
        ["configDirExists"] = detection.ClaudeCode.ConfigDirExists,
    },
    ["claudeDesktop"] = new JsonObject
    {
        ["installed"] = detection.ClaudeDesktop.Installed,
        ["kind"] = detection.ClaudeDesktop.Kind.ToString(),
        ["version"] = detection.ClaudeDesktop.Version,
    },
    ["subscription"] = new JsonObject
    {
        ["loggedIn"] = creds is not null,
        ["type"] = creds?.SubscriptionType,
        ["rateLimitTier"] = creds?.RateLimitTier,
        ["tokenExpiresAt"] = creds?.ExpiresAt?.ToString("O"),
        ["accessToken"] = creds is null ? null : "[redacted]",
    },
    ["usage"] = new JsonObject
    {
        ["status"] = noNetwork ? "Skipped" : state.Status.ToString(),
        ["fetchedAt"] = snap?.FetchedAt.ToString("O"),
        ["fiveHour"] = snap?.FiveHour is { } f ? Window(f) : null,
        ["sevenDay"] = snap?.SevenDay is { } s ? Window(s) : null,
        ["windows"] = new JsonArray((snap?.Windows ?? []).Select(w => (JsonNode)Window(w)).ToArray()),
        ["extraUsageEnabled"] = snap?.Extra?.IsEnabled,
        ["breakdown"] = new JsonArray((snap?.Breakdown ?? []).Select(b => (JsonNode)new JsonObject
        {
            ["name"] = b.DisplayName,
            ["percent"] = b.Percent,
        }).ToArray()),
    },
    ["local"] = new JsonObject
    {
        ["filesTracked"] = local.FilesTracked,
        ["windowStart"] = local.WindowStart.ToString("O"),
        ["todayTotal"] = local.TodayTotal,
        ["windowTotal"] = local.WindowTotal,
        ["today"] = Models(local.Today),
        ["window"] = Models(local.Window),
    },
    ["gate"] = new JsonObject
    {
        ["showNotch"] = gate.ShowNotch,
        ["reason"] = gate.Reason.ToString(),
        ["message"] = gate.Message,
    },
};

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine(report.ToJsonString(new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}));
return gate.ShowNotch ? 0 : 2;

static JsonObject Window(UsageWindow w) => new()
{
    ["key"] = w.Key,
    ["name"] = w.DisplayName,
    ["utilization"] = w.Utilization,
    ["resetsAt"] = w.ResetsAt?.ToString("O"),
    ["limitDollars"] = w.LimitDollars,
    ["usedDollars"] = w.UsedDollars,
};

static JsonArray Models(IEnumerable<ModelTokens> ms) => new(ms.Select(m => (JsonNode)new JsonObject
{
    ["model"] = m.Model,
    ["input"] = m.Input,
    ["output"] = m.Output,
    ["cacheCreate"] = m.CacheCreate,
    ["cacheRead"] = m.CacheRead,
    ["messages"] = m.Messages,
    ["total"] = m.Total,
}).ToArray());
