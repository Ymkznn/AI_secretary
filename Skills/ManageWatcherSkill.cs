using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Resources;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Manages whitelisted standing background web/API/DOM monitors.
/// </summary>
public sealed class ManageWatcherSkill : IBasicSkill
{
    private readonly GuardService _guard;
    private readonly SqliteStore _store;

    public ManageWatcherSkill(GuardService guard, SqliteStore store)
    {
        _guard = guard;
        _store = store;
    }

    public string Name => "manage_watcher";
    public string DisplayDescription => "Manage standing web monitors (price/stock/page updates)";
    public string MainAgentSpec => """
            manage_watcher
               - Purpose: Create, list, pause, resume, or delete standing web/price/API/DOM monitors.
               - Args: {"action": "create|list|delete|pause|resume", "watcher_id": "for delete/pause/resume", "title": "...", "kind": "http_text|http_json|browser_dom", "url": "https://...", "selector_or_path": "CSS/JSON/Regex", "condition": "changed|lt|lte|gt|gte|contains", "threshold": "...", "interval_seconds": 300}
            
        """;
    public string PlannerSpec => """manage_watcher: Create/list/delete/pause/resume web monitors. args: {"action":"create|list|delete|pause|resume","watcher_id":"...","title":"...","kind":"http_text|http_json|browser_dom","url":"https://...","selector_or_path":"...","condition":"changed|lt|lte|gt|gte|contains","threshold":"...","interval_seconds":300}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "list").Trim().ToLowerInvariant();
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Updating web monitors..."));

        if (action == "list")
        {
            var list = await _store.GetWatchersAsync(ctx.ConversationId, ctx.Ct);
            if (list.Count == 0)
            {
                ctx.Observations.Add("manage_watcher: no standing watchers.");
            }
            else
            {
                var lines = list.Select(w => $"- [{w.Status}] {w.Title} (id={w.WatcherId}, kind={w.Kind}, interval={w.IntervalSeconds}s, last={w.LastSnapshot ?? "none"})");
                ctx.Observations.Add($"manage_watcher: {list.Count} watcher(s):\n" + string.Join("\n", lines));
            }
            ctx.CompletedSteps.Add("manage_watcher:list");
            return;
        }

        if (action == "delete")
        {
            var id = ctx.GetArg("watcher_id") ?? "";
            var ok = await _store.DeleteWatcherAsync(id, ctx.Ct);
            ctx.Observations.Add(ok ? $"manage_watcher: deleted {id}." : $"manage_watcher: watcher {id} not found.");
            ctx.CompletedSteps.Add("manage_watcher:delete");
            return;
        }

        if (action is "pause" or "resume")
        {
            var id = ctx.GetArg("watcher_id") ?? "";
            var all = await _store.GetWatchersAsync(null, ctx.Ct);
            var target = all.FirstOrDefault(w => w.WatcherId == id);
            if (target is null)
            {
                ctx.Observations.Add($"manage_watcher: watcher {id} not found.");
                return;
            }
            var nextStatus = action == "pause" ? "Paused" : "Active";
            await _store.UpdateWatcherStateAsync(id, target.LastSnapshot, target.LastCheckedAt, DateTimeOffset.UtcNow, 0, nextStatus, ctx.Ct);
            ctx.Observations.Add($"manage_watcher: set \"{target.Title}\" to {nextStatus}.");
            ctx.CompletedSteps.Add($"manage_watcher:{action}");
            return;
        }

        if (action != "create")
        {
            ctx.Observations.Add($"manage_watcher: unsupported action '{action}'.");
            return;
        }

        var title = ctx.GetArg("title") ?? "Untitled Watcher";
        var kind = (ctx.GetArg("kind") ?? "http_text").Trim().ToLowerInvariant();
        var url = ctx.GetArg("url") ?? "";
        var selector = ctx.GetArg("selector_or_path") ?? "*";
        var cond = (ctx.GetArg("condition") ?? "changed").Trim().ToLowerInvariant();
        var thresh = ctx.GetArg("threshold");
        int interval = int.TryParse(ctx.GetArg("interval_seconds"), out var iv) ? iv : 300;

        var existing = await _store.GetWatchersAsync(null, ctx.Ct);
        var activeCount = existing.Count(w => w.Status == "Active");
        var guardRes = _guard.CheckWatcherRegistration(kind, url, interval, activeCount);
        if (!guardRes.Allowed)
        {
            ctx.Observations.Add($"manage_watcher: blocked by guard: {guardRes.Reason}");
            return;
        }

        var approved = await ctx.ConfirmAsync(
            "Standing Watcher Confirmation",
            $"Create standing background monitor:\n\nTitle: {title}\nKind: {kind}\nURL: {url}\nInterval: {interval}s\nCondition: {cond} {thresh}");
        if (!approved)
        {
            ctx.Observations.Add("manage_watcher: user declined creating watcher.");
            return;
        }

        var rec = new StandingWatcherRecord(
            WatcherId: Guid.NewGuid().ToString("N")[..12],
            ConversationId: ctx.ConversationId,
            Title: title,
            Kind: kind,
            Url: url,
            SelectorOrPath: selector,
            ConditionType: cond,
            ThresholdValue: thresh,
            IntervalSeconds: interval,
            LastSnapshot: null,
            LastCheckedAt: null,
            NextCheckAt: DateTimeOffset.UtcNow,
            FailureCount: 0,
            Status: "Active",
            CreatedAt: DateTimeOffset.UtcNow);

        await _store.CreateWatcherAsync(rec, ctx.Ct);
        ctx.Observations.Add($"manage_watcher: created \"{title}\" (id={rec.WatcherId}, interval={interval}s).");
        ctx.CompletedSteps.Add("manage_watcher:create");
    }
}
