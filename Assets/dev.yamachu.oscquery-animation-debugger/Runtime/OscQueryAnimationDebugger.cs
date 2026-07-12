using UnityEngine;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using VRC.OSCQuery;

public enum ParameterDriverMode
{
    Auto,
    Av3Emulator,
    GestureManager,
    AnimatorOnly
}

public partial class OscQueryAnimationDebugger : MonoBehaviour
{
    private const string AvatarParameterPrefix = "/avatar/parameters/";

    private static int? s_activeAvatarRootId;

    [Header("パラメータードライバー設定")]
    [SerializeField] [Tooltip("パラメーター適用方式: Auto=全てを試行, Av3Emulator=Av3優先, GestureManager=GM優先, AnimatorOnly=Animatorのみ")]
    private ParameterDriverMode driverMode = ParameterDriverMode.Auto;
    [SerializeField] [Tooltip("ドライバー初期化のタイムアウト(秒)。この時間内にドライバーが準備完了しない場合は警告を出します。")]
    private float driverInitializationTimeoutSeconds = 30f;

    [Header("OSCQuery 設定")]
    [SerializeField] private string serviceName = "VRC-Client-DUMMY";
    [SerializeField] private int tcpPort = 9001;
    [SerializeField] private int oscPort = 9010;
    [SerializeField] private float discoveryRefreshIntervalSeconds = 5f;
    [SerializeField] private bool verboseReceiveLogging = false;

    [Header("複製アバター除外設定")]
    [SerializeField] private string[] excludedRootNameKeywords = { "shadowclone", "mirrorreflection", "mirror_reflection" };

    [Header("カスタムコンポーネント探索設定")]
    [SerializeField] [Tooltip("コンポーネントのクラス名（例: GlassAmountComponent）。完全修飾名は不要です。")]
    private string[] customComponentNamesToExpose = { };
    [SerializeField] [Tooltip("アバターヒエラルキー配下の全Animatorも探索対象にします（Modular AvatarのPrefab追加対策）。")]
    private bool includeHierarchyAnimators = true;
    [SerializeField] [Tooltip("Animator再スキャン間隔(秒)。Prefab追加後の遅延反映に使います。")]
    private float animatorRescanIntervalSeconds = 2f;

    [Header("OSC Tracker 設定")]
    [SerializeField] [Tooltip("標準OSC Trackerパスの受信とOSCQuery公開を有効にします。")]
    private bool enableTrackers;
    [SerializeField] [Tooltip("OSC座標の原点。未指定時はこのコンポーネントのtransform.rootを使用します。")]
    private Transform trackerReferenceTransform;
    [SerializeField] [Min(0f)] [Tooltip("この秒数より古い更新は適用しません。最終姿勢は維持されます。")]
    private float trackerStaleTimeoutSeconds = 1f;
    [SerializeField] private List<TrackerBinding> trackerBindings = new List<TrackerBinding>();

    [Header("OSC BlendShape 設定")]
    [SerializeField] [Tooltip("/blendshape/{blendshapeName} の受信とOSCQuery公開を有効にします。")]
    private bool enableBlendshapes;
    [SerializeField] [Tooltip("BlendShapeを検索するSkinnedMeshRenderer。複数指定できます。")]
    private List<SkinnedMeshRenderer> blendshapeTargetRenderers = new List<SkinnedMeshRenderer>();
    [SerializeField] [Tooltip("Normalized01はOSC 0..1をUnityの0..100へ変換し、UnityWeightはOSC値をそのままUnity weightとして扱います。")]
    private BlendShapeValueMode blendshapeValueMode = BlendShapeValueMode.Normalized01;

    // --- Driver chain ---
    private readonly List<IAvatarParameterDriver> _drivers = new List<IAvatarParameterDriver>();
    private readonly List<(IAvatarParameterDriver driver, DriverParameterInfo info)> _broadcastSnapshot = new List<(IAvatarParameterDriver, DriverParameterInfo)>();
    private float _driverInitializationStartTime;
    private bool _driverInitializationTimedOut;
    private float _nextDriverRetryTime;

    // --- OSCQuery サービス状態 (Service.cs が使用) ---
    private OSCQueryService _oscQueryService;
    private float _nextDiscoveryRefreshTime;
    private readonly HashSet<string> _discoveredServiceKeys = new HashSet<string>();
    private readonly Dictionary<string, IPEndPoint> _remoteOscEndpoints = new Dictionary<string, IPEndPoint>();

    // --- OSC 受信・送信状態 (Receive.cs が使用) ---
    private UdpClient _oscUdpClient;
    private Thread _oscReceiveThread;
    private volatile bool _oscRunning;
    private UdpClient _oscSendClient;
    private readonly Queue<ParsedOscMessage> _pendingOscMessages = new Queue<ParsedOscMessage>();
    private readonly object _oscQueueLock = new object();

