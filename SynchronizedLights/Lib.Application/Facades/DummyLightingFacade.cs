using Lib.Application.Interfaces;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Serilog;
using System.Diagnostics;
using static SynchrolightAPI.Domain.Target;

namespace Lib.Application.Facades;

/// <summary>
/// Dummy実装
/// 概要：SynchrolightAPIに接続する前の開発用実装。
/// Debug.WriteLine でログ出力のみ行い、内部状態を保持する。
/// </summary>
public class DummyLightingFacade : ILightingFacade
{
    private readonly List<string> _connectedPorts = new();
    private string? _lastError;
    private int _queueLength;

    public bool IsConnected => _connectedPorts.Count > 0;
    public IReadOnlyList<string> ConnectedPorts => _connectedPorts.AsReadOnly();
    public int QueueLength => _queueLength;
    public string? LastError => _lastError;

    public event EventHandler? StatusChanged;

    #region 接続管理

    public Task<IReadOnlyList<string>> GetAvailablePortsAsync()
    {
        IReadOnlyList<string> ports = new[] { "COM3 (Dummy)", "COM4 (Dummy)", "COM5 (Dummy)" };
        Log.Information("[Dummy] GetAvailablePorts: {Ports}", string.Join(", ", ports));

        return Task.FromResult(ports);
    }

    public Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
    {
        _connectedPorts.Clear();
        _connectedPorts.AddRange(portNames);
        _lastError = null;
        Log.Information("[Dummy] Connect: {Ports}", string.Join(", ", _connectedPorts));
        RaiseStatusChanged();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _connectedPorts.Clear();
        Log.Information("[Dummy] Disconnect: all ports closed");

        RaiseStatusChanged();
        return Task.CompletedTask;
    }

    public Task InitializeTransmitterAsync(byte channel, byte power, CancellationToken ct = default)
    {
        // 未接続時は「COM未接続」エラーを発火
        if (!IsConnected)
        {
            _lastError = "発信機未接続 (ポートが接続されていません)";
            Log.Error("[Dummy] InitializeTransmitter failed: {Error}", _lastError);
            RaiseStatusChanged();
            throw new InvalidOperationException(_lastError);
        }

        Log.Information("[Dummy] InitializeTransmitter: channel={Channel}, power={Power}", channel, power);
        _lastError = null;
        RaiseStatusChanged();
        return Task.CompletedTask;
    }

    #endregion 接続管理

    #region 状態
    public void ClearError()
    {
        _lastError = null;
        RaiseStatusChanged();
    }
    #endregion 状態

    #region 即時制御

    public Task SetColorAsync(Target target, Rgb color, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            _lastError = $"送信失敗: 未接続状態で SetColor が呼ばれました";
            RaiseStatusChanged();
            throw new InvalidOperationException(_lastError);
        }
        Log.Information("[Dummy] SetColor target={Target} color=({R},{G},{B})", target, color.R, color.G, color.B);
        SimulateSend();
        return Task.CompletedTask;
    }

    #endregion 即時制御

    #region 演出
    public Task FlashAsync(Target target, int speedMs, Rgb color, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            _lastError = $"送信失敗: 未接続状態で Flash が呼ばれました";
            RaiseStatusChanged();
            throw new InvalidOperationException(_lastError);
        }
        Log.Information("[Dummy] Flash target={Target} speed={Speed}ms color=({R},{G},{B})", target, speedMs, color.R, color.G, color.B);
        SimulateSend();
        return Task.CompletedTask;
    }

    public Task FadeInAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            _lastError = $"送信失敗: 未接続状態で FadeIn が呼ばれました";
            RaiseStatusChanged();
            throw new InvalidOperationException(_lastError);
        }
        Log.Information("[Dummy] FadeIn target={Target} time={Time}ms color=({R},{G},{B})", target, timeMs, color.R, color.G, color.B);
        SimulateSend();
        return Task.CompletedTask;
    }

    public Task FadeOutAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            _lastError = $"送信失敗: 未接続状態で FadeOut が呼ばれました";
            RaiseStatusChanged();
            throw new InvalidOperationException(_lastError);
        }
        Log.Information("[Dummy] FadeOut target={Target} time={Time}ms color=({R},{G},{B})", target, timeMs, color.R, color.G, color.B);
        SimulateSend();
        return Task.CompletedTask;
    }

    #endregion 演出

    #region シーケンス
    public Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default)
    {
        Log.Information("[Dummy] Sequence target={Target} id={Id}", target, sequenceId);
        SimulateSend();
        return Task.CompletedTask;
    }
    #endregion シーケンス

    #region 内部処理
    private void SimulateSend()
    {
        _queueLength++;
        RaiseStatusChanged();
        Task.Run(async () =>
        {
            await Task.Delay(100);
            _queueLength = Math.Max(0, _queueLength - 1);
            RaiseStatusChanged();
        });
    }

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
    #endregion 内部処理

}