using System.Collections.Generic;
using UnityEngine;

public enum ParameterType
{
    Float,
    Int,
    Bool,
    Trigger
}

public enum ParameterAccess
{
    ReadWrite,
    WriteOnly
}

public struct DriverParameterInfo
{
    public string Name;
    public ParameterType Type;
    public ParameterAccess Access;

    public DriverParameterInfo(string name, ParameterType type, ParameterAccess access)
    {
        Name = name;
        Type = type;
        Access = access;
    }
}

public interface IAvatarParameterDriver
{
    string DisplayName { get; }
    bool IsReady { get; }
    bool TryInitialize(OscQueryAnimationDebugger bridge);
    bool TryApplyValue(string paramName, string rawValue);
    IEnumerable<DriverParameterInfo> EnumerateParameters();
    bool TryReadValue(string paramName, out string value);
}
