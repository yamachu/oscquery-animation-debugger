using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public partial class OscQueryAnimationDebugger
{
    private void StartOscReceiver()
    {
        try
        {
            _oscUdpClient = new UdpClient(oscPort);
            _oscSendClient = new UdpClient();
            _oscRunning = true;
            _oscReceiveThread = new Thread(OscReceiveLoop) { IsBackground = true, Name = "OSCReceiveThread" };
            _oscReceiveThread.Start();
            Debug.Log($"[OSCQuery Animation Debugger] OSC UDP 受信開始: udp={oscPort}, tcp={tcpPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[OSCQuery Animation Debugger] OSC UDP 受信の起動に失敗しました: {e.Message}");
        }
    }

    private void OscReceiveLoop()
    {
        var endpoint = new IPEndPoint(IPAddress.Any, 0);
        while (_oscRunning)
        {
            try
            {
                byte[] data = _oscUdpClient.Receive(ref endpoint);
                if (verboseReceiveLogging)
                {
                    Debug.Log($"[OSCQuery Animation Debugger] UDP受信: from={endpoint.Address}:{endpoint.Port}, bytes={data.Length}");
                }

                if (TryParseOscPacket(data, out ParsedOscMessage message))
                {
                    if (verboseReceiveLogging)
                    {
                        Debug.Log($"[OSCQuery Animation Debugger] OSCパース成功: path={message.Address}, args={message.Arguments.Length}, message={string.Join(", ", message.Arguments)}");
                    }

                    lock (_oscQueueLock)
                    {
                        _pendingOscMessages.Enqueue(message);
                    }
                }
                else if (verboseReceiveLogging)
                {
                    Debug.LogWarning($"[OSCQuery Animation Debugger] OSCパース失敗: {GetPacketPreview(data)}");
                }
            }
            catch (SocketException)
            {
                // UdpClient.Close() によって意図的に終了した場合
                break;
            }
            catch (Exception e)
            {
                if (_oscRunning)
                    Debug.LogWarning($"[OSCQuery Animation Debugger] OSC 受信エラー: {e.Message}");
            }
        }
    }

    private void ProcessPendingOscMessages()
    {
        lock (_oscQueueLock)
        {
            while (_pendingOscMessages.Count > 0)
            {
                OnOscMessageReceived(_pendingOscMessages.Dequeue());
            }
        }
    }
}
