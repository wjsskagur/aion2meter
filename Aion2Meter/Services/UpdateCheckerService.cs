using System.IO;
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
public class UpdateCheckerService : IDisposable
{
    // 본인 GitHub 계정명/레포명으로 변경
    private const string GITHUB_OWNER = "wjsskagur";
    private const string GITHUB_REPO  = "aion2meter";
    private const string API_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/latest";

    private readonly HttpClient _http;

    public UpdateCheckerService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "Aion2Meter-UpdateChecker");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public record UpdateInfo(
        string LatestVersion,
        string CurrentVersion,
        string ReleaseUrl,
        string DownloadUrl,
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
        // ⑤ ArrayPool: 80KB 버퍼를 매번 new로 할당하지 않고 풀에서 대여
        // 다운로드 완료 후 반드시 반납 (using 패턴으로 보장)
        byte[]? buffer = null;
        try
        {
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"Aion2Meter-Setup-{update.LatestVersion}.exe");

            using var response = await _http.GetAsync(
                update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var file = File.Create(tempPath);

            buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
            long downloaded = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, 81920))) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;
                if (total > 0)
                    progress?.Report((int)(downloaded * 100 / total));
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });

            App.Current.Dispatcher.BeginInvoke(() => App.Current.Shutdown());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            // ⑤ 예외 발생 시에도 반드시 풀에 반납
            if (buffer != null)
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string GetCurrentVersion()
    {
        // AssemblyInformationalVersion 우선 사용 (csproj <Version> 태그값)
        // ToString(3) 대신 전체 버전 문자열 사용 → 1.0.1.1 같은 4자리도 정확히 비교
        var assembly = Assembly.GetExecutingAssembly();
        var infoVersion = assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrEmpty(infoVersion))
            return infoVersion.Split('+')[0]; // +build 메타데이터 제거

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        static Version Normalize(string v)
        {
            var parts = v.Split('.');
            // parts 배열을 다시 만들어야 Length가 바뀜
            while (parts.Length < 4)
                parts = (v += ".0").Split('.');
            return Version.TryParse(v, out var ver) ? ver : new Version(0, 0, 0, 0);
        }

        return Normalize(latest) > Normalize(current);
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

    public void Dispose() => _http.Dispose();
}