    // --- エンドポイント・ブロードキャスト状態 ---
    private readonly HashSet<string> _registeredEndpoints = new HashSet<string>();
    private int _lastRegisteredEndpointCount = -1;
    private readonly Dictionary<string, string> _lastBroadcastValues = new Dictionary<string, string>(StringComparer.Ordinal);

    // --- その他 ---
    private bool _isPrimaryAvatarInstance;
    private float _nextAnimatorRescanTime;
    private bool _configurationDirty;

    void OnValidate()
    {
        _configurationDirty = true;
    }

    void Start()
    {
        if (!TryActivatePrimaryAvatarInstance())
        {
            enabled = false;
            return;
        }

        // Initialize driver chain
        InitializeDriverChain();

        // Validate OSC port
        if (!ValidateOscPort())
        {
            enabled = false;
            return;
        }

        TryStartOscQueryService();

        // Start OSC UDP receiver thread
        StartOscReceiver();

        // Start driver initialization time tracking
        _driverInitializationStartTime = Time.unscaledTime;
        _nextAnimatorRescanTime = Time.unscaledTime + 1f; // Delayed initial scan
        RebuildTrackerBindings();
        RebuildBlendshapeLookup();
        _configurationDirty = false;
        UpdateAnimatorEndpoints();
    }

    private void InitializeDriverChain()
    {
        _drivers.Clear();

        switch (driverMode)
        {
            case ParameterDriverMode.Auto:
                // Order: GestureManager → Animator → Av3Runtime → Custom Components
                // GM first: when GM controls the avatar via PlayableGraph, direct Animator writes are ineffective
                _drivers.Add(new GestureManagerDriver());
                _drivers.Add(new AnimatorParameterDriver());
                _drivers.Add(new LyumaAv3RuntimeDriver());
                _drivers.Add(new CustomComponentFieldDriver());
                Debug.Log("[OSCQuery Animation Debugger] Autoモード: 全ドライバーを有効化");
                break;

            case ParameterDriverMode.Av3Emulator:
                _drivers.Add(new AnimatorParameterDriver());
                _drivers.Add(new LyumaAv3RuntimeDriver());
                _drivers.Add(new CustomComponentFieldDriver());
                Debug.Log("[OSCQuery Animation Debugger] Av3Emulatorモード");
                break;

            case ParameterDriverMode.AnimatorOnly:
                _drivers.Add(new AnimatorParameterDriver());
                _drivers.Add(new CustomComponentFieldDriver());
                Debug.Log("[OSCQuery Animation Debugger] AnimatorOnlyモード");
                break;

            case ParameterDriverMode.GestureManager:
                _drivers.Add(new GestureManagerDriver());
                _drivers.Add(new AnimatorParameterDriver());
                _drivers.Add(new CustomComponentFieldDriver());
                Debug.Log("[OSCQuery Animation Debugger] GestureManagerモード");
                break;
        }

        Debug.Log($"[OSCQuery Animation Debugger] {_drivers.Count} 個のドライバーを初期化します");
    }

    // --- ドライバー向け公開プロパティ ---
    public bool VerboseReceiveLogging => verboseReceiveLogging;
    public int OscPort => oscPort;
    public bool IncludeHierarchyAnimators => includeHierarchyAnimators;
    public string[] CustomComponentNamesToExpose => customComponentNamesToExpose;

    /// <summary>
    /// ドライバーからコンポーネント型探索を利用するための公開ラッパー
    /// </summary>
    public Type TryFindComponentTypePublic(string componentName) => TryFindComponentType(componentName);


    private bool ValidateOscPort()
    {
        if (oscPort <= 0 || oscPort > 65535)
        {
            Debug.LogError($"[OSCQuery Animation Debugger] 無効な UDP ポートです: {oscPort}");
            return false;
        }

        return true;
    }





