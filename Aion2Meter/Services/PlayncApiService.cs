using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aion2Meter.Services;

/// <summary>
/// Plaync 공식 API를 통해 캐릭터 정보(전투력, 직업) 조회.
/// A2Viewer PlayncClient 분석 기반 구현.
/// API: https://aion2.plaync.com
/// </summary>
public static class PlayncApiService
{
    private static readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("https://aion2.plaync.com"),
        Timeout = TimeSpan.FromSeconds(8),
    };

    private static readonly Dictionary<string, (int cp, string jobName, DateTime cachedAt)> _cache = new();
    private static readonly SemaphoreSlim _throttle = new(3, 3); // 동시 최대 3건

    static PlayncApiService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// 캐릭터 이름 + 서버ID로 전투력과 직업명 조회.
    /// serverId &lt; 2000: 엘리시온(race=1), &gt;= 2000: 아스모디안(race=2).
    /// 실패 시 null 반환.
    /// </summary>
    public static async Task<(int cp, string jobName)?> FetchCombatPowerAsync(string name, int serverId)
    {
        string key = $"{name}:{serverId}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached) &&
                (DateTime.UtcNow - cached.cachedAt).TotalMinutes < 30)
                return (cached.cp, cached.jobName);
        }

        await _throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            int race = serverId < 2000 ? 1 : 2;
            // SearchCharacter: 이름으로 charId 획득
            string? charId = await SearchCharacterAsync(name, serverId, race).ConfigureAwait(false);
            if (charId == null && race == 1)
                charId = await SearchCharacterAsync(name, serverId, 2).ConfigureAwait(false);
            if (charId == null) return null;

            // FetchInfo: charId로 CP, 직업명 획득
            var (cp, jobName) = await FetchInfoAsync(charId, serverId).ConfigureAwait(false);
            if (cp <= 0) return null;

            lock (_cache)
                _cache[key] = (cp, jobName, DateTime.UtcNow);

            Console.WriteLine($"[PLAYNC] {name}@{serverId} CP={cp} job={jobName}");
            return (cp, jobName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PLAYNC] 조회 실패 {name}@{serverId}: {ex.Message}");
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static async Task<string?> SearchCharacterAsync(string name, int serverId, int race)
    {
        try
        {
            string path = $"/ko-kr/api/search/aion2/search/v2/character" +
                          $"?keyword={Uri.EscapeDataString(name)}&race={race}&serverId={serverId}";
            string json = await _http.GetStringAsync(path).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("list", out var list)) return null;

            foreach (var item in list.EnumerateArray())
            {
                string itemName = Regex.Replace(
                    item.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "", "<[^>]+>", "");
                if (!itemName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!item.TryGetProperty("characterId", out var cid)) continue;
                string charId = cid.ValueKind == JsonValueKind.String
                    ? Uri.UnescapeDataString(cid.GetString() ?? "")
                    : cid.GetRawText();
                return charId;
            }
        }
        catch { }
        return null;
    }

    private static async Task<(int cp, string jobName)> FetchInfoAsync(string charId, int serverId)
    {
        try
        {
            string path = $"/api/character/info?lang=ko" +
                          $"&characterId={Uri.EscapeDataString(charId)}&serverId={serverId}";
            string json = await _http.GetStringAsync(path).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int cp = 0;
            string jobName = "";
            if (root.TryGetProperty("profile", out var profile))
            {
                if (profile.TryGetProperty("combatPower", out var cpProp))
                    cp = cpProp.ValueKind == JsonValueKind.Number
                        ? cpProp.GetInt32()
                        : int.TryParse(cpProp.GetString(), out var n) ? n : 0;
                if (profile.TryGetProperty("className", out var jn))
                    jobName = jn.GetString() ?? "";
            }
            return (cp, jobName);
        }
        catch { }
        return (0, "");
    }
}
