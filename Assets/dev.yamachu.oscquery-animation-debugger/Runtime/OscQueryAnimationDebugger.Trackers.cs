using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VRC.OSCQuery;

public enum TrackerSlot
{
    Slot1,
    Slot2,
    Slot3,
    Slot4,
    Slot5,
    Slot6,
    Slot7,
    Slot8,
    Head
}

[Serializable]
public sealed class TrackerBinding
{
    [SerializeField] private TrackerSlot slot;
    [SerializeField] private Transform target;
    [SerializeField] [Tooltip("Experimental: Positionはtracking-space calibrationが必要です。Rotationは引き続きサポートされます。")]
    private bool applyPosition = false;
    [SerializeField] private bool applyRotation = true;

    public TrackerSlot Slot => slot;
    public Transform Target => target;
    public bool ApplyPosition => applyPosition;
    public bool ApplyRotation => applyRotation;
}

public partial class OscQueryAnimationDebugger
{
    private sealed class TrackerPoseState
    {
        public Vector3 Position;
        public Vector3 RotationEuler;
        public bool HasPosition;
        public bool HasRotation;
        public bool PositionDirty;
        public bool RotationDirty;
        public float LastUpdateTime;
    }

    private readonly Dictionary<TrackerSlot, TrackerBinding> _trackerBindingsBySlot = new Dictionary<TrackerSlot, TrackerBinding>();
    private readonly Dictionary<TrackerSlot, TrackerPoseState> _trackerPoseStates = new Dictionary<TrackerSlot, TrackerPoseState>();
    private readonly HashSet<TrackerSlot> _warnedDuplicateTrackerSlots = new HashSet<TrackerSlot>();
    private readonly HashSet<int> _warnedNullTrackerBindingIndices = new HashSet<int>();

    private void RebuildTrackerBindings()
    {
        _trackerBindingsBySlot.Clear();
        var seenSlots = new HashSet<TrackerSlot>();

        if (trackerBindings == null) return;

        for (int i = 0; i < trackerBindings.Count; i++)
        {
            TrackerBinding binding = trackerBindings[i];
            bool isDuplicate = binding != null && !seenSlots.Add(binding.Slot);
            if (isDuplicate && _warnedDuplicateTrackerSlots.Add(binding.Slot))
                Debug.LogWarning($"[OSCQuery Animation Debugger] Tracker slot '{GetTrackerSlotPath(binding.Slot)}' が重複しています。最初の有効な割り当てを使用します。");

            if (binding == null || binding.Target == null)
            {
                if (_warnedNullTrackerBindingIndices.Add(i))
                    Debug.LogWarning($"[OSCQuery Animation Debugger] Tracker binding {i} のTargetが未設定です。割り当てを無視します。");
                continue;
            }

            if (_trackerBindingsBySlot.ContainsKey(binding.Slot)) continue;

            _trackerBindingsBySlot.Add(binding.Slot, binding);
        }
    }

    private bool TryQueueTrackerMessage(ParsedOscMessage message)
    {
        if (!TryParseTrackerPath(message.Address, out TrackerSlot slot, out bool isPosition)) return false;
        if (!enableTrackers) return true;

        if (!TryGetTrackerVector(message.Arguments, out Vector3 value))
        {
            if (verboseReceiveLogging)
                Debug.LogWarning($"[OSCQuery Animation Debugger] Tracker値はfloat/double/int/longの3引数が必要です: {message.Address}");
            return true;
        }

        if (!_trackerPoseStates.TryGetValue(slot, out TrackerPoseState state))
        {
            state = new TrackerPoseState();
            _trackerPoseStates.Add(slot, state);
        }

        if (isPosition)
        {
            state.Position = value;
            state.HasPosition = true;
            state.PositionDirty = true;
        }
        else
        {
            state.RotationEuler = value;
            state.HasRotation = true;
            state.RotationDirty = true;
        }
        state.LastUpdateTime = Time.unscaledTime;
        return true;
    }

