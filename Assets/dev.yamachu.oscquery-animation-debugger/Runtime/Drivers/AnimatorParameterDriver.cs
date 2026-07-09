using System;
using System.Collections.Generic;
using UnityEngine;
using OscCore;

public class AnimatorParameterDriver : IAvatarParameterDriver
{
    private struct AnimatorParamSource
    {
        public Animator Animator;
        public AnimatorControllerParameterType Type;

        public AnimatorParamSource(Animator animator, AnimatorControllerParameterType type)
        {
            Animator = animator;
            Type = type;
        }
    }

    public string DisplayName => "Animator";
    public bool IsReady { get; private set; }

    private OscQueryAnimationDebugger _bridge;
    private Animator _cachedAnimator;
    private readonly Dictionary<string, List<Animator>> _animatorParamTargets = new Dictionary<string, List<Animator>>(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimatorParamSource> _animatorBroadcastSources = new Dictionary<string, AnimatorParamSource>(StringComparer.Ordinal);
    private readonly List<Animator> _hierarchyAnimators = new List<Animator>();

    public bool TryInitialize(OscQueryAnimationDebugger bridge)
    {
        _bridge = bridge;
        
        Animator primaryAnimator = ResolvePrimaryAnimator();
        if (primaryAnimator != null)
        {
            _cachedAnimator = primaryAnimator;
            IsReady = true;
            return true;
        }

        // Not ready yet, but will retry
        return false;
    }

    public bool TryApplyValue(string paramName, string rawValue)
    {
        if (!IsReady) return false;

        bool applied = false;

        // Try registered animator targets first
        if (_animatorParamTargets.TryGetValue(paramName, out List<Animator> targets) && targets.Count > 0)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                Animator targetAnimator = targets[i];
                if (targetAnimator == null) continue;

                if (ApplyValueToAnimator(targetAnimator, paramName, rawValue))
                {
                    applied = true;
                    if (_bridge.VerboseReceiveLogging)
                    {
                        Debug.Log($"[OSCQuery Bridge] マップ適用成功: parameter={paramName}, animator={targetAnimator.name}, rawValue={rawValue}");
                    }
                }
            }

            if (applied) return true;
        }

        // Fallback to main animator if targets not found
        Animator mainAnimator = ResolvePrimaryAnimator();
        if (mainAnimator != null)
        {
            applied = ApplyValueToAnimator(mainAnimator, paramName, rawValue);
            if (applied) return true;
        }

        // Try hierarchy animators
        for (int i = 0; i < _hierarchyAnimators.Count; i++)
        {
            Animator hierarchyAnimator = _hierarchyAnimators[i];
            if (hierarchyAnimator == null) continue;

            if (ApplyValueToAnimator(hierarchyAnimator, paramName, rawValue))
            {
                applied = true;
                if (_bridge.VerboseReceiveLogging)
                {
                    Debug.Log($"[OSCQuery Bridge] 階層Animatorへ適用: animator={hierarchyAnimator.name}, parameter={paramName}, rawValue={rawValue}");
                }
            }
        }

