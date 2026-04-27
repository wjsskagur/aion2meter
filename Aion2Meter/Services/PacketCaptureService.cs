using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using Aion2Meter.Models;
using System.Net;

namespace Aion2Meter.Services;

/// <summary>
/// SharpPcap을 이용한 네트워크 패킷 캡처 서비스.
/// 
/// 동작 원리:
/// 1. Npcap 드라이버를 통해 네트워크 인터페이스를 RAW 모드로 열기
/// 2. BPF 필터로 아이온2 서버 트래픽만 선별 (포트 번호 기반)
/// 3. 패킷 수신 시 이벤트로 PacketParserService에 전달
/// 
/// 왜 SharpPcap인가:
/// - Npcap의 C API를 .NET에서 사용 가능하게 래핑
/// - 비동기 캡처를 이벤트 기반으로 제공 → UI 블로킹 없음
/// </summary>
public class PacketCaptureService : IDisposable
{
    // 아이온2 서버 포트 (실제 포트로 교체 필요)
    // TCP 55000번대가 일반적인 MMORPG 게임 포트
    private const int AION2_PORT = 2106; // 패킷 분석 후 실제 포트로 수정

    private ILiveDevice? _device;
    private readonly PacketParserService _parser;
    private bool _isCapturing = false;

    /// <summary>파싱된 전투 이벤트 발생 시 구독자에게 전달</summary>
    public event EventHandler<CombatEvent>? OnCombatEvent;

    /// <summary>새 엔티티 정보(캐릭터명) 수신 시</summary>
    public event EventHandler<(uint entityId, string name)>? OnEntityInfo;

    /// <summary>캡처 에러 발생 시</summary>
    public event EventHandler<string>? OnError;

    public PacketCaptureService(PacketParserService parser)
    {
        _parser = parser;
        _parser.OnCombatEvent += (s, e) => OnCombatEvent?.Invoke(this, e);
        _parser.OnEntityInfo += (s, e) => OnEntityInfo?.Invoke(this, e);
    }

    /// <summary>
    /// 사용 가능한 네트워크 인터페이스 목록 반환.
    /// 설정 화면에서 사용자가 선택하게 하거나, 자동 선택에 사용.
    /// </summary>
    public static List<string> GetAvailableDevices()
    {
        try
        {
            return CaptureDeviceList.Instance
                .Select(d => d.Name)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 패킷 캡처 시작.
    /// deviceName이 null이면 첫 번째 활성 인터페이스를 자동 선택.
    /// </summary>
    public bool Start(string? deviceName = null, string? serverIp = null)
    {
        if (_isCapturing) return true;

        try
        {
            var devices = CaptureDeviceList.Instance;
            if (devices.Count == 0)
            {
                OnError?.Invoke(this, "Npcap이 설치되지 않았거나 네트워크 인터페이스를 찾을 수 없습니다.");
                return false;
            }

            // 인터페이스 선택: 지정된 이름 우선, 없으면 첫 번째
            _device = deviceName != null
                ? devices.FirstOrDefault(d => d.Name == deviceName) ?? devices[0]
                : devices[0];

            // Open: promiscuous mode = false (자신의 트래픽만 캡처)
            // read timeout 1000ms: 패킷 없을 때 1초마다 루프 반환
            _device.Open(DeviceModes.None, 1000);

            // BPF(Berkeley Packet Filter) 설정
            // 아이온2 서버 포트의 TCP 패킷만 캡처 → 불필요한 패킷 처리 제거
            // serverIp가 있으면 더 정확한 필터링 가능
            string filter = serverIp != null
                ? $"tcp and host {serverIp} and port {AION2_PORT}"
                : $"tcp and port {AION2_PORT}";

            _device.Filter = filter;
            _device.OnPacketArrival += OnPacketArrival;

            // 비동기 캡처 시작 (별도 스레드에서 실행됨)
            _device.StartCapture();
            _isCapturing = true;
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"캡처 시작 실패: {ex.Message}\n관리자 권한으로 실행했는지 확인하세요.");
            return false;
        }
    }

    public void Stop()
    {
        if (!_isCapturing) return;
        try
        {
            _device?.StopCapture();
            _device?.Close();
        }
        catch { /* 종료 시 에러는 무시 */ }
        finally
        {
            _isCapturing = false;
        }
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            // PacketDotNet으로 레이어 파싱
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
            var tcpPacket = packet.Extract<TcpPacket>();

            if (tcpPacket?.PayloadData == null || tcpPacket.PayloadData.Length == 0)
                return;

            // 게임 서버 → 클라이언트 방향 패킷만 처리 (서버포트가 source)
            if (tcpPacket.SourcePort == AION2_PORT)
            {
                _parser.ParsePacket(tcpPacket.PayloadData);
            }
        }
        catch
        {
            // 개별 패킷 파싱 실패는 무시 (캡처 중단 방지)
        }
    }

    public void Dispose()
    {
        Stop();
        _device?.Dispose();
    }
}
