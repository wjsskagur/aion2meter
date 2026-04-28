using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Aion2Meter.Models;

namespace Aion2Meter.Services;

/// <summary>
/// UI가 Named Pipe 서버, 캡처 프로세스가 클라이언트로 접속.
/// 
/// 파이프 방향 변경 이유:
/// 기존: 캡처 프로세스가 서버 → UI가 ConnectAsync로 대기 → 블로킹 발생
/// 변경: UI가 서버로 먼저 열고 WaitForConnectionAsync 백그라운드 대기
///       → UI는 블로킹 없이 즉시 반환, 캡처 프로세스가 나중에 접속
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

            // UI가 서버로 먼저 파이프를 엽니다
            _pipeServer = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            // 캡처 프로세스 실행 (파이프명을 인자로 전달)
            string args = serverIp != null
                ? $"\"{pipeName}\" {port} {serverIp}"
                : $"\"{pipeName}\" {port}";

            _captureProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = captureExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            _captureProcess.Exited += OnCaptureProcessExited;

            if (!_captureProcess.Start())
            {
                OnError?.Invoke(this, "캡처 프로세스 실행 실패");
                StopInternal();
                return false;
            }

            // 캡처 프로세스 접속 대기 (타임아웃 15초, 백그라운드)
            _cts = new CancellationTokenSource();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            try
            {
                await _pipeServer.WaitForConnectionAsync(connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                OnError?.Invoke(this, "캡처 프로세스 접속 타임아웃\nNpcap WinPcap 호환 모드 확인 필요");
                StopInternal();
                return false;
            }

            // 파이프 읽기 시작
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
            try { if (!_captureProcess.HasExited) _captureProcess.Kill(entireProcessTree: true); }
            catch { }
            _captureProcess.Exited -= OnCaptureProcessExited;
            _captureProcess.Dispose();
            _captureProcess = null;
        }

        _pipeServer?.Dispose();
        _pipeServer = null;
    }

    private async Task ReadPipeAsync(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(_pipeServer!);
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
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

            switch (typeProp.GetString())
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
                    OnStatus?.Invoke(this, root.GetProperty("message").GetString() ?? "");
                    break;

                case "error":
                    OnError?.Invoke(this, root.GetProperty("message").GetString() ?? "");
                    break;
            }
        }
        catch { }
    }

    private void OnCaptureProcessExited(object? sender, EventArgs e)
    {
        if (_disposed) return;
        int code = -1;
        try { code = _captureProcess?.ExitCode ?? -1; } catch { }
        if (code != 0)
            OnError?.Invoke(this, $"캡처 프로세스 비정상 종료 (코드: {code})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal();
        GC.SuppressFinalize(this);
    }
}
