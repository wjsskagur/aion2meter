using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;

namespace Aion2Meter.Services;

/// <summary>
/// GitHub Releases API로 최신 버전을 확인하는 서비스.
///
/// 동작 원리:
/// GET https://api.github.com/repos/{owner}/{repo}/releases/latest
/// → 응답의 tag_name(예: "v1.2.0")과 현재 앱 버전 비교
/// → 새 버전이 있으면 UpdateInfo 반환
///
/// GitHub API는 인증 없이 분당 60회 요청 가능 → 앱 시작 시 1회만 호출이므로 충분
/// </summary>
public class UpdateCheckerService
{
    // 본인 GitHub 계정명/레포명으로 변경
    private const string GITHUB_OWNER = "wjsskagur";
    private const string GITHUB_REPO  = "aion2meter";
    private const string API_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/latest";

    private static readonly HttpClient _http = new()
    {
        DefaultRequestHeaders =
        {
            // GitHub API 필수 헤더: User-Agent 없으면 403
            { "User-Agent", "Aion2Meter-UpdateChecker" },
            { "Accept", "application/vnd.github+json" }
        },
        Timeout = TimeSpan.FromSeconds(5) // 업데이트 체크가 앱 실행을 블로킹하지 않도록
    };

    public record UpdateInfo(
        string LatestVersion,
        string CurrentVersion,
        string ReleaseUrl,       // GitHub Release 페이지 URL
        string DownloadUrl,      // Setup.exe 직접 다운로드 URL
        string ReleaseNotes
    );

    /// <summary>
    /// 최신 버전 확인. 새 버전이 있으면 UpdateInfo 반환, 없으면 null.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var response = await _http.GetFromJsonAsync<GitHubReleaseResponse>(API_URL);
            if (response == null) return null;

            string latestVersion = response.TagName.TrimStart('v'); // "v1.2.0" → "1.2.0"
            string currentVersion = GetCurrentVersion();

            if (!IsNewerVersion(latestVersion, currentVersion))
                return null;

            // Assets에서 Setup.exe 다운로드 URL 추출
            string downloadUrl = response.Assets
                .FirstOrDefault(a => a.Name.EndsWith("-Setup.exe"))
                ?.BrowserDownloadUrl ?? response.HtmlUrl;

            return new UpdateInfo(
                LatestVersion: latestVersion,
                CurrentVersion: currentVersion,
                ReleaseUrl: response.HtmlUrl,
                DownloadUrl: downloadUrl,
                ReleaseNotes: response.Body ?? ""
            );
        }
        catch
        {
            // 네트워크 오류, API 오류 등은 조용히 무시 (업데이트 체크 실패가 앱 동작에 영향 없도록)
            return null;
        }
    }

    /// <summary>
    /// 업데이트 다운로드 후 설치 실행.
    /// Setup.exe를 임시 폴더에 다운로드 → 실행 → 앱 종료.
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(UpdateInfo update,
        IProgress<int>? progress = null)
    {
        try
        {
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"Aion2Meter-Setup-{update.LatestVersion}.exe");

            // 스트리밍 다운로드 (진행률 표시)
            using var response = await _http.GetAsync(
                update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var file = File.Create(tempPath);

            byte[] buffer = new byte[81920];
            long downloaded = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;
                if (total > 0)
                    progress?.Report((int)(downloaded * 100 / total));
            }

            // 다운로드 완료 → 새 Setup.exe 실행 후 현재 앱 종료
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });

            App.Current.Dispatcher.Invoke(() => App.Current.Shutdown());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetCurrentVersion()
    {
        // csproj의 <Version> 태그값이 어셈블리 버전으로 들어감
        return Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// SemVer 비교. "1.2.0" > "1.1.5" → true
    /// Version 클래스 사용: Major.Minor.Patch 비교를 직접 구현할 필요 없음
    /// </summary>
    private static bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(latest, out var l) &&
            Version.TryParse(current, out var c))
            return l > c;
        return false;
    }

    // GitHub API 응답 역직렬화용 레코드
    private record GitHubReleaseResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        string TagName,
        [property: System.Text.Json.Serialization.JsonPropertyName("html_url")]
        string HtmlUrl,
        [property: System.Text.Json.Serialization.JsonPropertyName("body")]
        string? Body,
        [property: System.Text.Json.Serialization.JsonPropertyName("assets")]
        List<GitHubAsset> Assets
    );

    private record GitHubAsset(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")]
        string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        string BrowserDownloadUrl
    );
}
