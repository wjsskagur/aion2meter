using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aion2Meter.Capture;

/// <summary>
/// 패킷 캡처 전용 프로세스.
/// SharpPcap으로 네트워크 패킷을 캡처하고
/// PacketParserService로 파싱한 결과를 Named Pipe로 WPF UI에 전달.
///
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

Console.WriteLine($"[Capture] pipe={pipeName} port={port} serverIp={serverIp ?? "auto"}");

// Named Pipe 서버 생성 (UI가 클라이언트로 연결)
await using var pipeServer = new NamedPipeServerStream(
    pipeName,
    PipeDirection.Out,
    maxNumberOfServerInstances: 1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);

// UI 프로세스 연결 대기 (최대 15초)
using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    await pipeServer.WaitForConnectionAsync(connectCts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("[Capture] UI 연결 타임아웃");
    return 1;
}

Console.WriteLine("[Capture] UI connected.");

using var writer = new StreamWriter(pipeServer, Encoding.UTF8, leaveOpen: true)
{
    AutoFlush = true
};

// JSON 전송 헬퍼
async Task SendAsync(object payload)
{
    try
    {
        if (!pipeServer.IsConnected) return;
        string json = JsonSerializer.Serialize(payload);
        await writer.WriteLineAsync(json);
    }
    catch { }
}

// PacketParserService 초기화 및 이벤트 연결
var parser = new PacketParserService();

parser.OnDamageEvent += async dmg =>
    await SendAsync(dmg);

parser.OnEntityInfoEvent += async info =>
    await SendAsync(new { type = "entity", entityId = info.entityId, name = info.name });

parser.OnBossHpEvent += async boss =>
    await SendAsync(new
    {
        type      = "bossHp",
        bossId    = boss.bossId,
        bossName  = boss.bossName,
        currentHp = boss.currentHp,
        maxHp     = boss.maxHp
    });

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
    device.Open(DeviceModes.None, readTimeout: 1000);

    string filter = serverIp != null
        ? $"tcp and host {serverIp} and port {port}"
        : $"tcp and port {port}";
    device.Filter = filter;

    Console.WriteLine($"[Capture] Listening on '{device.Name}' filter='{filter}'");
    await SendAsync(new { type = "status", message = "캡처 중..." });

    device.OnPacketArrival += async (sender, e) =>
    {
        try
        {
            var raw    = e.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var tcp    = packet.Extract<TcpPacket>();

            // 서버 → 클라이언트 방향 패킷만 처리
            if (tcp?.PayloadData is { Length: > 0 } payload && tcp.SourcePort == port)
                await parser.ParsePacketAsync(payload);
        }
        catch { }
    };

    device.StartCapture();

    // UI가 파이프를 닫을 때까지 대기
    while (pipeServer.IsConnected)
        await Task.Delay(500);

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
