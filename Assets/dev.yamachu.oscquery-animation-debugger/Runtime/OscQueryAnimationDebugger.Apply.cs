using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using OscCore;
using UnityEngine;
using VRC.OSCQuery;

public partial class OscQueryAnimationDebugger
{
    /// <summary>
    /// Animatorパラメーターの変化を検知し、発見済みのリモートOSCサービスへ通知する。
    /// Uses the cached broadcast snapshot (rebuilt in UpdateAnimatorEndpoints)
    /// to avoid per-frame parameter enumeration.
    /// </summary>
    private void BroadcastChangedParameters()
    {
        for (int i = 0; i < _broadcastSnapshot.Count; i++)
        {
            var (driver, paramInfo) = _broadcastSnapshot[i];
            if (!driver.IsReady) continue;

            string oscPath = $"/avatar/parameters/{paramInfo.Name}";

            if (!driver.TryReadValue(paramInfo.Name, out string currentValue))
                continue;

            // Unity Trigger: only send on True transition
            if (paramInfo.Type == ParameterType.Trigger)
            {
                if (currentValue != "True")
                {
                    _lastBroadcastValues[oscPath] = currentValue;
                    continue;
                }
            }

            // Skip if value hasn't changed
            if (_lastBroadcastValues.TryGetValue(oscPath, out string last) && last == currentValue)
                continue;

            _lastBroadcastValues[oscPath] = currentValue;

            if (verboseReceiveLogging)
                Debug.Log($"[OSCQuery Animation Debugger] 変化検知({driver.DisplayName}): {oscPath} = {currentValue}");

            byte[] packet = BuildOscPacketForParameter(oscPath, currentValue, paramInfo.Type);
            if (packet != null)
                SendPacketToAll(oscPath, currentValue, packet);
        }
    }

    private byte[] BuildOscPacketForParameter(string oscPath, string value, ParameterType paramType)
    {
        try
        {
            switch (paramType)
            {
                case ParameterType.Float:
                    if (TryParseFloatInvariant(value, out float fVal))
                        return new OscMessage(oscPath, fVal).ToByteArray();
                    break;
                case ParameterType.Int:
                    if (TryParseIntInvariant(value, out int iVal))
                        return new OscMessage(oscPath, iVal).ToByteArray();
                    break;
                case ParameterType.Bool:
                case ParameterType.Trigger:
                    if (TryParseBoolFlexible(value, out bool bVal))
                        return new OscMessage(oscPath, bVal).ToByteArray();
                    break;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private void SendPacketToAll(string oscPath, string currentValue, byte[] packet)
    {
        foreach (var kvpEndpoint in _remoteOscEndpoints)
        {
            // 自分自身へのループバック送信を防ぐ
            if (string.Equals(kvpEndpoint.Key, serviceName, StringComparison.Ordinal)) continue;

            try
            {
                _oscSendClient.Send(packet, packet.Length, kvpEndpoint.Value);
                if (verboseReceiveLogging)
                    Debug.Log($"[OSCQuery Animation Debugger] OSC送信: {oscPath} = {currentValue} -> {kvpEndpoint.Key}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OSCQuery Animation Debugger] OSC送信失敗 {kvpEndpoint.Key}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// ESP32等のOSCQueryデバイスから値を受信した時のコールバック
    /// Uses driver chain to apply values with first-success semantics.
    /// </summary>
    private void OnOscValueReceived(string oscPath, string rawValue)
    {
        if (!TryExtractParameterName(oscPath, out string paramName))
        {
            if (verboseReceiveLogging)
            {
                Debug.LogWarning($"[OSCQuery Animation Debugger] 未対応のOSCパスを受信: {oscPath}");
            }
            return;
        }

        if (verboseReceiveLogging)
        {
            Debug.Log($"[OSCQuery Animation Debugger] OSC値受信: parameter={paramName}, rawValue={rawValue}");
        }

        // Try each driver in order (first-success semantics)
        bool applied = false;
        foreach (var driver in _drivers)
        {
            if (!driver.IsReady) continue;

            if (driver.TryApplyValue(paramName, rawValue))
            {
                applied = true;
                break; // Stop at first success to match original behavior
            }
        }

        if (!applied && verboseReceiveLogging)
        {
            Debug.LogWarning($"[OSCQuery Animation Debugger] パラメーター '{paramName}' を適用できるドライバーがありませんでした");
        }

        // 受信値をキャッシュしてエコー送信を抑制する（無限ループ防止）
        _lastBroadcastValues[oscPath] = NormalizeBroadcastValue(rawValue);
    }

    private static bool TryExtractParameterName(string oscPath, out string paramName)
    {
        paramName = null;
        if (string.IsNullOrEmpty(oscPath)) return false;
        if (!oscPath.StartsWith(AvatarParameterPrefix, StringComparison.Ordinal)) return false;

        paramName = oscPath.Substring(AvatarParameterPrefix.Length);
        return !string.IsNullOrEmpty(paramName);
    }
}
