using System;
using System.Globalization;
using System.Text;
using OscCore;
using UnityEngine;

public partial class OscQueryAnimationDebugger
{
    internal readonly struct ParsedOscMessage
    {
        public ParsedOscMessage(string address, object[] arguments)
        {
            Address = address;
            Arguments = arguments;
        }

        public string Address { get; }
        public object[] Arguments { get; }
    }

    /// <summary>
    /// OSCパケットをパースしてアドレスと全ての型付き引数を返す
    /// </summary>
    internal static bool TryParseOscPacket(byte[] data, out ParsedOscMessage parsedMessage)
    {
        parsedMessage = default;
        if (data == null || data.Length < 4) return false;

        try
        {
            OscPacket packet = OscPacket.Read(data, 0, data.Length, null, null);
            OscMessage message = packet as OscMessage;
            if (message == null) return false;

            var arguments = new object[message.Count];
            for (int i = 0; i < message.Count; i++)
            {
                arguments[i] = message[i];
            }

            parsedMessage = new ParsedOscMessage(message.Address, arguments);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryFormatScalarOscArgument(object argument, out string value)
    {
        value = null;
        if (argument is float floatValue)
        {
            value = floatValue.ToString("G", CultureInfo.InvariantCulture);
            return true;
        }
        if (argument is double doubleValue)
        {
            value = doubleValue.ToString("G", CultureInfo.InvariantCulture);
            return true;
        }
        if (argument is int intValue) { value = intValue.ToString(CultureInfo.InvariantCulture); return true; }
        if (argument is long longValue) { value = longValue.ToString(CultureInfo.InvariantCulture); return true; }
        if (argument is bool boolValue) { value = boolValue ? "True" : "False"; return true; }
        if (argument is string stringValue) { value = stringValue; return true; }
        return false;
    }

    internal static string GetPacketPreview(byte[] data)
    {
        if (data == null || data.Length == 0) return "empty packet";

        int previewLength = Math.Min(data.Length, 32);
        string hex = BitConverter.ToString(data, 0, previewLength);
        string ascii = Encoding.ASCII.GetString(data, 0, previewLength).Replace('\0', '.');
        return $"len={data.Length}, hex={hex}, ascii={ascii}";
    }

    internal static string GetAnimatorParamValueAsString(Animator animator, string paramName, AnimatorControllerParameterType paramType)
    {
        switch (paramType)
        {
            case AnimatorControllerParameterType.Float:
                return animator.GetFloat(paramName).ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            case AnimatorControllerParameterType.Int:
                return animator.GetInteger(paramName).ToString();
            case AnimatorControllerParameterType.Bool:
            case AnimatorControllerParameterType.Trigger:
                return animator.GetBool(paramName) ? "True" : "False";
            default:
                return null;
        }
    }

    internal static byte[] BuildOscOutPacket(string oscPath, Animator animator, string paramName, AnimatorControllerParameterType paramType)
    {
        try
        {
            switch (paramType)
            {
                case AnimatorControllerParameterType.Float:
                    return new OscMessage(oscPath, animator.GetFloat(paramName)).ToByteArray();
                case AnimatorControllerParameterType.Int:
                    return new OscMessage(oscPath, animator.GetInteger(paramName)).ToByteArray();
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return new OscMessage(oscPath, animator.GetBool(paramName)).ToByteArray();
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    internal static string NormalizeBroadcastValue(string rawValue)
    {
        if (rawValue == "1") return "True";
        if (rawValue == "0") return "False";
        return rawValue;
    }

    public static bool TryParseBoolFlexible(string rawValue, out bool value)
    {
        if (bool.TryParse(rawValue, out value)) return true;
        if (rawValue == "1") { value = true; return true; }
        if (rawValue == "0") { value = false; return true; }
        return false;
    }

    public static bool TryParseFloatInvariant(string rawValue, out float value)
    {
        return float.TryParse(
            rawValue,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out value
        );
    }

    public static bool TryParseIntInvariant(string rawValue, out int value)
    {
        return int.TryParse(
            rawValue,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out value
        );
    }
}
