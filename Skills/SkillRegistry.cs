using System.Text.Json;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Read-only skill catalog lookup interface injected into GuardService via late binding to avoid DI cycles.
/// </summary>
public interface ISkillCatalogLookup
{
    SkillRecord? Resolve(string skillName);
    bool IsEnabled(string skillName);
}

/// <summary>
/// Unified SQLite-backed skill catalog governing both built-in (<see cref="IBasicSkill"/>) and custom skills.
/// Discovered built-in skill defaults are synced into SQLite on startup while preserving user-toggled state.
/// </summary>
public sealed class SkillRegistry : ISkillCatalogLookup
{
    private readonly Dictionary<string, IBasicSkill> _handlersByType;
    private readonly SqliteStore _store;
    private readonly UiLocalizer _localizer;
    private readonly ILogger<SkillRegistry> _logger;
    private readonly object _snapLock = new();

    private Dictionary<string, SkillRecord> _allByName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _aliasToCanonical = new(StringComparer.OrdinalIgnoreCase);
    private List<SkillRecord> _orderedSkills = new();

    public SkillRegistry(
        IEnumerable<IBasicSkill> skills,
        SqliteStore store,
        UiLocalizer localizer,
        ILogger<SkillRegistry> logger)
    {
        _store = store;
        _localizer = localizer;
        _logger = logger;

        var sortedHandlers = skills
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _handlersByType = sortedHandlers.ToDictionary(s => s.GetType().Name, s => s, StringComparer.OrdinalIgnoreCase);

        BuildSnapshot(BuildDefaultBasicRecords(sortedHandlers));
    }

    private static List<SkillRecord> BuildDefaultBasicRecords(IEnumerable<IBasicSkill> handlers)
    {
        var now = DateTimeOffset.UtcNow;
        return handlers.Select(s => new SkillRecord(
            Name: s.Name,
            Kind: "basic",
            HandlerType: s.GetType().Name,
            DisplayDescription: s.DisplayDescription,
            MainAgentSpec: s.MainAgentSpec,
            PlannerSpec: s.PlannerSpec,
            Command: null,
            Aliases: new List<string>(),
            Capability: s.Capability,
            IsSilentPoller: s.IsSilentPoller,
            IsEnabled: true,
            CreatedAt: now,
            UpdatedAt: now)).ToList();
    }

    private void BuildSnapshot(List<SkillRecord> records)
    {
        var byName = new Dictionary<string, SkillRecord>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rec in records)
        {
            byName[rec.Name] = rec;
            foreach (var alias in rec.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    aliases[alias.Trim()] = rec.Name;
            }
        }

        lock (_snapLock)
        {
            _allByName = byName;
            _aliasToCanonical = aliases;
            _orderedSkills = records
                .OrderBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string migratedFlagKey = "skills_catalog_migrated_v1";
        var migrated = await _store.GetSyncValueAsync(migratedFlagKey, ct) == "1";

        var legacyDisabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!migrated)
        {
            var disabledJson = await _store.GetSyncValueAsync("disabled_skills", ct);
            if (!string.IsNullOrWhiteSpace(disabledJson))
            {
                try
                {
                    var items = JsonSerializer.Deserialize<List<string>>(disabledJson);
                    if (items is not null)
                    {
                        foreach (var item in items.Where(x => !string.IsNullOrWhiteSpace(x)))
                            legacyDisabled.Add(item.Trim());
                    }
                }
                catch { }
            }

            await _store.MigrateLegacyCustomSkillsAsync(legacyDisabled, ct);
        }

        var defaults = BuildDefaultBasicRecords(_handlersByType.Values);
        await _store.SyncBasicSkillsAsync(defaults, legacyDisabled, ct);

        if (!migrated)
            await _store.SetSyncValueAsync(migratedFlagKey, "1", ct);

