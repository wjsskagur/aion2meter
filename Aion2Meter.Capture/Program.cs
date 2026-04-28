using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aion2Meter.Capture;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: Aion2Meter.Capture.exe <pipeName> [port] [serverIp]");
    return 1;
}

string pipeName  = args[0].Trim('"');
int    port      = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 13328;
string? serverIp = args.Length > 2 ? args[2] : null;
bool   testMode  = pipeName == "test";

Console.WriteLine($"[Capture] pipe={pipeName} port={port} serverIp={serverIp ?? "any"} test={testMode}");

// ── 테스트 모드: 파서 포함 패킷 분석 ────────────────────────────────
if (testMode)
{
    // 로그 파일 경로: 실행 폴더의 capture_log_날짜시간.txt
    string logPath = Path.Combine(
        AppContext.BaseDirectory,
        $"capture_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
    var logWriter = new StreamWriter(logPath, append: false, encoding: Encoding.UTF8) { AutoFlush = true };

    void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Console.WriteLine(line);
        logWriter.WriteLine(line);
    }

    Log($"=== 캡처 시작 port={port} ===");
    Log($"로그 파일: {logPath}");

    var testParser = new PacketParserService();
    testParser.OnDamageEvent += dmg =>
        Log($"[DAMAGE] {System.Text.Json.JsonSerializer.Serialize(dmg)}");
    testParser.OnEntityInfoEvent += info =>
        Log($"[ENTITY] id={info.entityId} name={info.name} local={info.isLocalPlayer}");

    var testDevices = new List<ILiveDevice>();
    foreach (var dev in CaptureDeviceList.Instance)
    {
        try
        {
            dev.Open(DeviceModes.None, 1000);
            dev.Filter = serverIp != null
                ? $"tcp and host {serverIp} and port {port}"
                : $"tcp and port {port}";
            dev.OnPacketArrival += (_, e) =>
            {
                try
                {
                    var pkt = Packet.ParsePacket(e.GetPacket().LinkLayerType, e.GetPacket().Data);
                    var tcp = pkt.Extract<TcpPacket>();
                    if (tcp?.PayloadData is { Length: > 0 } payload && tcp.SourcePort == port)
                    {
                        var d = payload.ToArray();
                        if (d.Length >= 4)
                        {
                            ushort op = (ushort)(d[2] | (d[3] << 8));
                            Log($"[PKT] src={tcp.SourcePort} dst={tcp.DestinationPort} " +
                                $"op=0x{op:X4} len={d.Length} " +
                                $"hex={BitConverter.ToString(d, 0, Math.Min(32, d.Length))}");
                        }
                        long seqNum = (long)tcp.SequenceNumber & 0xFFFFFFFFL;
                        testParser.FeedPacket(seqNum, d);
                    }
                }
                catch { }
            };
            dev.StartCapture();
            testDevices.Add(dev);
            Log($"[Capture] Listening on {dev.Name}");
        }
        catch (Exception ex) { Log($"[SKIP] {ex.Message}"); }
    }

    Console.WriteLine("Press Enter to stop...");
    Console.ReadLine();
    Log("=== 캡처 종료 ===");
    logWriter.Close();
    foreach (var dev in testDevices) { try { dev.StopCapture(); dev.Close(); dev.Dispose(); } catch { } }
    return 0;
}

// ── 정상 모드: 파이프 클라이언트로 접속 ─────────────────────────────
await using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);

try { await pipeClient.ConnectAsync(10000).ConfigureAwait(false); }
catch (Exception ex)
{
    Console.Error.WriteLine($"[Capture] Pipe connect failed: {ex.Message}");
    return 1;
}

Console.WriteLine("[Capture] Connected to UI.");

using var writer = new StreamWriter(pipeClient, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

void SendSync(object payload)
{
    try { if (pipeClient.IsConnected) writer.WriteLine(JsonSerializer.Serialize(payload)); }
    catch { }
}

async Task SendAsync(object payload)
{
    try { if (pipeClient.IsConnected) await writer.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false); }
    catch { }
}

var parser = new PacketParserService();
parser.OnDamageEvent += dmg =>
{
    var json = JsonSerializer.Serialize(dmg);
    Console.WriteLine($"[DAMAGE] {json}");
    SendSync(dmg);
};
parser.OnEntityInfoEvent += info =>
{
    Console.WriteLine($"[ENTITY] id={info.entityId} name={info.name} local={info.isLocalPlayer}");
    SendSync(new { type = "entity", entityId = info.entityId, name = info.name, isLocalPlayer = info.isLocalPlayer });
};
parser.OnBossHpEvent += boss => SendSync(new { type = "bossHp", bossId = boss.bossId, bossName = boss.bossName, currentHp = boss.currentHp, maxHp = boss.maxHp });

var captureDevices = new List<ILiveDevice>();
try
{
    if (CaptureDeviceList.Instance.Count == 0)
    {
        await SendAsync(new { type = "error", message = "네트워크 인터페이스 없음" });
        return 1;
    }

    foreach (var dev in CaptureDeviceList.Instance)
    {
        try
        {
            dev.Open(DeviceModes.None, 1000);
            dev.Filter = serverIp != null
                ? $"tcp and host {serverIp} and port {port}"
                : $"tcp and port {port}";
            dev.OnPacketArrival += OnPacketArrival;
            dev.StartCapture();
            captureDevices.Add(dev);
            Console.WriteLine($"[Capture] Listening on {dev.Name}");
        }
        catch { }
    }

    await SendAsync(new { type = "status", message = "캡처 중..." });

    while (pipeClient.IsConnected)
        await Task.Delay(500).ConfigureAwait(false);
}
finally
{
    foreach (var dev in captureDevices)
    {
        try { dev.OnPacketArrival -= OnPacketArrival; dev.StopCapture(); dev.Close(); dev.Dispose(); } catch { }
    }
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

        // 서버 → 클라이언트 방향만 처리
        if (tcp?.PayloadData is { Length: > 0 } payload && tcp.SourcePort == port)
        {
            var data   = payload.ToArray();
            long seqNum = (long)tcp.SequenceNumber & 0xFFFFFFFFL;

            if (data.Length >= 4)
            {
                ushort op = (ushort)(data[2] | (data[3] << 8));
                Console.WriteLine($"[PKT] src={tcp.SourcePort} dst={tcp.DestinationPort} seq={seqNum} op=0x{op:X4} len={data.Length}");
            }

            // Task.Run 제거 - 순서 보장을 위해 직접 호출
            parser.FeedPacket(seqNum, data);
        }
    }
    catch { }
}
