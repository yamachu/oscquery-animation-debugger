using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class CustomComponentFieldDriver : IAvatarParameterDriver
{
    public string DisplayName => "Custom Component Fields";
    public bool IsReady { get; private set; }

    private OscQueryAnimationDebugger _bridge;
    private Dictionary<Component, List<FieldInfo>> _customComponentFields = new Dictionary<Component, List<FieldInfo>>();
    private readonly HashSet<string> _loggedMissingCustomComponentTypes = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedMissingCustomComponentInstances = new HashSet<string>(StringComparer.Ordinal);

    public bool TryInitialize(OscQueryAnimationDebugger bridge)
    {
        _bridge = bridge;
        IsReady = true; // Always ready if configured
        return true;
    }

    public bool TryApplyValue(string paramName, string rawValue)
    {
        if (!IsReady) return false;

        foreach (var kvp in _customComponentFields)
        {
            Component component = kvp.Key;
            List<FieldInfo> fields = kvp.Value;

            foreach (FieldInfo field in fields)
            {
                if (field.Name != paramName) continue;

                try
                {
                    if (field.FieldType == typeof(float))
                    {
                        if (OscQueryAnimationDebugger.TryParseFloatInvariant(rawValue, out float fVal))
                        {
                            field.SetValue(component, fVal);
                            if (_bridge.VerboseReceiveLogging)
                            {
                                Debug.Log($"[OSCQuery Bridge] カスタムフィールド適用（Float）: {component.GetType().Name}.{paramName} = {fVal}");
                            }
                            return true;
                        }
                    }
                    else if (field.FieldType == typeof(int))
                    {
                        if (OscQueryAnimationDebugger.TryParseIntInvariant(rawValue, out int iVal))
                        {
                            field.SetValue(component, iVal);
                            if (_bridge.VerboseReceiveLogging)
                            {
                                Debug.Log($"[OSCQuery Bridge] カスタムフィールド適用（Int）: {component.GetType().Name}.{paramName} = {iVal}");
                            }
                            return true;
                        }
                    }
                    else if (field.FieldType == typeof(bool))
                    {
                        if (OscQueryAnimationDebugger.TryParseBoolFlexible(rawValue, out bool bVal))
                        {
                            field.SetValue(component, bVal);
                            if (_bridge.VerboseReceiveLogging)
                            {
                                Debug.Log($"[OSCQuery Bridge] カスタムフィールド適用（Bool）: {component.GetType().Name}.{paramName} = {bVal}");
                            }
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[OSCQuery Bridge] カスタムフィールド値設定失敗: {paramName} - {ex.Message}");
                    return true; // Handled (even if failed)
                }
            }
        }

        return false;
    }

    public IEnumerable<DriverParameterInfo> EnumerateParameters()
    {
        var parameters = new List<DriverParameterInfo>();

        if (_bridge.CustomComponentNamesToExpose.Length == 0)
        {
            return parameters;
        }

        Transform avatarRoot = _bridge.transform.root;
        _customComponentFields.Clear();

        foreach (string componentName in _bridge.CustomComponentNamesToExpose)
        {
            if (string.IsNullOrWhiteSpace(componentName)) continue;

            Type componentType = _bridge.TryFindComponentTypePublic(componentName);
            if (componentType == null)
            {
                if (_loggedMissingCustomComponentTypes.Add(componentName))
                {
                    Debug.LogWarning($"[OSCQuery Bridge] カスタムコンポーネント '{componentName}' が見つかりません。クラス名を確認してください。");
                }
                continue;
            }
            _loggedMissingCustomComponentTypes.Remove(componentName);

            Component component = avatarRoot.GetComponentInChildren(componentType, true);
            if (component == null)
            {
                if (_loggedMissingCustomComponentInstances.Add(componentName))
                {
                    Debug.LogWarning($"[OSCQuery Bridge] '{componentName}' はアセンブリに存在しますが、シーン内に配置されていません");
                }
                continue;
            }
            _loggedMissingCustomComponentInstances.Remove(componentName);

            List<FieldInfo> fields = new List<FieldInfo>();
            componentType = component.GetType();

            FieldInfo[] allFields = componentType.GetFields(
                BindingFlags.Public | BindingFlags.Instance
            );

            foreach (FieldInfo field in allFields)
            {
                if (!IsSupportedFieldType(field.FieldType)) continue;

                fields.Add(field);

                ParameterType paramType;
                if (field.FieldType == typeof(float))
                    paramType = ParameterType.Float;
                else if (field.FieldType == typeof(int))
                    paramType = ParameterType.Int;
                else if (field.FieldType == typeof(bool))
                    paramType = ParameterType.Bool;
                else
                    continue;

                parameters.Add(new DriverParameterInfo(field.Name, paramType, ParameterAccess.ReadWrite));

                if (_bridge.VerboseReceiveLogging)
                {
                    Debug.Log($"[OSCQuery Bridge] カスタムコンポーネントフィールド登録: {field.Name} ({field.FieldType.Name})");
                }
            }

            _customComponentFields[component] = fields;
        }

        return parameters;
    }

    public bool TryReadValue(string paramName, out string value)
    {
        value = null;
        if (!IsReady) return false;

        foreach (var kvp in _customComponentFields)
        {
            Component component = kvp.Key;
            List<FieldInfo> fields = kvp.Value;

            foreach (FieldInfo field in fields)
            {
                if (field.Name != paramName) continue;

                try
                {
                    object rawValue = field.GetValue(component);
                    if (rawValue == null) return false;

                    if (rawValue is float fVal)
                    {
                        value = fVal.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                    else if (rawValue is int iVal)
                    {
                        value = iVal.ToString();
                        return true;
                    }
                    else if (rawValue is bool bVal)
                    {
                        value = bVal ? "True" : "False";
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        return false;
    }

    private bool IsSupportedFieldType(Type fieldType)
    {
        return fieldType == typeof(float) || fieldType == typeof(int) || fieldType == typeof(bool);
    }
}