        await ReloadFromStoreAsync(ct);
        _logger.LogInformation("Loaded {Count} skills into SQLite-backed SkillRegistry.", _orderedSkills.Count);
    }

    public async Task ReloadFromStoreAsync(CancellationToken ct = default)
    {
        var all = await _store.GetSkillsAsync(null, ct);
        BuildSnapshot(all);
    }

    public IReadOnlyList<SkillRecord> All
    {
        get
        {
            lock (_snapLock)
                return _orderedSkills.ToList();
        }
    }

    public SkillRecord? Resolve(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            return null;

        var trimmed = skillName.Trim();
        if (trimmed.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..].Trim();

        lock (_snapLock)
        {
            if (_allByName.TryGetValue(trimmed, out var direct))
                return direct;
            if (_aliasToCanonical.TryGetValue(trimmed, out var canonical) &&
                _allByName.TryGetValue(canonical, out var aliased))
                return aliased;
            return null;
        }
    }

    public (SkillRecord Record, IBasicSkill Handler)? FindBasic(string skillName)
    {
        var rec = Resolve(skillName);
        if (rec is null || !string.Equals(rec.Kind, "basic", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(rec.HandlerType) || !_handlersByType.TryGetValue(rec.HandlerType, out var handler))
            return null;
        return (rec, handler);
    }

    public SkillRecord? FindByCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
            return null;
        lock (_snapLock)
        {
            return _orderedSkills.FirstOrDefault(s =>
                string.Equals(s.Capability, capability, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool ContainsBasic(string name)
    {
        var rec = Resolve(name);
        return rec is not null && string.Equals(rec.Kind, "basic", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsEnabled(string skillName)
        => Resolve(skillName)?.IsEnabled ?? false;

    public async Task SetEnabledAsync(string skillName, bool enabled, CancellationToken ct = default)
    {
        var rec = Resolve(skillName);
        var canonical = rec?.Name ?? skillName.Trim();
        if (canonical.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            canonical = canonical[7..].Trim();

        await _store.SetSkillEnabledInDbAsync(canonical, enabled, ct);
        await ReloadFromStoreAsync(ct);
    }

    public IReadOnlyList<SkillRecord> GetCustom()
    {
        lock (_snapLock)
            return _orderedSkills.Where(s => string.Equals(s.Kind, "custom", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public IReadOnlyList<(string Name, string Spec)> GetEnabledMainAgentSpecs()
    {
        lock (_snapLock)
        {
            return _orderedSkills
                .Where(s => string.Equals(s.Kind, "basic", StringComparison.OrdinalIgnoreCase) && s.IsEnabled)
                .Select(s => (s.Name, s.MainAgentSpec))
                .ToList();
        }
    }

    public IReadOnlyList<(string Name, string Spec)> GetEnabledPlannerSpecs()
    {
        lock (_snapLock)
        {
            return _orderedSkills
                .Where(s => string.Equals(s.Kind, "basic", StringComparison.OrdinalIgnoreCase) && s.IsEnabled)
                .Select(s => (s.Name, s.PlannerSpec))
                .ToList();
        }
    }

    public IReadOnlyDictionary<string, string> GetHotLocalizationEntries()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        List<SkillRecord> basic;
        lock (_snapLock)
            basic = _orderedSkills.Where(s => string.Equals(s.Kind, "basic", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var s in basic)
        {
            map[$"skill_desc:{s.Name}"] = s.DisplayDescription;
            if (!string.IsNullOrWhiteSpace(s.HandlerType) && _handlersByType.TryGetValue(s.HandlerType, out var handler))
                map[$"skill_status:{s.Name}"] = handler.StatusText;
        }
        return map;
    }

    public async Task<IReadOnlyList<SkillMenuEntry>> GetMenuEntriesAsync(CancellationToken ct = default)
    {
        List<SkillRecord> snapshot;
        lock (_snapLock)
            snapshot = _orderedSkills.ToList();

        var entries = new List<SkillMenuEntry>(snapshot.Count);
        foreach (var s in snapshot)
        {
            var isCustom = string.Equals(s.Kind, "custom", StringComparison.OrdinalIgnoreCase);
            var desc = isCustom
                ? s.DisplayDescription
                : await _localizer.TextAsync($"skill_desc:{s.Name}", s.DisplayDescription, Array.Empty<object?>(), ct);
            entries.Add(new SkillMenuEntry(s.Name, desc, isCustom, s.IsEnabled, s.Capability));
        }
        return entries;
    }
}
