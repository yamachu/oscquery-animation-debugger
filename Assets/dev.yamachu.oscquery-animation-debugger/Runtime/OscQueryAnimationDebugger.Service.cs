using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;
using VRC.OSCQuery;

public partial class OscQueryAnimationDebugger
{
    private void TryStartOscQueryService()
    {
        IPAddress localIP = GetLocalIPAddress();

        try
        {
            _oscQueryService = new OSCQueryServiceBuilder()
                .WithServiceName(serviceName)
                .WithTcpPort(tcpPort)
                .WithUdpPort(oscPort)
                .WithHostIP(localIP)  // 実際のマシン IP でリッスン
                .WithDefaults()  // HTTP サーバーを開始し、Zeroconf でアドバタイズを開始
                .Build();

            _oscQueryService.OnOscQueryServiceAdded += OnOscQueryServiceAdded;
            _oscQueryService.OnOscServiceAdded += OnOscServiceAdded;

            Debug.Log($"[OSCQuery Animation Debugger] サービス起動成功: {serviceName} (IP:{localIP} / TCP:{tcpPort} / UDP:{oscPort})");

            RefreshDiscoveredServices();
            _nextDiscoveryRefreshTime = Time.unscaledTime + Mathf.Max(1f, discoveryRefreshIntervalSeconds);
        }
        catch (Exception e)
        {
            Debug.LogError($"[OSCQuery Animation Debugger] サービスの起動に失敗しました: {e.Message}, IP:{localIP} / TCP:{tcpPort} / UDP:{oscPort}");
            Debug.LogException(e);
        }
    }

    /// <summary>
    /// マシンのローカル IP アドレスを取得します。
    /// </summary>
    private static IPAddress GetLocalIPAddress()
    {
        try
        {
            // アクティブなネットワークインターフェースから IP を取得
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in networkInterfaces)
            {
                // ループバック以外で、操作可能なインターフェースを探す
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    ni.OperationalStatus == OperationalStatus.Up)
                {
                    var addresses = ni.GetIPProperties().UnicastAddresses;
                    foreach (var address in addresses)
                    {
                        // IPv4 アドレスを優先
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            return address.Address;
                        }
                    }
                }
            }

            // フォールバック: DNS を使用して取得
            var hostName = Dns.GetHostName();
            var hostEntry = Dns.GetHostEntry(hostName);
            foreach (var ip in hostEntry.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip;
                }
            }

            // 最後のフォールバック
            Debug.LogWarning("[OSCQuery Animation Debugger] ローカル IP アドレスを取得できません。localhost を使用します。");
            return IPAddress.Loopback;
        }
        catch (Exception e)
        {
            Debug.LogError($"[OSCQuery Animation Debugger] IP アドレス取得エラー: {e.Message}");
            return IPAddress.Loopback;
        }
    }

    private void RefreshDiscoveredServices()
    {
        try
        {
            _oscQueryService.RefreshServices();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[OSCQuery Animation Debugger] サービス探索の更新に失敗しました: {e.Message}");
        }
    }

    private void OnOscQueryServiceAdded(OSCQueryServiceProfile service)
    {
        LogDiscoveredService("OSCQuery", service);
        // OSCQuery サービスの port は HTTP ポートのため、OSC UDP 送信先としては登録しない
    }

    private void OnOscServiceAdded(OSCQueryServiceProfile service)
    {
        LogDiscoveredService("OSC", service);
        RegisterRemoteOscEndpoint(service);
    }

    private void RegisterRemoteOscEndpoint(OSCQueryServiceProfile service)
    {
        if (service == null || service.port <= 0) return;
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(service.address.ToString()), service.port);
            _remoteOscEndpoints[service.name] = endpoint;
            Debug.Log($"[OSCQuery Animation Debugger] 送信先として登録: {service.name} ({endpoint})");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OSCQuery Animation Debugger] 送信先の登録に失敗: {service.name} - {ex.Message}");
        }
    }

    private void LogDiscoveredService(string serviceType, OSCQueryServiceProfile service)
    {
        if (service == null) return;

        string key = $"{serviceType}:{service.name}:{service.address}:{service.port}";
        if (!_discoveredServiceKeys.Add(key)) return;

        Debug.Log($"[OSCQuery Animation Debugger] {serviceType}サービスを発見: {service.name} ({service.address}:{service.port})");
    }
}
