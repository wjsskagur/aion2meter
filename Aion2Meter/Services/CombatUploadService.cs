using Aion2Meter.Models;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aion2Meter.Services;

/// <summary>
/// 전투 결과를 외부 사이트로 HTTPS + HMAC-SHA256 서명으로 안전하게 전송.
///
/// 보안 설계:
/// 1. HTTPS 강제 - HTTP URL 거부
/// 2. HMAC-SHA256 서명 - 서버가 발급한 secretKey로 payload 서명 (위변조 방지)
/// 3. 익명 clientId - UUID만 전송, 개인정보 없음
/// 4. 타임스탬프 포함 - 리플레이 공격 방지 (서버에서 5분 내 요청만 수락 권장)
/// 5. 재시도 3회 제한 - 게임 플레이 방해 없이 조용히 실패
/// 6. 5초 타임아웃
/// </summary>
public class CombatUploadService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public async Task<bool> UploadAsync(CombatSession session, AppSettings settings)
    {
        if (!settings.AutoUpload) return false;
        if (string.IsNullOrWhiteSpace(settings.UploadUrl)) return false;

        // HTTPS 강제
        if (!settings.UploadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[Upload] HTTPS URL만 허용됩니다.");
            return false;
        }

        // payload 생성
        var payload = BuildPayload(session, settings);
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // HMAC-SHA256 서명
        string signature = ComputeHmac(json, settings.UploadSecretKey);
        string timestamp  = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Signature",  signature);
        content.Headers.Add("X-Timestamp",  timestamp);
        content.Headers.Add("X-Client-Id",  settings.ClientId);
        content.Headers.Add("X-App-Version", "1.0");

        // 재시도 3회
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var response = await _http.PostAsync(settings.UploadUrl, content);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Upload] 전송 성공 ({attempt}회차)");
                    return true;
                }
                Console.WriteLine($"[Upload] 서버 오류 {(int)response.StatusCode} ({attempt}회차)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Upload] 전송 실패 ({attempt}회차): {ex.Message}");
            }

            if (attempt < 3)
                await Task.Delay(attempt * 1000); // 1초, 2초 간격 재시도
        }

        return false;
    }

    private static object BuildPayload(CombatSession session, AppSettings settings)
    {
        double elapsed = session.ElapsedSeconds;

        return new
        {
            version   = "1.0",
            clientId  = settings.ClientId,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            session   = new
            {
                bossName      = session.BossName,
                startTime     = session.StartTime.ToString("O"),
                elapsedSeconds = (int)elapsed,
                totalDamage   = session.TotalPartyDamage,
                players       = session.Players.Values
                    .OrderByDescending(p => p.TotalDamage)
                    .Select(p => new
                    {
                        name          = p.Name,
                        totalDamage   = p.TotalDamage,
                        dps           = elapsed > 0 ? (long)(p.TotalDamage / elapsed) : 0,
                        damagePercent = session.TotalPartyDamage > 0
                            ? Math.Round((double)p.TotalDamage / session.TotalPartyDamage, 4) : 0,
                        maxHit        = p.MaxHit,
                        directDamage  = p.DirectDamage,
                        dotDamage     = p.DotDamage,
                        hitCount      = p.HitCount,
                        critRate      = Math.Round(p.CritRate, 4)
                    })
            }
        };
    }

    /// <summary>
    /// HMAC-SHA256 서명 생성.
    /// 서버에서 동일한 secretKey로 검증:
    ///   expected = HMAC-SHA256(payload, secretKey)
    ///   if header["X-Signature"] != expected → 401 거부
    /// </summary>
    private static string ComputeHmac(string payload, string secretKey)
    {
        if (string.IsNullOrEmpty(secretKey)) return "";
        var keyBytes  = Encoding.UTF8.GetBytes(secretKey);
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        return Convert.ToHexString(hash).ToLower();
    }
}
