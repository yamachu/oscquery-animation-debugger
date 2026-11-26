using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class OscQueryLoggingTests
{
    private readonly List<(LogType Type, string Message)> _written = new List<(LogType, string)>();
    private double _now;
    private OscQueryLogLimiter _limiter;

    [SetUp]
    public void SetUp()
    {
        _now = 0;
        _written.Clear();
        _limiter = new OscQueryLogLimiter(() => _now, (type, message) => _written.Add((type, message)));
    }

    [Test]
    public void VerboseSamplesArePerCategoryAndSummarizedAfterTrafficStops()
    {
        Assert.That(_limiter.ShouldLogVerbose("UDP受信", LogType.Log), Is.True);
        for (int i = 0; i < 999; i++)
            Assert.That(_limiter.ShouldLogVerbose("UDP受信", LogType.Log), Is.False);
        Assert.That(_limiter.ShouldLogVerbose("OSC送信", LogType.Log), Is.True);

        _now = OscQueryLogLimiter.IntervalSeconds - 0.01;
        _limiter.Flush();
        Assert.That(_written, Is.Empty);

        _now = OscQueryLogLimiter.IntervalSeconds;
        _limiter.Flush();
        Assert.That(_written.Count, Is.EqualTo(1));
        Assert.That(_written[0].Type, Is.EqualTo(LogType.Log));
        Assert.That(_written[0].Message, Does.Contain("UDP受信").And.Contain("999 件"));
        Assert.That(_limiter.ShouldLogVerbose("UDP受信", LogType.Log), Is.True);
    }

    [Test]
    public void RepeatedWarningsAndErrorsRetainTheirFirstMessageAndSeverity()
    {
        _limiter.LogRepeated(LogType.Warning, "warning A");
        _limiter.LogRepeated(LogType.Warning, "warning A");
        _limiter.LogRepeated(LogType.Warning, "warning A");
        _limiter.LogRepeated(LogType.Warning, "warning B");
        _limiter.LogRepeated(LogType.Error, "error A");
        _limiter.LogRepeated(LogType.Error, "error A");

        Assert.That(_written.Select(item => item.Message), Is.EqualTo(new[] { "warning A", "warning B", "error A" }));
        _now = OscQueryLogLimiter.IntervalSeconds;
        _limiter.Flush();

        Assert.That(_written.Count, Is.EqualTo(5));
        Assert.That(_written.Any(item => item.Type == LogType.Warning && item.Message.Contains("warning A") && item.Message.Contains("2 件")), Is.True);
        Assert.That(_written.Any(item => item.Type == LogType.Error && item.Message.Contains("error A") && item.Message.Contains("1 件")), Is.True);
    }

    [Test]
    public void DistinctMessageOverflowIsBoundedAndDoesNotHideOtherSeverities()
    {
        for (int i = 0; i < 100; i++)
            _limiter.LogRepeated(LogType.Log, $"info {i}");

        _limiter.LogRepeated(LogType.Warning, "important warning");
        for (int i = 0; i < 40; i++)
            _limiter.LogRepeated(LogType.Warning, $"warning {i}");

        Assert.That(_written.Count(item => item.Type == LogType.Log), Is.EqualTo(OscQueryLogLimiter.MaxRepeatedMessages + 1));
        Assert.That(_written.Any(item => item.Type == LogType.Warning && item.Message == "important warning"), Is.True);
        Assert.That(_written.Count(item => item.Type == LogType.Warning), Is.EqualTo(OscQueryLogLimiter.MaxRepeatedMessages + 1));

        _now = OscQueryLogLimiter.IntervalSeconds;
        _limiter.Flush();
        Assert.That(_written.Any(item => item.Type == LogType.Log && item.Message.Contains("67 件")), Is.True);
        Assert.That(_written.Any(item => item.Type == LogType.Warning && item.Message.Contains("8 件")), Is.True);

        int before = _written.Count;
        _limiter.LogRepeated(LogType.Warning, "new warning");
        Assert.That(_written.Count, Is.EqualTo(before + 1));
        Assert.That(_written.Last().Message, Is.EqualTo("new warning"));
    }

    [Test]
    public void ForcedFlushReportsPendingCountsOnlyOnce()
    {
        _limiter.ShouldLogVerbose("UDP受信", LogType.Log);
        _limiter.ShouldLogVerbose("UDP受信", LogType.Log);
        _limiter.LogRepeated(LogType.Warning, "warning A");
        _limiter.LogRepeated(LogType.Warning, "warning A");

        _limiter.Flush(true);
        Assert.That(_written.Count, Is.EqualTo(3));
        Assert.That(_written.Count(item => item.Message.Contains("1 件")), Is.EqualTo(2));
        _limiter.Flush(true);
        Assert.That(_written.Count, Is.EqualTo(3));
        Assert.That(_limiter.ShouldLogVerbose("UDP受信", LogType.Log), Is.True);
    }

    [Test]
    public void ConcurrentVerboseEventsAreCountedExactly()
    {
        int samples = 0;
        Parallel.For(0, 10000, _ =>
        {
            if (_limiter.ShouldLogVerbose("UDP受信", LogType.Log))
                Interlocked.Increment(ref samples);
        });

        _now = OscQueryLogLimiter.IntervalSeconds;
        _limiter.Flush();
        Assert.That(samples, Is.EqualTo(1));
        Assert.That(_written.Count, Is.EqualTo(1));
        Assert.That(_written[0].Message, Does.Contain("9999 件"));
    }

    [Test]
    public void RepeatedGestureManagerReadFailuresAreVisibleWithVerboseLoggingOff()
    {
        var messages = new List<string>();
        var messagesLock = new object();
        Application.LogCallback callback = (message, stackTrace, type) =>
        {
            if (type == LogType.Warning && message.Contains("GM read failed"))
            {
                lock (messagesLock) messages.Add(message);
            }
        };
        Application.logMessageReceivedThreaded += callback;
        var gameObject = new GameObject("OSC warning test");
        try
        {
            var debugger = gameObject.AddComponent<OscQueryAnimationDebugger>();
            var module = new ThrowingGestureModule();
            var driver = new GestureManagerDriver();
            SetDriverField(driver, "_bridge", debugger);
            SetDriverField(driver, "_moduleInstance", module);
            SetDriverField(driver, "_paramsField", typeof(ThrowingGestureModule).GetField("Params"));
            SetDriverField(driver, "_vrc3ParamTypeField", typeof(ThrowingGestureParameter).GetField("Type"));
            SetDriverField(driver, "_floatValueMethod", typeof(ThrowingGestureParameter).GetMethod("FloatValue"));
            typeof(GestureManagerDriver).GetProperty("IsReady").GetSetMethod(true).Invoke(driver, new object[] { true });

            Assert.That(debugger.VerboseReceiveLogging, Is.False);
            for (int i = 0; i < 60; i++)
                Assert.That(driver.TryReadValue("Broken", out _), Is.False);
            GetMethod("OnDisable").Invoke(debugger, null);

            lock (messagesLock)
            {
                Assert.That(messages.Count, Is.EqualTo(2));
                Assert.That(messages[0], Does.Contain("GM read failed for Broken"));
                Assert.That(messages[1], Does.Contain("59 件"));
            }
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
            Application.logMessageReceivedThreaded -= callback;
        }
    }

    [Test]
    public void OscReceptionContinuesWhenVerboseLoggingIsToggledAndSampled()
    {
        var messages = new List<string>();
        var messagesLock = new object();
        Application.LogCallback callback = (message, stackTrace, type) =>
        {
            if (!message.StartsWith("[OSCQuery Animation Debugger]", StringComparison.Ordinal)) return;
            lock (messagesLock) messages.Add(message);
        };
        Application.logMessageReceivedThreaded += callback;

        var gameObject = new GameObject("OSC logging test");
        OscQueryAnimationDebugger debugger = null;
        try
        {
            debugger = gameObject.AddComponent<OscQueryAnimationDebugger>();
            FieldInfo portField = GetField("oscPort");
            FieldInfo verboseField = GetField("verboseReceiveLogging");
            int port;
            using (var socket = new UdpClient(0))
                port = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
            portField.SetValue(debugger, port);

            GetMethod("StartOscReceiver").Invoke(debugger, null);
            Assert.That((bool)GetField("_oscRunning").GetValue(debugger), Is.True);
            byte[] packet = CreateOscPacket();
            using (var sender = new UdpClient())
            {
                for (int i = 0; i < 6; i++)
                    sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
                WaitForPendingMessages(debugger, 6);
                lock (messagesLock)
                    Assert.That(messages.Any(message => message.Contains("UDP受信: from=")), Is.False);

                verboseField.SetValue(debugger, true);
                for (int i = 0; i < 6; i++)
                    sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
                WaitForPendingMessages(debugger, 12);
            }

            lock (messagesLock)
            {
                Assert.That(messages.Count(message => message.Contains("UDP受信: from=")), Is.EqualTo(1));
                Assert.That(messages.Count(message => message.Contains("OSCパース成功: path=")), Is.EqualTo(1));
            }

            verboseField.SetValue(debugger, false);
            GetMethod("ProcessPendingOscMessages").Invoke(debugger, null);
            Assert.That(PendingMessageCount(debugger), Is.Zero);
            GetMethod("OnDisable").Invoke(debugger, null);
            GetMethod("OnDestroy").Invoke(debugger, null);
            Object.DestroyImmediate(gameObject);
            gameObject = null;

            lock (messagesLock)
            {
                Assert.That(messages.Any(message => message.Contains("UDP受信: 詳細ログを 5 件")), Is.True);
                Assert.That(messages.Any(message => message.Contains("OSCパース成功: 詳細ログを 5 件")), Is.True);
            }
        }
        finally
        {
            if (gameObject != null)
            {
                if (debugger != null) GetMethod("OnDestroy").Invoke(debugger, null);
                Object.DestroyImmediate(gameObject);
            }
            Application.logMessageReceivedThreaded -= callback;
        }
    }

    private static FieldInfo GetField(string name)
    {
        return typeof(OscQueryAnimationDebugger).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static void SetDriverField(GestureManagerDriver driver, string name, object value)
    {
        typeof(GestureManagerDriver).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(driver, value);
    }

    private static MethodInfo GetMethod(string name)
    {
        return typeof(OscQueryAnimationDebugger).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static int PendingMessageCount(OscQueryAnimationDebugger debugger)
    {
        object queueLock = GetField("_oscQueueLock").GetValue(debugger);
        lock (queueLock)
            return ((ICollection)GetField("_pendingOscMessages").GetValue(debugger)).Count;
    }

    private static void WaitForPendingMessages(OscQueryAnimationDebugger debugger, int count)
    {
        var timer = Stopwatch.StartNew();
        while (PendingMessageCount(debugger) < count && timer.ElapsedMilliseconds < 3000)
            Thread.Sleep(10);
        Assert.That(PendingMessageCount(debugger), Is.EqualTo(count), "OSC packets were not received");
    }

    private static byte[] CreateOscPacket()
    {
        using (var stream = new MemoryStream())
        {
            WriteOscString(stream, "/avatar/parameters/Test");
            WriteOscString(stream, ",i");
            stream.Write(new byte[] { 0, 0, 0, 1 }, 0, 4);
            return stream.ToArray();
        }
    }

    private static void WriteOscString(Stream stream, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        int padding = 4 - bytes.Length % 4;
        for (int i = 0; i < padding; i++) stream.WriteByte(0);
    }

    private sealed class ThrowingGestureModule
    {
        public IDictionary Params = new Hashtable { { "Broken", new ThrowingGestureParameter() } };
    }

    private sealed class ThrowingGestureParameter
    {
        public AnimatorControllerParameterType Type = AnimatorControllerParameterType.Float;

        public float FloatValue()
        {
            throw new InvalidOperationException("test failure");
        }
    }
}
