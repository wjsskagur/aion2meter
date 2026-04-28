using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aion2Meter.Capture;

/// <summary>
/// 패킷 캡처 전용 프로세스.
/// UI 프로세스가 열어둔 Named Pipe 서버에 클라이언트로 접속.
/// 실행: Aion2Meter.Capture.exe <pipeName> [port] [serverIp]
/// </summary>

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: Aion2Meter.Capture.exe <pipeName> [port] [serverIp]");
    return 1;
}

string pipeName = args[0].Trim('"');
int    port     = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 2106;
string? serverIp = args.Length > 2 ? args[2] : null;

Console.WriteLine($"[Capture] Connecting pipe={pipeName} port={port}");

// UI 프로세스가 서버 → 이쪽이 클라이언트로 접속
await using var pipeClient = new NamedPipeClientStream(
    ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);

try
{
    // UI 서버 접속 (최대 10초)
    await pipeClient.ConnectAsync(10000).ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Capture] Pipe connect failed: {ex.Message}");
    return 1;
}

Console.WriteLine("[Capture] Connected to UI.");

using var writer = new StreamWriter(pipeClient, Encoding.UTF8, leaveOpen: true)
{
    AutoFlush = true
};

void SendSync(object payload)
{
    try
    {
        if (!pipeClient.IsConnected) return;
        writer.WriteLine(JsonSerializer.Serialize(payload));
    }
    catch { }
}

async Task SendAsync(object payload)
{
    try
    {
        if (!pipeClient.IsConnected) return;
        await writer.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    }
    catch { }
}

// PacketParserService 이벤트 연결
var parser = new PacketParserService();
parser.OnDamageEvent    += dmg  => SendSync(dmg);
parser.OnEntityInfoEvent += info => SendSync(new { type = "entity",  entityId = info.entityId, name = info.name });
parser.OnBossHpEvent    += boss => SendSync(new { type = "bossHp", bossId = boss.bossId, bossName = boss.bossName, currentHp = boss.currentHp, maxHp = boss.maxHp });

// SharpPcap 캡처 시작
ILiveDevice? device = null;
try
{
    var devices = CaptureDeviceList.Instance;
    if (devices.Count == 0)
    {
        await SendAsync(new { type = "error", message = "네트워크 인터페이스 없음 - Npcap 설치 확인" });
        return 1;
    }

    device = devices[0];
    device.Open(DeviceModes.None, 1000);

    string filter = serverIp != null
        ? $"tcp and host {serverIp} and port {port}"
        : $"tcp and port {port}";
    device.Filter = filter;

    Console.WriteLine($"[Capture] Listening on '{device.Name}' filter='{filter}'");
    await SendAsync(new { type = "status", message = "캡처 중..." });

    device.OnPacketArrival += OnPacketArrival;
    device.StartCapture();

    // UI가 파이프를 닫을 때까지 대기
    while (pipeClient.IsConnected)
        await Task.Delay(500).ConfigureAwait(false);

    device.OnPacketArrival -= OnPacketArrival;
    device.StopCapture();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Capture] Fatal: {ex}");
    await SendAsync(new { type = "error", message = $"캡처 오류: {ex.Message}" });
    return 1;
}
finally
{
    device?.Close();
    device?.Dispose();
}

Console.WriteLine("[Capture] Exiting normally.");
return 0;

void OnPacketArrival(object? sender, SharpPcap.PacketCapture e)
{
    try
    {
        var raw    = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp    = packet.Extract<TcpPacket>();

        if (tcp?.PayloadData is { Length: > 0 } payload && tcp.SourcePort == port)
        {
            var data = payload.ToArray();
            _ = Task.Run(() => parser.ParsePacket(data));
        }
    }
    catch { }
}
