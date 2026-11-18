using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Central registry for all <see cref="IBasicSkill"/> implementations discovered at startup.
/// </summary>
public sealed class SkillRegistry
{
    private readonly IReadOnlyList<IBasicSkill> _skills;
    private readonly Dictionary<string, IBasicSkill> _byName;

    public SkillRegistry(IEnumerable<IBasicSkill> skills)
    {
        _skills = skills
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _byName = new Dictionary<string, IBasicSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _skills)
        {
            _byName[s.Name] = s;
        }
    }

    public IReadOnlyList<IBasicSkill> All => _skills;

    public bool Contains(string name) => Find(name) is not null;

    public IBasicSkill? Find(string name)
    {
        if (_byName.TryGetValue(name, out var s)) return s;
        var mapped = name.Trim().ToLowerInvariant() switch
        {
            "read_inbox" or "get_mail_detail" or "read_gmail" or "get_gmail_detail" or "sync_mail" => "manage_mail",
            "create_reminder" or "create_schedule" or "list_schedules" or "update_schedule" or "delete_schedule" => "manage_schedule",
            "record_fact" or "search_facts" or "get_fact_detail" or "update_fact" or "delete_fact" => "manage_facts",
            "create_skill" or "delete_skill" => "manage_custom_skill",
            _ => null
        };
        return mapped is not null ? _byName.GetValueOrDefault(mapped) : null;
    }

    public IReadOnlyList<SkillMenuEntry> GetMenuEntries(Func<string, bool> isEnabled)
        => _skills
            .Select(s => new SkillMenuEntry(
                s.Name,
                Resources.AppMessages.Localize($"skill_desc:{s.Name}", s.DisplayDescription),
                false,
                isEnabled(s.Name)))
            .ToList();

    public IEnumerable<(string Name, string Spec)> GetMainAgentSpecs()
        => _skills.Select(s => (s.Name, s.MainAgentSpec));

    public IEnumerable<(string Name, string Spec)> GetPlannerSpecs()
        => _skills.Select(s => (s.Name, s.PlannerSpec));
}
