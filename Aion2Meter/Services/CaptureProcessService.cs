using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Aion2Meter.Models;

namespace Aion2Meter.Services;

/// <summary>
/// 캡처 프로세스를 실행하고 Named Pipe로 이벤트를 수신.
/// StartAsync는 즉시 반환 - 연결 대기는 완전히 백그라운드에서 처리.
/// </summary>
public class CaptureProcessService : IDisposable
{
    private Process? _captureProcess;
    private NamedPipeServerStream? _pipeServer;
    private CancellationTokenSource? _cts;
    private bool _disposed = false;

    public event EventHandler<CombatEvent>? OnCombatEvent;
    public event EventHandler<(uint entityId, string name, bool isLocalPlayer)>? OnEntityInfo;
    public event EventHandler<(uint bossId, string bossName, long currentHp, long maxHp)>? OnBossHp;
    public event EventHandler<string>? OnError;
    public event EventHandler<string>? OnStatus;

    public bool IsRunning => _captureProcess is { HasExited: false };

    /// <summary>
    /// 즉시 반환. 캡처 프로세스 실행 및 파이프 연결은 백그라운드에서 처리.
    /// </summary>
    public void Start(int port = 13328, string? serverIp = null)
    {
        if (_disposed) return;
        StopInternal();

        WriteLog("CaptureProcessService.Start - begin");

        // serverIp를 비워두면 Capture 프로세스가 포트만으로 필터링
        // → 던전마다 서버 IP가 바뀌어도 자동 대응
        WriteLog($"port={port} serverIp={serverIp ?? "auto(port only)"}");

        // 실행 파일 경로 후보 (dotnet run / Rider / 배포 모두 지원)
        var candidates = new[]
        {
            // 1. 현재 실행 파일과 같은 폴더 (배포 환경)
            Path.Combine(AppContext.BaseDirectory, "Aion2Meter.Capture.exe"),
            // 2. dotnet run 시 임시 폴더 → 프로세스 실행 파일 경로 기반
            Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "",
                "Aion2Meter.Capture.exe"),
            // 3. 현재 디렉터리
            Path.Combine(Directory.GetCurrentDirectory(), "Aion2Meter.Capture.exe"),
            // 4. 로컬 Debug 빌드 경로 (dotnet run 시 AppContext.BaseDirectory = bin\Debug\net8.0-windows\win-x64\)
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "Aion2Meter.Capture", "bin", "Debug", "net8.0-windows", "win-x64",
                "Aion2Meter.Capture.exe"),
        };

        string? captureExe = candidates
            .Select(p => Path.GetFullPath(p))
            .FirstOrDefault(File.Exists);

        WriteLog($"Candidates: {string.Join(", ", candidates.Select(p => $"{p}={File.Exists(p)}"))}");
        WriteLog($"Selected: {captureExe ?? "NONE"}");

        if (captureExe == null)
        {
            OnError?.Invoke(this, $"Aion2Meter.Capture.exe 없음\n검색 경로:\n{string.Join("\n", candidates)}");
            return;
        }

        string pipeName = $"Aion2Meter_{Guid.NewGuid():N}";
        _cts = new CancellationTokenSource();

        // Capture.exe 옆에 .dll도 있으면 dotnet으로 실행 (framework-dependent 빌드)
        string captureDll = Path.ChangeExtension(captureExe, ".dll");
        bool useDotnet = File.Exists(captureDll) && !IsNativeExecutable(captureExe);

        WriteLog($"useDotnet={useDotnet}, dll={captureDll}, dllExists={File.Exists(captureDll)}");

        _ = Task.Run(() => RunCaptureAsync(captureExe, captureDll, useDotnet, pipeName, port, serverIp, _cts.Token));
    }

    private static void WriteLog(string msg)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Aion2Meter");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "init.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    /// <summary>
    /// 13328 포트로 연결된 TCP 연결에서 서버 IP 자동 감지.
    /// 던전마다 서버 IP가 바뀌므로 매 캡처 시작 시 호출.
    /// </summary>
    private static string? DetectAionServerIp(int port)
    {
        try
        {
            var tcpConns = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpConnections();

            // 해당 포트로 ESTABLISHED 연결 중 루프백이 아닌 첫 번째 IP
            foreach (var conn in tcpConns)
            {
                if (conn.RemoteEndPoint.Port != port) continue;
                if (conn.State != System.Net.NetworkInformation.TcpState.Established) continue;
                var ip = conn.RemoteEndPoint.Address.ToString();
                if (ip == "127.0.0.1" || ip == "::1") continue;
                WriteLog($"DetectAionServerIp: auto-detected {ip}:{port}");
                return ip;
            }

            WriteLog($"DetectAionServerIp: no connection to port {port} found, capturing all IPs");
        }
        catch (Exception ex)
        {
            WriteLog($"DetectAionServerIp error: {ex.Message}");
        }
        return null;
    }

    private static bool IsNativeExecutable(string exePath)
    {
        // PE 헤더 확인 - SelfContained exe는 네이티브 실행 가능
        try
        {
            using var fs = File.OpenRead(exePath);
            var buf = new byte[2];
            fs.Read(buf, 0, 2);
            return buf[0] == 0x4D && buf[1] == 0x5A; // MZ header
        }
        catch { return true; }
    }

    private async Task RunCaptureAsync(
        string captureExe, string captureDll, bool useDotnet,
        string pipeName, int port, string? serverIp, CancellationToken ct)
    {
        string args = serverIp != null
            ? $"\"{pipeName}\" {port} {serverIp}"
            : $"\"{pipeName}\" {port}";

        string fileName;
        string arguments;

        if (useDotnet)
        {
            fileName  = "dotnet";
            arguments = $"\"{captureDll}\" {args}";
        }
        else
        {
            fileName  = captureExe;
            arguments = args;
        }

        WriteLog($"RunCaptureAsync: fileName={fileName} args={arguments}");
        NamedPipeServerStream? pipe = null;
        Process? process = null;

        try
        {
            pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName        = fileName,
                    Arguments       = arguments,
                    UseShellExecute = false,
                    CreateNoWindow  = true
                },
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                NotifyError("캡처 프로세스 실행 실패");
                return;
            }

            _captureProcess = process;
            _pipeServer = pipe;

            // 캡처 프로세스 접속 대기 (타임아웃 15초)
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));

            try
            {
                await pipe.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!ct.IsCancellationRequested)
                    NotifyError("캡처 초기화 타임아웃\n↺ 버튼을 눌러 재시도하세요");
                return;
            }

            NotifyStatus("캡처 연결됨");

            // 파이프 읽기 루프
            await ReadPipeAsync(pipe, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_disposed)
        {
            NotifyError($"캡처 오류: {ex.Message}");
        }
        finally
        {
            // process가 _captureProcess와 다를 수 있으므로 둘 다 정리
            try { if (process != null && !process.HasExited) process.Kill(true); } catch { }
            process?.Dispose();
            pipe?.Dispose();
        }
    }

    private async Task ReadPipeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new System.IO.StreamReader(pipe);
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    ProcessMessage(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_disposed)
        {
            NotifyError($"파이프 읽기 오류: {ex.Message}");
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t)) return;

            switch (t.GetString())
            {
                case "damage":
                    OnCombatEvent?.Invoke(this, new CombatEvent
                    {
                        AttackerId   = root.GetProperty("attackerId").GetUInt32(),
                        AttackerName = root.GetProperty("attackerName").GetString() ?? "Unknown",
                        TargetId     = root.GetProperty("targetId").GetUInt32(),
                        TargetName   = root.GetProperty("targetName").GetString() ?? "Unknown",
                        SkillId      = root.GetProperty("skillId").GetUInt32(),
                        SkillName    = root.GetProperty("skillName").GetString() ?? "Unknown",
                        Damage       = root.GetProperty("damage").GetInt64(),
                        IsCritical   = root.GetProperty("isCritical").GetBoolean(),
                        Timestamp    = DateTime.Now
                    });
                    break;
                case "entity":
                    var entityId = root.GetProperty("entityId").GetUInt32();
                    var entityName = root.GetProperty("name").GetString() ?? "";
                    var isLocal = root.TryGetProperty("isLocalPlayer", out var localProp) && localProp.GetBoolean();
                    OnEntityInfo?.Invoke(this, (entityId, entityName, isLocal));
                    break;
                case "bossHp":
                    OnBossHp?.Invoke(this, (
                        root.GetProperty("bossId").GetUInt32(),
                        root.GetProperty("bossName").GetString() ?? "",
                        root.GetProperty("currentHp").GetInt64(),
                        root.GetProperty("maxHp").GetInt64()));
                    break;
                case "status":
                    NotifyStatus(root.GetProperty("message").GetString() ?? "");
                    break;
                case "error":
                    NotifyError(root.GetProperty("message").GetString() ?? "");
                    break;
            }
        }
        catch { }
    }

    // UI 스레드 없이 이벤트만 발행 (호출자가 Dispatcher 처리)
    private void NotifyError(string msg)  => OnError?.Invoke(this, msg);
    private void NotifyStatus(string msg) => OnStatus?.Invoke(this, msg);

    public void Stop() => StopInternal();

    private void StopInternal()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_captureProcess != null)
        {
            try { if (!_captureProcess.HasExited) _captureProcess.Kill(true); } catch { }
            _captureProcess.Dispose();
            _captureProcess = null;
        }

        _pipeServer?.Dispose();
        _pipeServer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal();
        GC.SuppressFinalize(this);
    }
}
