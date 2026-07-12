using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.OSCQuery;

public enum BlendShapeValueMode
{
    Normalized01,
    UnityWeight
}

public partial class OscQueryAnimationDebugger
{
    private const string BlendshapePrefix = "/blendshape/";

    private sealed class BlendshapeTarget
    {
        public SkinnedMeshRenderer Renderer;
        public int Index;
    }

    private readonly Dictionary<string, List<BlendshapeTarget>> _blendshapeLookup = new Dictionary<string, List<BlendshapeTarget>>(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedMissingBlendshapes = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedBlendshapeEndpointPaths = new HashSet<string>(StringComparer.Ordinal);

    private void RebuildBlendshapeLookup()
    {
        _blendshapeLookup.Clear();
        _warnedMissingBlendshapes.Clear();

        if (blendshapeTargetRenderers == null) return;

        var seenRenderers = new HashSet<SkinnedMeshRenderer>();
        foreach (SkinnedMeshRenderer renderer in blendshapeTargetRenderers)
        {
            if (renderer == null || !seenRenderers.Add(renderer) || renderer.sharedMesh == null) continue;

            Mesh mesh = renderer.sharedMesh;
            for (int index = 0; index < mesh.blendShapeCount; index++)
            {
                string name = mesh.GetBlendShapeName(index);
                if (!_blendshapeLookup.TryGetValue(name, out List<BlendshapeTarget> targets))
                {
                    targets = new List<BlendshapeTarget>();
                    _blendshapeLookup.Add(name, targets);
                }

                targets.Add(new BlendshapeTarget { Renderer = renderer, Index = index });
            }
        }
    }

    private bool TryApplyBlendshapeMessage(ParsedOscMessage message)
    {
        if (message.Address == null || !message.Address.StartsWith(BlendshapePrefix, StringComparison.Ordinal)) return false;
        if (!enableBlendshapes) return true;

        string blendshapeName = message.Address.Substring(BlendshapePrefix.Length);
        if (string.IsNullOrEmpty(blendshapeName)) return true;
        if (message.Arguments == null || message.Arguments.Length != 1 || !TryGetNumericValue(message.Arguments[0], out float value))
        {
            if (verboseReceiveLogging)
                Debug.LogWarning($"[OSCQuery Animation Debugger] BlendShape値はfloat/double/int/longの1引数が必要です: {message.Address}");
            return true;
        }

        if (!_blendshapeLookup.TryGetValue(blendshapeName, out List<BlendshapeTarget> targets))
        {
            if (verboseReceiveLogging && _warnedMissingBlendshapes.Add(blendshapeName))
                Debug.LogWarning($"[OSCQuery Animation Debugger] BlendShape '{blendshapeName}' を設定済みRendererから見つけられませんでした");
            return true;
        }

        float weight = blendshapeValueMode == BlendShapeValueMode.Normalized01 ? value * 100f : value;
        weight = Mathf.Clamp(weight, 0f, 100f);
        foreach (BlendshapeTarget target in targets)
        {
            if (target.Renderer != null)
                target.Renderer.SetBlendShapeWeight(target.Index, weight);
        }

        return true;
    }

    private void RegisterBlendshapeEndpoints()
    {
        if (!enableBlendshapes) return;

        foreach (string blendshapeName in _blendshapeLookup.Keys)
        {
            string path = BlendshapePrefix + blendshapeName;
            try
            {
                if (_oscQueryService.AddEndpoint(path, "f", Attributes.AccessValues.WriteOnly, description: $"BlendShape: {blendshapeName}"))
                    _registeredEndpoints.Add(path);
                else if (_warnedBlendshapeEndpointPaths.Add(path))
                    Debug.LogWarning($"[OSCQuery Animation Debugger] BlendShapeエンドポイント登録失敗: {path}");
            }
            catch (Exception ex)
            {
                if (_warnedBlendshapeEndpointPaths.Add(path))
                    Debug.LogWarning($"[OSCQuery Animation Debugger] BlendShapeエンドポイント登録失敗: {path} - {ex.Message}");
            }
        }
    }
}