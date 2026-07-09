using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using OscCore;

public class LyumaAv3RuntimeDriver : IAvatarParameterDriver
{
    public string DisplayName => "Lyuma Av3Emulator";
    public bool IsReady { get; private set; }

    private OscQueryAnimationDebugger _bridge;
    private object _av3RuntimeInstance;
    private Type _av3RuntimeType;
    private FieldInfo _av3AnimatorField;
    private FieldInfo _av3FloatsField;
    private FieldInfo _av3IntsField;
    private FieldInfo _av3BoolsField;
    private FieldInfo _av3FloatToIndexField;
    private FieldInfo _av3IntToIndexField;
    private FieldInfo _av3BoolToIndexField;
    private FieldInfo _av3IsLocalField;
    private FieldInfo _av3IsMirrorCloneField;
    private FieldInfo _av3IsShadowCloneField;
    private readonly Dictionary<Type, FieldInfo> _runtimeParamValueFieldCache = new Dictionary<Type, FieldInfo>();
    private readonly HashSet<Type> _runtimeParamValueFieldMissingTypes = new HashSet<Type>();

    public bool TryInitialize(OscQueryAnimationDebugger bridge)
    {
        _bridge = bridge;

        // Initialize reflection
        if (_av3RuntimeType == null)
        {
            _av3RuntimeType = ResolveTypeFromLoadedAssemblies("Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime");

            if (_av3RuntimeType != null)
            {
                _av3AnimatorField = TryFindAnimatorField(_av3RuntimeType);
                CacheAv3RuntimeParameterFields();

                if (_av3AnimatorField == null)
                {
                    Debug.LogError("[OSCQuery Bridge] LyumaAv3Runtime の animator フィールド/プロパティが見つかりません。");
                }
                else
                {
                    Debug.Log($"[OSCQuery Bridge] Av3Emulatorを検知しました。animator={_av3AnimatorField.Name} をキャッシュしました。");
                }
            }
            else
            {
                Debug.LogWarning("[OSCQuery Bridge] LyumaAv3Runtimeが見つかりません。Av3Emulatorがインストールされているか確認してください。");
                return false;
            }
        }

        // Try to capture runtime instance
        if (_av3RuntimeInstance == null && _av3RuntimeType != null)
        {
            _av3RuntimeInstance = FindRuntimeOnSameAvatarRoot();
            if (_av3RuntimeInstance != null)
            {
                Debug.Log("[OSCQuery Bridge] 実行中のアバター（LyumaAv3Runtime）を捕捉しました。");
                IsReady = true;
                return true;
            }
        }

        return _av3RuntimeInstance != null;
    }

    public bool TryApplyValue(string paramName, string rawValue)
    {
        if (!IsReady || _av3RuntimeInstance == null) return false;

        List<object> runtimes = GetCandidateRuntimesOnSameAvatarRoot();
        if (runtimes.Count == 0) return false;

        bool applied = false;
        bool parsedFloat = OscQueryAnimationDebugger.TryParseFloatInvariant(rawValue, out float fVal);
        bool parsedInt = OscQueryAnimationDebugger.TryParseIntInvariant(rawValue, out int iVal);
        bool parsedBool = OscQueryAnimationDebugger.TryParseBoolFlexible(rawValue, out bool bVal);

        for (int r = 0; r < runtimes.Count; r++)
        {
            object runtime = runtimes[r];

            if (parsedFloat
                && TryGetRuntimeParamIndex(runtime, _av3FloatToIndexField, paramName, out int floatIdx)
                && TrySetRuntimeFloatParam(runtime, floatIdx, fVal))
            {
                applied = true;
                if (_bridge.VerboseReceiveLogging)
                {
                    Debug.Log($"[OSCQuery Bridge] Runtime Float適用: {paramName} = {fVal} target={GetRuntimeName(runtime)}");
                }
            }

            if (parsedInt
                && TryGetRuntimeParamIndex(runtime, _av3IntToIndexField, paramName, out int intIdx)
                && TrySetRuntimeListValue(runtime, _av3IntsField, intIdx, "value", iVal))
            {
                applied = true;
                if (_bridge.VerboseReceiveLogging)
                {
                    Debug.Log($"[OSCQuery Bridge] Runtime Int適用: {paramName} = {iVal} target={GetRuntimeName(runtime)}");
                }
            }

            if (parsedBool
                && TryGetRuntimeParamIndex(runtime, _av3BoolToIndexField, paramName, out int boolIdx)
                && TrySetRuntimeListValue(runtime, _av3BoolsField, boolIdx, "value", bVal))
            {
                applied = true;
                if (_bridge.VerboseReceiveLogging)
                {
                    Debug.Log($"[OSCQuery Bridge] Runtime Bool適用: {paramName} = {bVal} target={GetRuntimeName(runtime)}");
                }
            }
        }

        return applied;
    }

