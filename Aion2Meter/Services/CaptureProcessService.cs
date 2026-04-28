using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Aion2Meter.Models;

namespace Aion2Meter.Services;

/// <summary>
/// Aion2Meter.Capture.exe 를 자식 프로세스로 실행하고
/// Named Pipe로 전투 이벤트를 수신하는 서비스.
/// </summary>
public class CaptureProcessService : IDisposable
{
    private Process? _captureProcess;
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private bool _disposed = false;

    public event EventHandler<CombatEvent>? OnCombatEvent;
    public event EventHandler<(uint entityId, string name)>? OnEntityInfo;
    public event EventHandler<(uint bossId, string bossName, long currentHp, long maxHp)>? OnBossHp;
    public event EventHandler<string>? OnError;
    public event EventHandler<string>? OnStatus;

    public bool IsRunning => _captureProcess is { HasExited: false };

    public async Task<bool> StartAsync(int port = 2106, string? serverIp = null)
    {
        if (_disposed) return false;

        StopInternal();

        try
        {
            string captureExe = Path.Combine(AppContext.BaseDirectory, "Aion2Meter.Capture.exe");
            if (!File.Exists(captureExe))
            {
                OnError?.Invoke(this, $"Aion2Meter.Capture.exe 없음\n경로: {captureExe}");
                return false;
            }

            string pipeName = $"Aion2Meter_{Guid.NewGuid():N}";
            string args = serverIp != null
                ? $"\"{pipeName}\" {port} {serverIp}"
                : $"\"{pipeName}\" {port}";

            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);

            _captureProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = captureExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };
            _captureProcess.Exited += OnCaptureProcessExited;

            if (!_captureProcess.Start())
            {
                OnError?.Invoke(this, "캡처 프로세스 실행 실패");
                return false;
            }

            // 프로세스가 파이프 서버를 열 시간을 줌 (2초)
            await Task.Delay(2000);

            // 프로세스가 이미 죽었는지 확인
            if (_captureProcess.HasExited)
            {
                string stderr = await _captureProcess.StandardError.ReadToEndAsync();
                OnError?.Invoke(this, $"캡처 프로세스 즉시 종료\n{stderr}");
                return false;
            }

            // 파이프 연결 (타임아웃 10초)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _pipe.ConnectAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                OnError?.Invoke(this, "캡처 프로세스 파이프 연결 타임아웃\nNpcap WinPcap 호환 모드 재설치 필요");
                StopInternal();
                return false;
            }

            _cts = new CancellationTokenSource();
            _ = ReadPipeAsync(_cts.Token);

            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"캡처 시작 오류: {ex.GetType().Name}\n{ex.Message}");
            StopInternal();
            return false;
        }
    }

    public void Stop() => StopInternal();

    private void StopInternal()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_captureProcess != null)
        {
            try
            {
                if (!_captureProcess.HasExited)
                    _captureProcess.Kill(entireProcessTree: true);
            }
            catch { }
            _captureProcess.Exited -= OnCaptureProcessExited;
            _captureProcess.Dispose();
            _captureProcess = null;
        }

        _pipe?.Dispose();
        _pipe = null;
    }

    private async Task ReadPipeAsync(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(_pipe!);
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break; // 파이프 닫힘
                if (!string.IsNullOrWhiteSpace(line))
                    ProcessMessage(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_disposed)
        {
            OnError?.Invoke(this, $"파이프 읽기 오류: {ex.Message}");
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            string type = typeProp.GetString() ?? "";

            switch (type)
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

                case "bossHp":
                    OnBossHp?.Invoke(this, (
                        root.GetProperty("bossId").GetUInt32(),
                        root.GetProperty("bossName").GetString() ?? "",
                        root.GetProperty("currentHp").GetInt64(),
                        root.GetProperty("maxHp").GetInt64()
                    ));
                    break;

                case "entity":
                    OnEntityInfo?.Invoke(this, (
                        root.GetProperty("entityId").GetUInt32(),
                        root.GetProperty("name").GetString() ?? ""));
                    break;

                case "status":
                    OnStatus?.Invoke(this, root.GetProperty("message").GetString() ?? "");
                    break;

                case "error":
                    OnError?.Invoke(this, root.GetProperty("message").GetString() ?? "");
                    break;
            }
        }
        catch { /* 파싱 실패 무시 */ }
    }

    private void OnCaptureProcessExited(object? sender, EventArgs e)
    {
        if (_disposed) return;
        int exitCode = 0;
        try { exitCode = _captureProcess?.ExitCode ?? -1; } catch { }

        // 정상 종료(0)가 아닐 때만 에러 알림
        if (exitCode != 0)
            OnError?.Invoke(this, $"캡처 프로세스가 비정상 종료됨 (코드: {exitCode})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal();
        GC.SuppressFinalize(this);
    }
}