    private void ApplyDirtyTrackerPoses()
    {
        if (!enableTrackers) return;

        Transform reference = trackerReferenceTransform != null ? trackerReferenceTransform : transform.root;
        foreach (KeyValuePair<TrackerSlot, TrackerPoseState> entry in _trackerPoseStates)
        {
            TrackerPoseState state = entry.Value;
            if (!state.PositionDirty && !state.RotationDirty) continue;

            bool isStale = trackerStaleTimeoutSeconds > 0f && Time.unscaledTime - state.LastUpdateTime > trackerStaleTimeoutSeconds;
            if (!isStale && _trackerBindingsBySlot.TryGetValue(entry.Key, out TrackerBinding binding) && binding.Target != null)
            {
                if (state.PositionDirty && state.HasPosition && binding.ApplyPosition)
                    binding.Target.position = reference.TransformPoint(state.Position);
                if (state.RotationDirty && state.HasRotation && binding.ApplyRotation)
                    // Unity Quaternion.Euler applies rotations in the required Z-X-Y order.
                    binding.Target.rotation = reference.rotation * Quaternion.Euler(state.RotationEuler);
            }

            state.PositionDirty = false;
            state.RotationDirty = false;
        }
    }

    private void RegisterTrackerEndpoints()
    {
        if (!enableTrackers) return;

        foreach (TrackerSlot slot in Enum.GetValues(typeof(TrackerSlot)))
        {
            string slotPath = GetTrackerSlotPath(slot);
            RegisterTrackerEndpoint($"/tracking/trackers/{slotPath}/position", "Tracker position (meters)");
            RegisterTrackerEndpoint($"/tracking/trackers/{slotPath}/rotation", "Tracker rotation (degrees)");
        }
    }

    private void RegisterTrackerEndpoint(string path, string description)
    {
        try
        {
            if (_oscQueryService.AddEndpoint(path, "fff", Attributes.AccessValues.WriteOnly, description: description))
            {
                _registeredEndpoints.Add(path);
            }
            else
            {
                Debug.LogWarning($"[OSCQuery Animation Debugger] Trackerエンドポイント登録失敗: {path}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OSCQuery Animation Debugger] Trackerエンドポイント登録失敗: {path} - {ex.Message}");
        }
    }

    private static bool TryParseTrackerPath(string path, out TrackerSlot slot, out bool isPosition)
    {
        slot = default;
        isPosition = false;
        const string prefix = "/tracking/trackers/";
        if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.Ordinal)) return false;

        int valueSeparator = path.IndexOf('/', prefix.Length);
        if (valueSeparator < 0) return false;
        string slotName = path.Substring(prefix.Length, valueSeparator - prefix.Length);
        string valueName = path.Substring(valueSeparator + 1);
        if (valueName == "position") isPosition = true;
        else if (valueName != "rotation") return false;

        switch (slotName)
        {
            case "1": slot = TrackerSlot.Slot1; return true;
            case "2": slot = TrackerSlot.Slot2; return true;
            case "3": slot = TrackerSlot.Slot3; return true;
            case "4": slot = TrackerSlot.Slot4; return true;
            case "5": slot = TrackerSlot.Slot5; return true;
            case "6": slot = TrackerSlot.Slot6; return true;
            case "7": slot = TrackerSlot.Slot7; return true;
            case "8": slot = TrackerSlot.Slot8; return true;
            case "head": slot = TrackerSlot.Head; return true;
            default: return false;
        }
    }

    private static bool TryGetTrackerVector(object[] arguments, out Vector3 value)
    {
        value = default;
        if (arguments == null || arguments.Length != 3) return false;
        if (!TryGetNumericValue(arguments[0], out float x) ||
            !TryGetNumericValue(arguments[1], out float y) ||
            !TryGetNumericValue(arguments[2], out float z)) return false;

        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryGetNumericValue(object argument, out float value)
    {
        switch (argument)
        {
            case float floatValue: value = floatValue; return true;
            case double doubleValue: value = (float)doubleValue; return true;
            case int intValue: value = intValue; return true;
            case long longValue: value = longValue; return true;
            default: value = 0f; return false;
        }
    }

    private static string GetTrackerSlotPath(TrackerSlot slot)
    {
        return slot == TrackerSlot.Head
            ? "head"
            : ((int)slot + 1).ToString(CultureInfo.InvariantCulture);
    }
}