        return applied;
    }

    public IEnumerable<DriverParameterInfo> EnumerateParameters()
    {
        var parameters = new List<DriverParameterInfo>();
        var registeredNames = new HashSet<string>(StringComparer.Ordinal);

        Animator primaryAnimator = ResolvePrimaryAnimator();
        if (primaryAnimator != null)
        {
            _cachedAnimator = primaryAnimator;
        }

        // Clear and rebuild tracking maps
        _animatorParamTargets.Clear();
        _animatorBroadcastSources.Clear();

        if (primaryAnimator != null)
        {
            CollectAnimatorParameters(primaryAnimator, parameters, registeredNames);
        }

        // Hierarchy animators
        UpdateHierarchyAnimators(primaryAnimator);
        foreach (var hierarchyAnimator in _hierarchyAnimators)
        {
            CollectAnimatorParameters(hierarchyAnimator, parameters, registeredNames);
        }

        return parameters;
    }

    public bool TryReadValue(string paramName, out string value)
    {
        value = null;
        if (!IsReady) return false;

        // Try to resolve broadcast source
        if (_animatorBroadcastSources.TryGetValue(paramName, out AnimatorParamSource source))
        {
            if (!TryResolveAnimatorBroadcastSource(paramName, ref source))
            {
                return false;
            }

            _animatorBroadcastSources[paramName] = source;

            value = OscQueryAnimationDebugger.GetAnimatorParamValueAsString(source.Animator, paramName, source.Type);
            return value != null;
        }

        return false;
    }

    private void CollectAnimatorParameters(Animator animator, List<DriverParameterInfo> parameters, HashSet<string> registeredNames)
    {
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            if (_bridge.VerboseReceiveLogging && animator != null)
            {
                Debug.Log($"[OSCQuery Bridge] Animatorをスキップ(Controllerなし): {animator.name}");
            }
            return;
        }

        for (int i = 0; i < animator.parameterCount; i++)
        {
            AnimatorControllerParameter param = animator.GetParameter(i);
            if (registeredNames.Contains(param.name)) continue;

            ParameterType paramType;
            ParameterAccess access = ParameterAccess.ReadWrite;

            switch (param.type)
            {
                case AnimatorControllerParameterType.Float:
                    paramType = ParameterType.Float;
                    break;
                case AnimatorControllerParameterType.Int:
                    paramType = ParameterType.Int;
                    break;
                case AnimatorControllerParameterType.Bool:
                    paramType = ParameterType.Bool;
                    break;
                case AnimatorControllerParameterType.Trigger:
                    paramType = ParameterType.Trigger;
                    access = ParameterAccess.WriteOnly;
                    break;
                default:
                    continue;
            }

            parameters.Add(new DriverParameterInfo(param.name, paramType, access));
            registeredNames.Add(param.name);

            // Register for apply and broadcast
            RegisterAnimatorParameterTarget(param.name, animator);
            RegisterAnimatorBroadcastSource(param.name, animator, param.type);

            if (_bridge.VerboseReceiveLogging)
            {
                Debug.Log($"[OSCQuery Bridge] Animatorパラメーター登録: {param.name} ({param.type}) source={animator.name}");
            }
        }
    }

    private void UpdateHierarchyAnimators(Animator primaryAnimator)
    {
        _hierarchyAnimators.Clear();
        if (!_bridge.IncludeHierarchyAnimators) return;

        Transform avatarRoot = _bridge.transform.root;
        Animator[] animators = avatarRoot.GetComponentsInChildren<Animator>(true);

        foreach (Animator hierarchyAnimator in animators)
        {
            if (hierarchyAnimator == null || hierarchyAnimator == primaryAnimator) continue;
            _hierarchyAnimators.Add(hierarchyAnimator);
        }
    }

    private void RegisterAnimatorParameterTarget(string paramName, Animator animator)
    {
        if (string.IsNullOrEmpty(paramName) || animator == null) return;

        if (!_animatorParamTargets.TryGetValue(paramName, out List<Animator> targets))
        {
            targets = new List<Animator>();
            _animatorParamTargets[paramName] = targets;
        }

        if (!targets.Contains(animator))
        {
            targets.Add(animator);
        }
    }

    private void RegisterAnimatorBroadcastSource(string paramName, Animator animator, AnimatorControllerParameterType paramType)
    {
        if (string.IsNullOrEmpty(paramName) || animator == null) return;

        if (!_animatorBroadcastSources.ContainsKey(paramName))
        {
            _animatorBroadcastSources[paramName] = new AnimatorParamSource(animator, paramType);
        }
    }

    private bool TryResolveAnimatorBroadcastSource(string paramName, ref AnimatorParamSource source)
    {
        if (source.Animator != null && source.Animator.runtimeAnimatorController != null)
        {
            return true;
        }

        if (!_animatorParamTargets.TryGetValue(paramName, out List<Animator> targets) || targets == null)
        {
            return false;
        }

        for (int t = 0; t < targets.Count; t++)
        {
            Animator candidate = targets[t];
            if (candidate == null || candidate.runtimeAnimatorController == null) continue;

            for (int i = 0; i < candidate.parameterCount; i++)
            {
                AnimatorControllerParameter p = candidate.GetParameter(i);
                if (p.name != paramName) continue;

                source = new AnimatorParamSource(candidate, p.type);
                return true;
            }
        }

        return false;
    }

    private Animator ResolvePrimaryAnimator()
    {
        if (_cachedAnimator != null && _cachedAnimator.runtimeAnimatorController != null)
        {
            return _cachedAnimator;
        }

        // Let Av3RuntimeDriver provide its animator if available
        // For now, search hierarchy
        Animator[] animators = _bridge.transform.root.GetComponentsInChildren<Animator>(true);
        foreach (Animator candidate in animators)
        {
            if (candidate != null && candidate.runtimeAnimatorController != null)
            {
                _cachedAnimator = candidate;
                return candidate;
            }
        }

        return null;
    }

    private bool ApplyValueToAnimator(Animator animator, string paramName, string rawValue)
    {
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            return false;
        }

        bool foundParameter = false;

        for (int i = 0; i < animator.parameterCount; i++)
        {
            AnimatorControllerParameter param = animator.GetParameter(i);

            if (param.name == paramName)
            {
                foundParameter = true;
                switch (param.type)
                {
                    case AnimatorControllerParameterType.Float:
                        if (OscQueryAnimationDebugger.TryParseFloatInvariant(rawValue, out float fVal))
                        {
                            animator.SetFloat(paramName, fVal);
                            if (_bridge.VerboseReceiveLogging)
                                Debug.Log($"[OSCQuery Bridge] Animator Float: {paramName} = {fVal}");
                        }
                        break;

                    case AnimatorControllerParameterType.Int:
                        if (OscQueryAnimationDebugger.TryParseIntInvariant(rawValue, out int iVal))
                        {
                            animator.SetInteger(paramName, iVal);
                            if (_bridge.VerboseReceiveLogging)
                                Debug.Log($"[OSCQuery Bridge] Animator Int: {paramName} = {iVal}");
                        }
                        break;

                    case AnimatorControllerParameterType.Bool:
                        if (OscQueryAnimationDebugger.TryParseBoolFlexible(rawValue, out bool bVal))
                        {
                            animator.SetBool(paramName, bVal);
                            if (_bridge.VerboseReceiveLogging)
                                Debug.Log($"[OSCQuery Bridge] Animator Bool: {paramName} = {bVal}");
                        }
                        break;

                    case AnimatorControllerParameterType.Trigger:
                        animator.SetTrigger(paramName);
                        if (_bridge.VerboseReceiveLogging)
                            Debug.Log($"[OSCQuery Bridge] Animator Trigger: {paramName}");
                        break;
                }
                break;
            }
        }

        if (!foundParameter && _bridge.VerboseReceiveLogging)
        {
            Debug.LogWarning($"[OSCQuery Bridge] Animator parameter が見つかりません: {paramName}");
        }

        return foundParameter;
    }
}
