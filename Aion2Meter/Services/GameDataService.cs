using Newtonsoft.Json;
using System.IO;

namespace Aion2Meter.Services;

public static class GameDataService
{
    private static readonly Dictionary<long, string> _skills = new();
    private static readonly Dictionary<long, (string name, bool boss)> _mobs = new();
    private static bool _loaded = false;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string baseDir = AppContext.BaseDirectory;
            LoadSkills(Path.Combine(baseDir, "Resources", "skills.json"));
            LoadMobs(Path.Combine(baseDir, "Resources", "mobs.json"));
        }
        catch { }
    }

    private static void LoadSkills(string path)
    {
        if (!File.Exists(path)) return;
        var items = JsonConvert.DeserializeObject<List<SkillJsonItem>>(File.ReadAllText(path));
        if (items == null) return;
        foreach (var item in items) _skills[item.code] = item.name;
    }

    private static void LoadMobs(string path)
    {
        if (!File.Exists(path)) return;
        var items = JsonConvert.DeserializeObject<List<MobJsonItem>>(File.ReadAllText(path));
        if (items == null) return;
        foreach (var item in items) _mobs[item.code] = (item.name, item.boss);
    }

    public static string GetSkillName(long skillId)
    {
        if (_skills.TryGetValue(skillId, out var n)) return n;
        if (_skills.TryGetValue(skillId / 10, out n)) return n;
        if (_skills.TryGetValue(skillId / 100, out n)) return n;
        return $"Skill_{skillId}";
    }

    public static string? GetMobName(long mobCode)
        => _mobs.TryGetValue(mobCode, out var m) ? m.name : null;

    public static bool IsBoss(long mobCode)
        => _mobs.TryGetValue(mobCode, out var m) && m.boss;

    public static int SkillCount => _skills.Count;
    public static int MobCount => _mobs.Count;

    private class SkillJsonItem { public long code { get; set; } public string name { get; set; } = ""; }
    private class MobJsonItem { public long code { get; set; } public string name { get; set; } = ""; public bool boss { get; set; } }
}
