namespace Aion2Meter.Models;

public class AppSettings
{
    // ── 화면 ──────────────────────────────────────────────
    public bool   AlwaysOnTop  { get; set; } = true;
    public double Opacity      { get; set; } = 0.9;
    public double WindowLeft   { get; set; } = 100;
    public double WindowTop    { get; set; } = 100;
    public double WindowWidth  { get; set; } = 320;
    public bool   CompactMode  { get; set; } = false;
    public double FontScale    { get; set; } = 1.0;

    // ── 딜 표시 ──────────────────────────────────────────
    /// <summary>
    /// 닉네임 확인된 플레이어만 표시
    /// (ENTITY 패킷 수신 기준 - 파티원 + 닉네임 패킷 받은 레이드 인원)
    /// OFF = 보스를 공격한 모든 공격자 표시
    /// </summary>
    public bool   FilterByKnownPlayers { get; set; } = true;
    public bool   PinLocalPlayer       { get; set; } = false;
    public int    AutoEndSeconds       { get; set; } = 10;
    public string SortBy               { get; set; } = "TotalDamage";

    // ── 캡처 설정 (내부용, UI 미노출) ───────────────────
    public int    AionPort  { get; set; } = 13328;

    // ── 전투 결과 자동 발송 ──────────────────────────────
    /// <summary>자동 발송 ON/OFF (사이트 완성 후 활성화)</summary>
    public bool   AutoUpload      { get; set; } = false;
    public string UploadUrl       { get; set; } = "";
    public string UploadSecretKey { get; set; } = "";
    /// <summary>익명 클라이언트 ID (기기별 고정 UUID)</summary>
    public string ClientId        { get; set; } = Guid.NewGuid().ToString();
}