    public IEnumerable<DriverParameterInfo> EnumerateParameters()
    {
        var parameters = new List<DriverParameterInfo>();

        if (_av3RuntimeInstance == null) return parameters;

        RegisterFromIndex(_av3FloatToIndexField, ParameterType.Float, parameters);
        RegisterFromIndex(_av3IntToIndexField, ParameterType.Int, parameters);
        RegisterFromIndex(_av3BoolToIndexField, ParameterType.Bool, parameters);

        return parameters;
    }

    public bool TryReadValue(string paramName, out string value)
    {
        value = null;
        if (!IsReady || _av3RuntimeInstance == null) return false;

        // Use the captured same-root runtime instance directly.
        // (Per-call FindObjectsOfType scans are too expensive for the broadcast path.)
        object runtime = _av3RuntimeInstance;

        // Try float
        if (TryGetRuntimeParamIndex(runtime, _av3FloatToIndexField, paramName, out int floatIdx))
        {
            if (TryGetRuntimeListValue(runtime, _av3FloatsField, floatIdx, out float fVal))
            {
                value = fVal.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
        }

        // Try int
        if (TryGetRuntimeParamIndex(runtime, _av3IntToIndexField, paramName, out int intIdx))
        {
            if (TryGetRuntimeListValue(runtime, _av3IntsField, intIdx, out int iVal))
            {
                value = iVal.ToString();
                return true;
            }
        }

        // Try bool
        if (TryGetRuntimeParamIndex(runtime, _av3BoolToIndexField, paramName, out int boolIdx))
        {
            if (TryGetRuntimeListValue(runtime, _av3BoolsField, boolIdx, out bool bVal))
            {
                value = bVal ? "True" : "False";
                return true;
            }
        }

        return false;
    }

    public Animator GetRuntimeAnimator()
    {
        if (_av3RuntimeInstance != null && _av3AnimatorField != null)
        {
            try
            {
                return (Animator)_av3AnimatorField.GetValue(_av3RuntimeInstance);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OSCQuery Bridge] Runtime animator取得失敗: {ex.Message}");
            }
        }
        return null;
    }

    private void RegisterFromIndex(FieldInfo indexField, ParameterType paramType, List<DriverParameterInfo> parameters)
    {
        if (indexField == null) return;

        object dictObj = indexField.GetValue(_av3RuntimeInstance);
        IDictionary dict = dictObj as IDictionary;
        if (dict == null) return;

        foreach (DictionaryEntry entry in dict)
        {
            string paramName = entry.Key as string;
            if (string.IsNullOrEmpty(paramName)) continue;

            parameters.Add(new DriverParameterInfo(paramName, paramType, ParameterAccess.ReadWrite));

            if (_bridge.VerboseReceiveLogging)
            {
                Debug.Log($"[OSCQuery Bridge] Runtimeパラメーター登録: {paramName} ({paramType})");
            }
        }
    }

    private void CacheAv3RuntimeParameterFields()
    {
        if (_av3RuntimeType == null) return;

        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _av3FloatsField = _av3RuntimeType.GetField("Floats", flags);
        _av3IntsField = _av3RuntimeType.GetField("Ints", flags);
        _av3BoolsField = _av3RuntimeType.GetField("Bools", flags);
        _av3FloatToIndexField = _av3RuntimeType.GetField("FloatToIndex", flags);
        _av3IntToIndexField = _av3RuntimeType.GetField("IntToIndex", flags);
        _av3BoolToIndexField = _av3RuntimeType.GetField("BoolToIndex", flags);
        _av3IsLocalField = _av3RuntimeType.GetField("IsLocal", flags);
        _av3IsMirrorCloneField = _av3RuntimeType.GetField("IsMirrorClone", flags);
        _av3IsShadowCloneField = _av3RuntimeType.GetField("IsShadowClone", flags);
    }

    private object FindRuntimeOnSameAvatarRoot()
    {
        UnityEngine.Object[] runtimes = UnityEngine.Object.FindObjectsOfType(_av3RuntimeType);
        if (runtimes == null || runtimes.Length == 0) return null;

        Transform myRoot = _bridge.transform.root;
        foreach (UnityEngine.Object runtime in runtimes)
        {
            Component runtimeComponent = runtime as Component;
            if (runtimeComponent != null && runtimeComponent.transform.root == myRoot)
            {
                return runtime;
            }
        }

        return null;
    }

    private List<object> GetCandidateRuntimesOnSameAvatarRoot()
    {
        var results = new List<object>();
        if (_av3RuntimeType == null) return results;

        Transform myRoot = _bridge.transform.root;
        UnityEngine.Object[] runtimes = UnityEngine.Object.FindObjectsOfType(_av3RuntimeType);

        for (int i = 0; i < runtimes.Length; i++)
        {
            Component runtimeComponent = runtimes[i] as Component;
            if (runtimeComponent == null || runtimeComponent.transform.root != myRoot) continue;

            object runtime = runtimes[i];
            bool isMirror = TryGetRuntimeBool(runtime, _av3IsMirrorCloneField);
            bool isShadow = TryGetRuntimeBool(runtime, _av3IsShadowCloneField);
            if (isMirror || isShadow) continue;

            results.Add(runtime);
        }

        results.Sort((a, b) =>
        {
            bool aLocal = TryGetRuntimeBool(a, _av3IsLocalField);
            bool bLocal = TryGetRuntimeBool(b, _av3IsLocalField);
            if (aLocal == bLocal) return 0;
            return aLocal ? -1 : 1;
        });

        if (results.Count == 0 && _av3RuntimeInstance != null)
        {
            results.Add(_av3RuntimeInstance);
        }

        return results;
    }

