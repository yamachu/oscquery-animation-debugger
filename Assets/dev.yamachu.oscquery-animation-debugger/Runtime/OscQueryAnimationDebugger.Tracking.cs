using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using VRC.OSCQuery;

public partial class OscQueryAnimationDebugger
{
    /// <summary>
    /// Scan all parameters from ready drivers and update registered OSCQuery endpoints.
    /// Also rebuilds the broadcast snapshot used by BroadcastChangedParameters
    /// (so per-frame EnumerateParameters calls are avoided).
    /// </summary>
    private void UpdateAnimatorEndpoints()
    {
        if (_oscQueryService == null) return;

        // Clear existing endpoints
        foreach (string endpoint in _registeredEndpoints)
        {
            _oscQueryService.RemoveEndpoint(endpoint);
        }
        _registeredEndpoints.Clear();

        // Collect parameters from all ready drivers
        var allParameters = new Dictionary<string, DriverParameterInfo>(StringComparer.Ordinal);
        _broadcastSnapshot.Clear();

        foreach (var driver in _drivers)
        {
            if (!driver.IsReady) continue;

            foreach (var paramInfo in driver.EnumerateParameters())
            {
                string oscPath = $"/avatar/parameters/{paramInfo.Name}";

                // Skip if already registered by another driver (first-come priority)
                if (allParameters.ContainsKey(paramInfo.Name))
                    continue;

                allParameters[paramInfo.Name] = paramInfo;
                _broadcastSnapshot.Add((driver, paramInfo));

                try
                {
                    RegisterParameterEndpoint(oscPath, paramInfo, driver.DisplayName);
                    _registeredEndpoints.Add(oscPath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OSCQuery Animation Debugger] エンドポイント登録失敗: {oscPath} - {ex.Message}");
                }
            }
        }

        RegisterTrackerEndpoints();
        RegisterBlendshapeEndpoints();

        if (_registeredEndpoints.Count != _lastRegisteredEndpointCount)
        {
            _lastRegisteredEndpointCount = _registeredEndpoints.Count;
            Debug.Log($"[OSCQuery Animation Debugger] {_registeredEndpoints.Count} 個のエンドポイントを登録しました");
        }
        else if (verboseReceiveLogging)
        {
            Debug.Log($"[OSCQuery Animation Debugger] エンドポイント数に変化なし: {_registeredEndpoints.Count}");
        }
    }

    private void RegisterParameterEndpoint(string oscPath, DriverParameterInfo paramInfo, string sourceLabel)
    {
        Attributes.AccessValues access = paramInfo.Access == ParameterAccess.WriteOnly
            ? Attributes.AccessValues.WriteOnly
            : Attributes.AccessValues.ReadWrite;

        string typeDesc;
        switch (paramInfo.Type)
        {
            case ParameterType.Float:
                _oscQueryService.AddEndpoint<float>(oscPath, access, description: $"{sourceLabel} Float: {paramInfo.Name}");
                typeDesc = "Float";
                break;
            case ParameterType.Int:
                _oscQueryService.AddEndpoint<int>(oscPath, access, description: $"{sourceLabel} Int: {paramInfo.Name}");
                typeDesc = "Int";
                break;
            case ParameterType.Bool:
                _oscQueryService.AddEndpoint<bool>(oscPath, access, description: $"{sourceLabel} Bool: {paramInfo.Name}");
                typeDesc = "Bool";
                break;
            case ParameterType.Trigger:
                _oscQueryService.AddEndpoint<bool>(oscPath, access, description: $"{sourceLabel} Trigger: {paramInfo.Name}");
                typeDesc = "Trigger";
                break;
            default:
                return;
        }

        if (verboseReceiveLogging)
        {
            Debug.Log($"[OSCQuery Animation Debugger] エンドポイント登録: {oscPath} ({typeDesc}) source={sourceLabel}");
        }
    }
}
