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
    public event EventHandler<(uint entityId, string name)>? OnEntityInfo;
    public event EventHandler<(uint bossId, string bossName, long currentHp, long maxHp)>? OnBossHp;
    public event EventHandler<string>? OnError;
    public event EventHandler<string>? OnStatus;

    public bool IsRunning => _captureProcess is { HasExited: false };

    /// <summary>
    /// 즉시 반환. 캡처 프로세스 실행 및 파이프 연결은 백그라운드에서 처리.
    /// </summary>
    public void Start(int port = 2106, string? serverIp = null)
    {
        if (_disposed) return;
        StopInternal();

        WriteLog("CaptureProcessService.Start - begin");

        string captureExe = Path.Combine(AppContext.BaseDirectory, "Aion2Meter.Capture.exe");
        WriteLog($"CaptureProcessService.Start - exe path: {captureExe}, exists: {File.Exists(captureExe)}");

        if (!File.Exists(captureExe))
        {
            OnError?.Invoke(this, $"Aion2Meter.Capture.exe 없음\n경로: {captureExe}");
            return;
        }

        string pipeName = $"Aion2Meter_{Guid.NewGuid():N}";
        _cts = new CancellationTokenSource();

        WriteLog("CaptureProcessService.Start - launching Task.Run");
        _ = Task.Run(() => RunCaptureAsync(captureExe, pipeName, port, serverIp, _cts.Token));
        WriteLog("CaptureProcessService.Start - Task.Run launched, returning");
    }

    private static void WriteLog(string msg)
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Aion2Meter", "init.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private async Task RunCaptureAsync(
        string captureExe, string pipeName, int port, string? serverIp, CancellationToken ct)
    {
        NamedPipeServerStream? pipe = null;
        Process? process = null;

        try
        {
            // 파이프 서버 오픈
            pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            // 캡처 프로세스 실행
            string args = serverIp != null
                ? $"\"{pipeName}\" {port} {serverIp}"
                : $"\"{pipeName}\" {port}";

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName  = captureExe,
                    Arguments = args,
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
                    NotifyError("캡처 프로세스 접속 타임아웃 - Npcap WinPcap 호환 모드 확인");
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
                    OnEntityInfo?.Invoke(this, (
                        root.GetProperty("entityId").GetUInt32(),
                        root.GetProperty("name").GetString() ?? ""));
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
