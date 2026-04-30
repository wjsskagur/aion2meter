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

    // 연결별 파서 (테스트 모드)
    var testParsers = new System.Collections.Concurrent.ConcurrentDictionary<ushort, PacketParserService>();
    PacketParserService GetTestParser(ushort dstPort) =>
        testParsers.GetOrAdd(dstPort, _ =>
        {
            var p = new PacketParserService();
            p.OnDamageEvent += dmg => Log($"[DAMAGE] {System.Text.Json.JsonSerializer.Serialize(dmg)}");
            p.OnEntityInfoEvent += info => Log($"[ENTITY] id={info.entityId} name={info.name} local={info.isLocalPlayer}");
            p.OnSpawnEvent += spawn => Log($"[SPAWN] entityId={spawn.entityId} name={spawn.mobName} boss={spawn.isBoss}");
            return p;
        });

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
                        GetTestParser(tcp.DestinationPort).FeedPacket(seqNum, d);
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

// TCP 연결(dst포트)별 파서 - 연결마다 시퀀스 번호가 독립적이므로 분리 필수
var parsers = new System.Collections.Concurrent.ConcurrentDictionary<ushort, PacketParserService>();

PacketParserService GetOrCreateParser(ushort dstPort)
{
    return parsers.GetOrAdd(dstPort, _ =>
    {
        var p = new PacketParserService();
        p.OnDamageEvent += dmg =>
        {
            var json = JsonSerializer.Serialize(dmg);
            Console.WriteLine($"[DAMAGE] {json}");
            // type 필드를 포함해서 전송해야 ProcessMessage에서 "damage"로 라우팅됨
            SendSync(new { type = "damage", data = json });
        };
        p.OnEntityInfoEvent += info =>
        {
            Console.WriteLine($"[ENTITY] id={info.entityId} name={info.name} local={info.isLocalPlayer} server={info.serverId}");
            SendSync(new { type = "entity", entityId = info.entityId, name = info.name, isLocalPlayer = info.isLocalPlayer, serverId = info.serverId });
        };
        p.OnBossHpEvent += boss => SendSync(new { type = "bossHp", bossId = boss.bossId, bossName = boss.bossName, currentHp = boss.currentHp, maxHp = boss.maxHp });
        p.OnSpawnEvent += spawn =>
        {
            Console.WriteLine($"[SPAWN] entityId={spawn.entityId} name={spawn.mobName} boss={spawn.isBoss}");
            SendSync(new { type = "spawn", entityId = spawn.entityId, name = spawn.mobName, isBoss = spawn.isBoss });
        };
        p.OnEntityRemovedEvent += entityId =>
        {
            Console.WriteLine($"[REMOVED] entityId={entityId}");
            SendSync(new { type = "removed", entityId });
        };
        p.OnSummonEvent += s =>
        {
            Console.WriteLine($"[SUMMON] summonId={s.summonId} ownerId={s.ownerId}");
            SendSync(new { type = "summon", summonId = s.summonId, ownerId = s.ownerId });
        };
        p.OnCombatPowerNameEvent += cp =>
        {
            Console.WriteLine($"[CPNAME] nick={cp.nick} server={cp.serverId}");
            SendSync(new { type = "cpname", name = cp.nick, serverId = cp.serverId });
        };
        return p;
    });
}

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
            // 전체 TCP 캡처: 게임 포트(13328)는 전투 파서, 그 외 포트는 닉 스캔
            dev.Filter = serverIp != null ? $"tcp and host {serverIp}" : "tcp";
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
        if (tcp?.PayloadData is not { Length: > 0 } payload) return;

        var data = payload.ToArray();

        // 게임 서버 → 클라이언트 패킷: 전투 파서 + 닉 스캔
        if (tcp.SourcePort == port)
        {
            long seqNum = (long)tcp.SequenceNumber & 0xFFFFFFFFL;
            if (data.Length >= 4)
            {
                ushort op = (ushort)(data[2] | (data[3] << 8));
                Console.WriteLine($"[PKT] src={tcp.SourcePort} dst={tcp.DestinationPort} seq={seqNum} op=0x{op:X4} len={data.Length}");
            }
            GetOrCreateParser(tcp.DestinationPort).FeedPacket(seqNum, data);
        }
        else if (tcp.DestinationPort == port)
        {
            // 클라이언트 → 서버 방향: 무시 (필요시 추가 가능)
        }
        else
        {
            // 다른 포트 TCP: 닉네임 앵커 패턴만 스캔 (파티/로비 서버)
            // 20~4096 바이트로 범위 제한해 성능 보호
            if (data.Length >= 20 && data.Length <= 4096)
                ScanNickPatterns(data);
        }
    }
    catch { }
}

// 다른 포트 패킷에서 NickAnchor(0x4F 0x36 0x00 0x00) 직접 스캔
// PacketParserService.TryScanNickAnchor와 동일 로직을 인라인으로 실행
void ScanNickPatterns(byte[] data)
{
    const int MIN_LEN = 10;
    if (data.Length < MIN_LEN) return;
    for (int i = 0; i <= data.Length - 8; i++)
    {
        if (data[i] != 0x4F || data[i+1] != 0x36 || data[i+2] != 0x00 || data[i+3] != 0x00) continue;
        int pos = i + 4;
        if (pos + 2 >= data.Length) break;
        if (data[pos] != 0x07) continue;

        // VarInt(length) + nick
        int nameStart = pos + 1;
        int count = 0, shift = 0, value = 0;
        while (nameStart + count < data.Length)
        {
            int b = data[nameStart + count++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift >= 32) { count = -1; break; }
        }
        if (count <= 0 || value < 2 || value > 72) continue;
        int dataStart = nameStart + count;
        if (dataStart + value > data.Length) continue;

        string nick;
        try { nick = System.Text.Encoding.UTF8.GetString(data, dataStart, value); }
        catch { continue; }

        bool valid = nick.Length >= 2 && nick.All(c =>
            (c >= '가' && c <= '힣') || (c >= 'a' && c <= 'z') ||
            (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'));
        if (!valid) continue;

        Console.WriteLine($"[NICKANCHOR-MULTI] nick={nick}");
        SendSync(new { type = "cpname", name = nick, serverId = -1 });
        i = dataStart + value;
    }
}