    private bool TryGetRuntimeBool(object runtime, FieldInfo field)
    {
        if (runtime == null || field == null) return false;
        object value = field.GetValue(runtime);
        return value is bool b && b;
    }

    private bool TryGetRuntimeParamIndex(object runtime, FieldInfo indexField, string paramName, out int idx)
    {
        idx = -1;
        if (indexField == null || runtime == null) return false;

        IDictionary dict = indexField.GetValue(runtime) as IDictionary;
        if (dict == null || !dict.Contains(paramName)) return false;

        object value = dict[paramName];
        if (value is int intValue)
        {
            idx = intValue;
            return true;
        }

        return false;
    }

    private bool TrySetRuntimeListValue(object runtime, FieldInfo listField, int index, string memberName, object value)
    {
        if (listField == null || runtime == null) return false;

        IList list = listField.GetValue(runtime) as IList;
        if (list == null || index < 0 || index >= list.Count) return false;

        object paramObj = list[index];
        if (paramObj == null) return false;

        Type paramType = paramObj.GetType();
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        FieldInfo field = paramType.GetField(memberName, flags);
        if (field != null)
        {
            field.SetValue(paramObj, Convert.ChangeType(value, field.FieldType));
            return true;
        }

        PropertyInfo property = paramType.GetProperty(memberName, flags);
        if (property != null && property.CanWrite)
        {
            property.SetValue(paramObj, Convert.ChangeType(value, property.PropertyType), null);
            return true;
        }

        return false;
    }

    private bool TryGetRuntimeListValue<T>(object runtime, FieldInfo listField, int index, out T value)
    {
        value = default(T);
        if (listField == null || runtime == null) return false;

        IList list = listField.GetValue(runtime) as IList;
        if (list == null || index < 0 || index >= list.Count) return false;

        object paramObj = list[index];
        if (paramObj == null) return false;

        FieldInfo vf = GetRuntimeValueField(paramObj.GetType());
        if (vf == null) return false;

        object rawVal = vf.GetValue(paramObj);
        if (rawVal is T typedVal)
        {
            value = typedVal;
            return true;
        }

        try
        {
            value = (T)Convert.ChangeType(rawVal, typeof(T));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySetRuntimeFloatParam(object runtime, int index, float value)
    {
        bool ok = TrySetRuntimeListValue(runtime, _av3FloatsField, index, "value", value);
        ok = TrySetRuntimeListValue(runtime, _av3FloatsField, index, "exportedValue", value) || ok;
        ok = TrySetRuntimeListValue(runtime, _av3FloatsField, index, "expressionValue", value) || ok;
        ok = TrySetRuntimeListValue(runtime, _av3FloatsField, index, "lastExpressionValue_", value) || ok;
        return ok;
    }

    private FieldInfo GetRuntimeValueField(Type paramType)
    {
        if (paramType == null) return null;

        if (_runtimeParamValueFieldCache.TryGetValue(paramType, out FieldInfo cachedField))
        {
            return cachedField;
        }

        if (_runtimeParamValueFieldMissingTypes.Contains(paramType))
        {
            return null;
        }

        FieldInfo valueField = paramType.GetField(
            "value",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        );

        if (valueField != null)
        {
            _runtimeParamValueFieldCache[paramType] = valueField;
            return valueField;
        }

        _runtimeParamValueFieldMissingTypes.Add(paramType);
        return null;
    }

    private string GetRuntimeName(object runtime)
    {
        Component c = runtime as Component;
        return c != null ? c.gameObject.name : "unknown";
    }

    private static Type ResolveTypeFromLoadedAssemblies(string fullTypeName)
    {
        Type direct = Type.GetType(fullTypeName);
        if (direct != null) return direct;

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (Assembly assembly in assemblies)
        {
            Type type = assembly.GetType(fullTypeName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static FieldInfo TryFindAnimatorField(Type runtimeType)
    {
        if (runtimeType == null) return null;

        string[] fieldNameCandidates = { "animator", "_animator", "m_animator" };

        foreach (string fieldName in fieldNameCandidates)
        {
            FieldInfo field = runtimeType.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            );
            if (field != null)
            {
                Debug.Log($"[OSCQuery Bridge] animator フィールド '{fieldName}' を発見しました。");
                return field;
            }
        }

        Debug.LogError($"[OSCQuery Bridge] LyumaAv3Runtime 内に animator フィールドが見つかりません。");
        return null;
    }
}
