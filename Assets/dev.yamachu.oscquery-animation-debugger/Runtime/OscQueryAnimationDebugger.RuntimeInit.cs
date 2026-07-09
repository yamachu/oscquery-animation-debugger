using UnityEngine;
using System;
using System.Reflection;

public partial class OscQueryAnimationDebugger
{
    /// <summary>
    /// Try to activate this component as the primary avatar instance.
    /// Performs checks for hierarchy, excluded keywords, layers, and singleton enforcement.
    /// </summary>
    private bool TryActivatePrimaryAvatarInstance()
    {
        if (!gameObject.activeInHierarchy)
        {
            Debug.Log("[OSCQuery Bridge] 非アクティブなため初期化をスキップします。");
            return false;
        }

        Transform root = transform.root;
        string rootNameLower = root.name.ToLowerInvariant();

        foreach (string keyword in excludedRootNameKeywords)
        {
            if (string.IsNullOrWhiteSpace(keyword)) continue;
            if (rootNameLower.Contains(keyword.ToLowerInvariant()))
            {
                Debug.Log($"[OSCQuery Bridge] 複製アバター({root.name})を検出したため無効化します。");
                return false;
            }
        }

        int mirrorReflectionLayer = LayerMask.NameToLayer("MirrorReflection");
        if (mirrorReflectionLayer >= 0 && root.gameObject.layer == mirrorReflectionLayer)
        {
            Debug.Log($"[OSCQuery Bridge] MirrorReflectionレイヤーのため無効化します: {root.name}");
            return false;
        }

        int rootId = root.GetInstanceID();
        if (s_activeAvatarRootId.HasValue && s_activeAvatarRootId.Value != rootId)
        {
            Debug.Log($"[OSCQuery Bridge] 既に別アバターで起動済みのため無効化します: {root.name}");
            return false;
        }

        s_activeAvatarRootId = rootId;
        _isPrimaryAvatarInstance = true;
        return true;
    }
}
