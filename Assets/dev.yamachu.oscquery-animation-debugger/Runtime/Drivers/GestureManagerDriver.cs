using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

public class GestureManagerDriver : IAvatarParameterDriver
{
    public string DisplayName => "Gesture Manager";
    public bool IsReady { get; private set; }

    private OscQueryAnimationDebugger _bridge;
    private object _moduleInstance;
    private Type _gestureManagerType;
    private Type _moduleVrc3Type;
    private FieldInfo _paramsField;
    private MethodInfo _setMethod;
    private MethodInfo _floatValueMethod;
    private FieldInfo _vrc3ParamTypeField;
    private bool _typeResolutionAttempted;
    private bool _oscPortConflictWarned;

    public bool TryInitialize(OscQueryAnimationDebugger bridge)
    {
        _bridge = bridge;

        // Lazy type resolution (only once)
        if (!_typeResolutionAttempted)
        {
            _typeResolutionAttempted = true;

            _gestureManagerType = ResolveTypeFromLoadedAssemblies("BlackStartX.GestureManager.GestureManager");
            if (_gestureManagerType == null)
            {
                if (_bridge.VerboseReceiveLogging)
                {
                    Debug.Log("[OSCQuery Bridge] GestureManager type not found (GM not installed).");
                }
                return false;
            }

            _moduleVrc3Type = ResolveTypeFromLoadedAssemblies("BlackStartX.GestureManager.Editor.Modules.Vrc3.ModuleVrc3");
            if (_moduleVrc3Type == null)
            {
                Debug.LogWarning("[OSCQuery Bridge] ModuleVrc3 type not found.");
                return false;
            }

            Type vrc3ParamType = ResolveTypeFromLoadedAssemblies("BlackStartX.GestureManager.Editor.Modules.Vrc3.Params.Vrc3Param");
            if (vrc3ParamType != null)
            {
                _vrc3ParamTypeField = vrc3ParamType.GetField("Type", BindingFlags.Public | BindingFlags.Instance);
                _floatValueMethod = vrc3ParamType.GetMethod("FloatValue", BindingFlags.Public | BindingFlags.Instance);

                // Find Set method: prefer Set(ModuleVrc3, float, object)
                _setMethod = vrc3ParamType.GetMethod("Set", new Type[] { _moduleVrc3Type, typeof(float), typeof(object) });
                if (_setMethod == null)
                {
                    // Fallback: scan for Set with compatible signature
                    MethodInfo[] methods = vrc3ParamType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    foreach (MethodInfo method in methods)
                    {
                        if (method.Name == "Set")
                        {
                            ParameterInfo[] parameters = method.GetParameters();
                            if (parameters.Length >= 2 && parameters.Length <= 3)
                            {
                                if (parameters[0].ParameterType.IsAssignableFrom(_moduleVrc3Type) && parameters[1].ParameterType == typeof(float))
                                {
                                    _setMethod = method;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _paramsField = _moduleVrc3Type.GetField("Params", flags);

            Debug.Log("[OSCQuery Bridge] Gesture Manager types resolved.");
        }

        // Try to find GM instance controlling our avatar
        if (_moduleInstance == null && _gestureManagerType != null)
        {
            _moduleInstance = FindGestureManagerModuleForAvatar();
            if (_moduleInstance != null)
            {
                Debug.Log("[OSCQuery Bridge] Gesture Manager (ModuleVrc3) を検知し、アバターに接続しました。");
                IsReady = true;

                // Check for OSC port conflict
                if (!_oscPortConflictWarned && _bridge.OscPort == 9000)
                {
                    Debug.LogWarning("[OSCQuery Bridge] OSC port 9000 is in use. If Gesture Manager's built-in OSC module (GM 3.8+) is enabled, this may cause conflicts.");
                    _oscPortConflictWarned = true;
                }

                return true;
            }
        }

        return _moduleInstance != null;
    }

    public bool TryApplyValue(string paramName, string rawValue)
    {
        if (!IsReady || _moduleInstance == null) return false;

        // Get Params dictionary
        IDictionary paramsDict = GetParamsDictionary();
        if (paramsDict == null || !paramsDict.Contains(paramName)) return false;

        object paramObj = paramsDict[paramName];
        if (paramObj == null) return false;

        // Read parameter type
        if (_vrc3ParamTypeField == null) return false;
        object typeObj = _vrc3ParamTypeField.GetValue(paramObj);
        if (!(typeObj is AnimatorControllerParameterType paramType)) return false;

        // Convert rawValue to float based on type
        float floatValue;
        switch (paramType)
        {
            case AnimatorControllerParameterType.Float:
                if (!OscQueryAnimationDebugger.TryParseFloatInvariant(rawValue, out floatValue))
                    return false;
                break;

            case AnimatorControllerParameterType.Int:
                if (!OscQueryAnimationDebugger.TryParseIntInvariant(rawValue, out int intValue))
                    return false;
                floatValue = (float)intValue;
                break;

            case AnimatorControllerParameterType.Bool:
            case AnimatorControllerParameterType.Trigger:
                if (!OscQueryAnimationDebugger.TryParseBoolFlexible(rawValue, out bool boolValue))
                    return false;
                floatValue = boolValue ? 1f : 0f;
                break;

            default:
                return false;
        }

        // Invoke Set method
        if (_setMethod == null) return false;

        try
        {
            _setMethod.Invoke(paramObj, new object[] { _moduleInstance, floatValue, null });

            if (_bridge.VerboseReceiveLogging)
            {
                Debug.Log($"[OSCQuery Bridge] GM param applied: {paramName} = {rawValue} (float: {floatValue})");
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OSCQuery Bridge] GM Set failed for {paramName}: {ex.Message}");
            return false;
        }
    }

    public IEnumerable<DriverParameterInfo> EnumerateParameters()
    {
        var parameters = new List<DriverParameterInfo>();

        if (!IsReady || _moduleInstance == null) return parameters;

        // Check if module's avatar is still valid
        if (!IsModuleStillValid())
        {
            // Clear the stale module so a later TryInitialize can rebind.
            _moduleInstance = null;
            IsReady = false;
            return parameters;
        }

        IDictionary paramsDict = GetParamsDictionary();
        if (paramsDict == null) return parameters;

        try
        {
            foreach (DictionaryEntry entry in paramsDict)
            {
                string paramName = entry.Key as string;
                if (string.IsNullOrEmpty(paramName)) continue;

                object paramObj = entry.Value;
                if (paramObj == null) continue;

                // Read parameter type
                if (_vrc3ParamTypeField == null) continue;
                object typeObj = _vrc3ParamTypeField.GetValue(paramObj);
                if (!(typeObj is AnimatorControllerParameterType paramType)) continue;

                // Map to ParameterType and ParameterAccess
                ParameterType driverType;
                ParameterAccess access;

                switch (paramType)
                {
                    case AnimatorControllerParameterType.Float:
                        driverType = ParameterType.Float;
                        access = ParameterAccess.ReadWrite;
                        break;

                    case AnimatorControllerParameterType.Int:
                        driverType = ParameterType.Int;
                        access = ParameterAccess.ReadWrite;
                        break;

                    case AnimatorControllerParameterType.Bool:
                        driverType = ParameterType.Bool;
                        access = ParameterAccess.ReadWrite;
                        break;

                    case AnimatorControllerParameterType.Trigger:
                        driverType = ParameterType.Trigger;
                        access = ParameterAccess.WriteOnly;
                        break;

                    default:
                        continue; // Skip unknown types
                }

                parameters.Add(new DriverParameterInfo(paramName, driverType, access));
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OSCQuery Bridge] GM parameter enumeration failed: {ex.Message}");
            IsReady = false;
            return new List<DriverParameterInfo>();
        }

        return parameters;
    }

    public bool TryReadValue(string paramName, out string value)
    {
        value = null;
        if (!IsReady || _moduleInstance == null) return false;

        IDictionary paramsDict = GetParamsDictionary();
        if (paramsDict == null || !paramsDict.Contains(paramName)) return false;

        object paramObj = paramsDict[paramName];
        if (paramObj == null) return false;

        try
        {
            // Read parameter type
            if (_vrc3ParamTypeField == null) return false;
            object typeObj = _vrc3ParamTypeField.GetValue(paramObj);
            if (!(typeObj is AnimatorControllerParameterType paramType)) return false;

            // Get float value
            if (_floatValueMethod == null) return false;
            object floatObj = _floatValueMethod.Invoke(paramObj, null);
            if (!(floatObj is float floatValue)) return false;

            // Format based on type
            switch (paramType)
            {
                case AnimatorControllerParameterType.Float:
                    value = floatValue.ToString("G", CultureInfo.InvariantCulture);
                    break;

                case AnimatorControllerParameterType.Int:
                    value = ((int)Math.Round(floatValue)).ToString();
                    break;

                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    value = (floatValue != 0f) ? "True" : "False";
                    break;

                default:
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OSCQuery Bridge] GM read failed for {paramName}: {ex.Message}");
            return false;
        }
    }

    private object FindGestureManagerModuleForAvatar()
    {
        if (_gestureManagerType == null || _moduleVrc3Type == null) return null;

        UnityEngine.Object[] gmInstances = UnityEngine.Object.FindObjectsOfType(_gestureManagerType);
        if (gmInstances == null || gmInstances.Length == 0) return null;

        Transform myRoot = _bridge.transform.root;
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (UnityEngine.Object gmObj in gmInstances)
        {
            // Get Module from GestureManager component
            object moduleObj = TryGetMemberValue(gmObj, "Module", flags);
            if (moduleObj == null) continue;

            // Check if module is ModuleVrc3
            if (!_moduleVrc3Type.IsInstanceOfType(moduleObj)) continue;

            // Get Avatar from module (search up type hierarchy)
            GameObject avatar = GetModuleAvatar(moduleObj);
            if (avatar == null) continue;

            // Check if avatar root matches our root
            if (avatar.transform.root == myRoot)
            {
                return moduleObj;
            }
        }

        return null;
    }

    private GameObject GetModuleAvatar(object moduleObj)
    {
        if (moduleObj == null) return null;

        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type currentType = moduleObj.GetType();

        // Search up the type hierarchy for "Avatar" property or field
        while (currentType != null)
        {
            object avatarObj = TryGetMemberValue(moduleObj, "Avatar", flags, currentType);
            if (avatarObj is GameObject go)
            {
                return go;
            }

            currentType = currentType.BaseType;
        }

        return null;
    }

    private object TryGetMemberValue(object obj, string memberName, BindingFlags flags, Type typeToSearch = null)
    {
        if (obj == null) return null;

        Type searchType = typeToSearch ?? obj.GetType();

        // Try property first
        PropertyInfo prop = searchType.GetProperty(memberName, flags);
        if (prop != null && prop.CanRead)
        {
            try
            {
                return prop.GetValue(obj, null);
            }
            catch
            {
                // Ignore
            }
        }

        // Try field
        FieldInfo field = searchType.GetField(memberName, flags);
        if (field != null)
        {
            try
            {
                return field.GetValue(obj);
            }
            catch
            {
                // Ignore
            }
        }

        return null;
    }

    private IDictionary GetParamsDictionary()
    {
        if (_moduleInstance == null || _paramsField == null) return null;

        try
        {
            object paramsObj = _paramsField.GetValue(_moduleInstance);
            return paramsObj as IDictionary;
        }
        catch
        {
            return null;
        }
    }

    private bool IsModuleStillValid()
    {
        if (_moduleInstance == null) return false;

        try
        {
            GameObject avatar = GetModuleAvatar(_moduleInstance);
            if (avatar == null) return false;

            Transform myRoot = _bridge.transform.root;
            return avatar.transform.root == myRoot;
        }
        catch
        {
            return false;
        }
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
}