    /// <summary>
    /// コンポーネント型を名前で探す。クラス名だけで探索可能（完全修飾名は不要）
    /// </summary>
    private static Type TryFindComponentType(string componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName)) return null;

        // 1. 完全修飾名で探す
        Type directType = Type.GetType(componentName);
        if (directType != null) return directType;

        // 2. クラス名のみで全アセンブリをスキャン
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (Assembly assembly in assemblies)
        {
            try
            {
                Type[] types = assembly.GetTypes();
                foreach (Type type in types)
                {
                    // クラス名がマッチしたら返す（MonoBehaviour 派生のみ）
                    if (type.Name == componentName && typeof(MonoBehaviour).IsAssignableFrom(type))
                    {
                        return type;
                    }
                }
            }
            catch
            {
                // アセンブリのスキャンに失敗した場合はスキップ
            }
        }

        return null;
    }



    void Update()
    {
        if (!_isPrimaryAvatarInstance) return;

        if (_configurationDirty)
        {
            _configurationDirty = false;
            RebuildTrackerBindings();
            RebuildBlendshapeLookup();
            UpdateAnimatorEndpoints();
        }

        // Try to initialize drivers that are not ready yet (throttled to ~1s)
        if (!_driverInitializationTimedOut && Time.unscaledTime >= _nextDriverRetryTime)
        {
            _nextDriverRetryTime = Time.unscaledTime + 1f;
            bool allReady = true;
            foreach (var driver in _drivers)
            {
                if (!driver.IsReady)
                {
                    driver.TryInitialize(this);
                    if (!driver.IsReady)
                    {
                        allReady = false;
                    }
                    else
                    {
                        Debug.Log($"[OSCQuery Animation Debugger] ドライバー準備完了: {driver.DisplayName}");
                        // Trigger endpoint update when a driver becomes ready
                        UpdateAnimatorEndpoints();
                    }
                }
            }

            if (!allReady && Time.unscaledTime - _driverInitializationStartTime > driverInitializationTimeoutSeconds)
            {
                _driverInitializationTimedOut = true;
                bool anyReady = false;
                foreach (var driver in _drivers)
                {
                    if (!driver.IsReady)
                    {
                        Debug.LogWarning($"[OSCQuery Animation Debugger] タイムアウト: ドライバー '{driver.DisplayName}' が {driverInitializationTimeoutSeconds}秒以内に準備完了しませんでした");
                    }
                    else
                    {
                        anyReady = true;
                    }
                }
                if (!anyReady)
                {
                    Debug.LogWarning("[OSCQuery Animation Debugger] パラメーター送信元が見つかりませんでした。Av3Emulator、Gesture Manager、RuntimeAnimatorControllerを持つAnimator、またはndmf/Modular Avatarの'Apply on Play'設定がサポートされています。");
                }
            }
        }

        if (_oscQueryService != null && Time.unscaledTime >= _nextDiscoveryRefreshTime)
        {
            RefreshDiscoveredServices();
            _nextDiscoveryRefreshTime = Time.unscaledTime + Mathf.Max(1f, discoveryRefreshIntervalSeconds);
        }

        if (_oscQueryService != null && Time.unscaledTime >= _nextAnimatorRescanTime)
        {
            UpdateAnimatorEndpoints();
            _nextAnimatorRescanTime = Time.unscaledTime + Mathf.Max(0.5f, animatorRescanIntervalSeconds);
        }

        ProcessPendingOscMessages();
        ApplyDirtyTrackerPoses();

        // Broadcast changed parameters using drivers
        BroadcastChangedParameters();
    }

    void OnDestroy()
    {
        if (_isPrimaryAvatarInstance && s_activeAvatarRootId.HasValue && s_activeAvatarRootId.Value == transform.root.GetInstanceID())
        {
            s_activeAvatarRootId = null;
        }

        // OSC 受信スレッドの停止
        _oscRunning = false;
        if (_oscUdpClient != null)
        {
            _oscUdpClient.Close();
            _oscUdpClient = null;
        }
        if (_oscReceiveThread != null)
        {
            _oscReceiveThread.Join(500);
            _oscReceiveThread = null;
        }
        if (_oscSendClient != null)
        {
            _oscSendClient.Close();
            _oscSendClient = null;
        }

        // 終了時にサービスを確実に解放（ポートの占有を防ぐ）
        if (_oscQueryService != null)
        {
            try
            {
                // リフレクションで HttpListener を直接クローズ（ポート解放の確実化）
                ForceCloseHttpListener();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OSCQuery Animation Debugger] HttpListener 強制クローズ失敗: {ex.Message}");
            }

            _oscQueryService.Dispose();
            Debug.Log("[OSCQuery Animation Debugger] サービスを停止しました。");
        }
    }

    private void ForceCloseHttpListener()
    {
        if (_oscQueryService == null) return;

        try
        {
            Type oscQueryServiceType = _oscQueryService.GetType();
            FieldInfo httpServerField = oscQueryServiceType.GetField(
                "_httpServer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );

            if (httpServerField != null)
            {
                object httpServer = httpServerField.GetValue(_oscQueryService);
                if (httpServer != null)
                {
                    Type httpServerType = httpServer.GetType();
                    
                    // HttpListener を探索（複数パターン対応）
                    FieldInfo listenerField = httpServerType.GetField(
                        "_listener",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                    );

                    if (listenerField == null)
                    {
                        listenerField = httpServerType.GetField(
                            "listener",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                        );
                    }

                    if (listenerField != null)
                    {
                        object listener = listenerField.GetValue(httpServer);
                        if (listener is System.Net.HttpListener httpListener)
                        {
                            if (httpListener.IsListening)
                            {
                                httpListener.Stop();
                                httpListener.Close();
                                Debug.Log("[OSCQuery Animation Debugger] HttpListener を強制クローズしました (TCP:" + tcpPort + ")");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OSCQuery Animation Debugger] HttpListener リフレクションアクセス失敗（問題なし）: {ex.Message}");
        }
    }
}
