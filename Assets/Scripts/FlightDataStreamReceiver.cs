using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class FlightDataStreamReceiver : MonoBehaviour
{
    [Serializable]
    public class TrackPosition
    {
        public string trackId;
        public string planId;
        public string flightTime;
        public double lng;
        public double lat;
        public float alt;
        public float heading;
        public float pitch;
        public float roll;
    }

    [Serializable]
    public class TrackLine
    {
        public string trackId;
        public string flightTime;
        public readonly List<TrackPosition> positions = new List<TrackPosition>();
        public readonly List<TrackPlanLine> plans = new List<TrackPlanLine>();
    }

    [Serializable]
    public class TrackPlanLine
    {
        public string trackId;
        public string planId;
        public string flightTime;
        public readonly List<TrackPosition> positions = new List<TrackPosition>();
    }

    [Serializable]
    public class TrackTimetableState
    {
        public string flightTime;
        /// <summary>本帧没带时间。告警按时间入队，这种情况直接跳过，不沿用上一帧。</summary>
        public bool frameTimeMissing;
        public string trackId;
        public string state;
        public float battery;
        public float speed;
        public string alarm;
        public string detectorType;
        public string rangeType;
        public float detectRange;
        public float yawAngle;
        public float pitchAngle;
        /// <summary>该角度本帧有没有下发。用来区分「0 度」和「没数据」。</summary>
        public bool hasYawAngle;
        public bool hasPitchAngle;
        public string centerPoint;
        public string centerPoint1;
        public float yawScanAngle;
        public float pitchScanAngle;
        public string radarStatus;
    }

    [Serializable]
    public class CycleTableRecord
    {
        public string flightTime;
        public string trackId;
        public int cycleId;
        public double lng;
        public double lat;
        public float alt;
        public float heading;
        public float pitch;
        public float roll;
        public string radarStatus;
    }

    [Serializable]
    public class TaskTimeInfo
    {
        public string taskId;
        public string startTime;
        public string endTime;
        public float durationSeconds;
    }

    public static FlightDataStreamReceiver Instance { get; private set; }
    public static event Action<TaskTimeInfo> TaskTimeInfoReceived;
    public static event Action<string, float> FlightTimeReceived;
    public static event Action DataChannelConnected;

    [Header("Data Server")]
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int port = 8888;
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool listenOnAllInterfaces = true;
    [SerializeField] private int listenBacklog = 1;

    [Header("Debug")]
    [SerializeField] private bool enableDebugLog = true;
    [SerializeField] private bool logIncomingMessageTypes = false;
    [SerializeField] private bool logIncomingRawPayloads = false;

    private TcpClient client;
    private TcpListener listener;
    private NetworkStream stream;
    private Thread listenThread;
    private Coroutine listenRetryRoutine;
    private bool warnedPortInUse;
    private volatile bool isRunning;
    private readonly object connectionLock = new object();
    private UnityMainThreadDispatcher mainThreadDispatcher;
    private bool isDuplicateInstance;
    private readonly object dataLock = new object();

    private readonly Dictionary<string, TrackPosition> latestPositionsByTrack = new Dictionary<string, TrackPosition>();
    private readonly Dictionary<string, TrackLine> latestTrackLinesByTrack = new Dictionary<string, TrackLine>();
    private readonly Dictionary<string, TrackTimetableState> latestStatesByTrack = new Dictionary<string, TrackTimetableState>();
    private readonly Dictionary<string, TrackTimetableState> latestSensorsByKey = new Dictionary<string, TrackTimetableState>();
    private readonly HashSet<string> removedSensorKeys = new HashSet<string>();
    private readonly List<CycleTableRecord> latestCycleRecords = new List<CycleTableRecord>();
    private readonly HashSet<string> latestCycleUpdatedTrackIds = new HashSet<string>();
    private string latestCycleFlightTime;
    private int trackPositionsResponseCount;
    private bool lastTrackPositionsHadRenderableRoute;
    private string lastTrackPositionsSummary = "";
    private bool warnedNonRenderableTrackPositions;
    private readonly Dictionary<string, float> previousHeadingByTrack = new Dictionary<string, float>();
    private readonly Dictionary<string, DateTime> previousTimeByTrack = new Dictionary<string, DateTime>();
    private readonly Dictionary<string, List<string>> dataFields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> latestValuesByTrack = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    private int dataFieldsVersion;
    /// <summary>Incremented every time a DATA_FIELDS message is applied; cheap change detection for UI.</summary>
    public int DataFieldsVersion => dataFieldsVersion;
    public IReadOnlyDictionary<string, List<string>> DataFields
    {
        get { lock (dataLock) { return new Dictionary<string, List<string>>(dataFields); } }
    }

    /// <summary>按 DATA_FIELDS 的 json key 取该飞机最近一帧的原始值（周期表/事件表增量合并）。</summary>
    public bool TryGetFieldValue(string trackId, string jsonKey, out string value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(jsonKey))
            return false;
        lock (dataLock)
        {
            if (!latestValuesByTrack.TryGetValue(trackId, out var bag) || bag == null)
                return false;
            return bag.TryGetValue(jsonKey, out value) && value != null;
        }
    }

    public float? TryGetFieldFloat(string trackId, string jsonKey)
    {
        if (!TryGetFieldValue(trackId, jsonKey, out string text) || string.IsNullOrWhiteSpace(text))
            return null;
        if (float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed))
            return parsed;
        return null;
    }
    private string latestEventTableFlightTime = string.Empty;
    private int eventTableFrameCount;
    private bool eventTableSummaryLogged;
    private bool cycleTableSummaryLogged;

    /// <summary>最近一帧 EVENT_TABLE_DATA 的 flightTime（调试用）。
    /// 数据端用该类型推送事件/时刻表（与 TIMETABLE_STATE 同结构，约几百字节），
    /// 必须解析 state 并在有经纬姿态时增量合并位置；禁止整包丢弃，否则面板停在首帧。
    /// 禁止在锁内 ToString 大负载。</summary>
    public bool TryGetLatestEventTable(out string flightTime, out string json)
    {
        lock (dataLock)
        {
            flightTime = latestEventTableFlightTime;
            json = string.Empty;
        }
        return !string.IsNullOrEmpty(flightTime);
    }
    private string primaryTrackId;
    private TaskTimeInfo latestTaskTimeInfo;
    private DateTime? taskStartTime;

    public string Host => host;
    public int Port => port;
    public bool IsConnected => client != null && client.Connected && stream != null;

    public TrackPosition PrimaryPosition { get { lock (dataLock) { return GetPrimaryPositionUnsafe(); } } }
    public TrackTimetableState PrimaryTimetableState { get { lock (dataLock) { return GetPrimaryStateUnsafe(); } } }
    public float PrimaryAltitudeMeters { get { lock (dataLock) { return GetPrimaryPositionUnsafe()?.alt ?? 0f; } } }
    public float PrimaryAirspeedKnots { get { lock (dataLock) { return GetPrimaryStateUnsafe()?.speed ?? 0f; } } }
    public float PrimaryPitchDeg { get { lock (dataLock) { return GetPrimaryPositionUnsafe()?.pitch ?? 0f; } } }
    public float PrimaryRollDeg { get { lock (dataLock) { return GetPrimaryPositionUnsafe()?.roll ?? 0f; } } }
    public float PrimaryTurnRateDegPerSec { get { lock (dataLock) { return GetPrimaryTurnRateUnsafe(); } } }
    public float PrimarySlipSkidDeg { get { lock (dataLock) { return GetPrimaryPositionUnsafe()?.roll ?? 0f; } } }
    public string PrimaryTrackId { get { lock (dataLock) { return primaryTrackId; } } }
    public TaskTimeInfo LatestTaskTimeInfo { get { lock (dataLock) { return latestTaskTimeInfo; } } }
    public int TrackPositionsResponseCount { get { lock (dataLock) { return trackPositionsResponseCount; } } }
    public bool LastTrackPositionsHadRenderableRoute { get { lock (dataLock) { return lastTrackPositionsHadRenderableRoute; } } }
    public string LastTrackPositionsSummary { get { lock (dataLock) { return lastTrackPositionsSummary; } } }
    public List<TrackPosition> SnapshotPositions
    {
        get
        {
            lock (dataLock)
            {
                return new List<TrackPosition>(latestPositionsByTrack.Values);
            }
        }
    }
    public List<TrackTimetableState> SnapshotStates
    {
        get
        {
            lock (dataLock)
            {
                return new List<TrackTimetableState>(latestStatesByTrack.Values);
            }
        }
    }

    /// <summary>按 飞机|探测器 增量保存的传感器快照。无探测器类型的帧不会覆盖已有行。</summary>
    public List<TrackTimetableState> SnapshotSensors
    {
        get
        {
            lock (dataLock)
            {
                return new List<TrackTimetableState>(latestSensorsByKey.Values);
            }
        }
    }

    /// <summary>面板删除过传感器后，即使快照为空也不回退到时刻表整行，避免已删条目重新出现。</summary>
    public bool HasSensorEdits
    {
        get
        {
            lock (dataLock)
            {
                return latestSensorsByKey.Count > 0 || removedSensorKeys.Count > 0;
            }
        }
    }

    /// <summary>被删掉的传感器键（飞机|探测器）。面板据此保留飞机条目。</summary>
    public List<string> RemovedSensorKeys
    {
        get
        {
            lock (dataLock)
            {
                return new List<string>(removedSensorKeys);
            }
        }
    }

    public List<string> SnapshotTrackIds()
    {
        lock (dataLock)
        {
            var ids = new List<string>();
            foreach (var kv in latestPositionsByTrack)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !ids.Contains(kv.Key))
                    ids.Add(kv.Key);
            }
            foreach (var kv in latestStatesByTrack)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !ids.Contains(kv.Key))
                    ids.Add(kv.Key);
            }
            foreach (var kv in latestSensorsByKey)
            {
                string id = kv.Value != null ? kv.Value.trackId : null;
                if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id))
                    ids.Add(id);
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
    }

    public bool TryGetSensor(string trackId, string detector, out TrackTimetableState sensor)
    {
        sensor = null;
        if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(detector))
            return false;
        string key = trackId + "|" + detector.Trim();
        lock (dataLock)
        {
            if (!latestSensorsByKey.TryGetValue(key, out var found) || found == null)
                return false;
            sensor = CloneSensor(found);
            return true;
        }
    }

    public bool AddSensor(string trackId, TrackTimetableState template)
    {
        if (string.IsNullOrWhiteSpace(trackId) || template == null || string.IsNullOrWhiteSpace(template.detectorType))
            return false;
        string detector = template.detectorType.Trim();
        string key = trackId + "|" + detector;
        lock (dataLock)
        {
            removedSensorKeys.Remove(key);
            if (latestSensorsByKey.ContainsKey(key))
                return false;
            var sensor = CloneSensor(template);
            sensor.trackId = trackId;
            sensor.detectorType = detector;
            latestSensorsByKey[key] = sensor;
        }
        SaveSensorCatalog();
        return true;
    }

    public bool RenameSensor(string trackId, string oldDetector, string newDetector)
    {
        if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(oldDetector) || string.IsNullOrWhiteSpace(newDetector))
            return false;
        string from = oldDetector.Trim();
        string to = newDetector.Trim();
        if (from == to)
            return true;
        string oldKey = trackId + "|" + from;
        string newKey = trackId + "|" + to;
        lock (dataLock)
        {
            if (!latestSensorsByKey.TryGetValue(oldKey, out var sensor) || sensor == null)
                return false;
            if (latestSensorsByKey.ContainsKey(newKey))
                return false;
            latestSensorsByKey.Remove(oldKey);
            removedSensorKeys.Add(oldKey);
            removedSensorKeys.Remove(newKey);
            sensor.detectorType = to;
            latestSensorsByKey[newKey] = sensor;
        }
        SaveSensorCatalog();
        return true;
    }

    public bool RemoveSensor(string trackId, string detector)
    {
        if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(detector))
            return false;
        string key = trackId + "|" + detector.Trim();
        bool removed;
        lock (dataLock)
        {
            removedSensorKeys.Add(key);
            removed = latestSensorsByKey.Remove(key);
        }
        SaveSensorCatalog();
        return removed;
    }

    public void SeedSensorsIfEmpty(IList<TrackTimetableState> states)
    {
        if (states == null)
            return;
        lock (dataLock)
        {
            if (latestSensorsByKey.Count > 0 || removedSensorKeys.Count > 0)
                return;
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (s == null || string.IsNullOrWhiteSpace(s.trackId) || string.IsNullOrWhiteSpace(s.detectorType))
                    continue;
                string detector = s.detectorType.Trim();
                string key = s.trackId + "|" + detector;
                if (removedSensorKeys.Contains(key) || latestSensorsByKey.ContainsKey(key))
                    continue;
                var clone = CloneSensor(s);
                clone.trackId = s.trackId;
                clone.detectorType = detector;
                latestSensorsByKey[key] = clone;
            }
        }
    }

    public void ApplySensorCatalog(PanelMemory.Store store)
    {
        if (store == null)
            return;
        lock (dataLock)
        {
            if (store.removedSensors != null)
            {
                foreach (string key in store.removedSensors)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        removedSensorKeys.Add(key);
                }
            }
            if (store.sensorCatalog != null)
            {
                foreach (var item in store.sensorCatalog)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.trackId) || string.IsNullOrWhiteSpace(item.detectorType))
                        continue;
                    string detector = item.detectorType.Trim();
                    string key = item.trackId + "|" + detector;
                    if (removedSensorKeys.Contains(key))
                        continue;
                    latestSensorsByKey[key] = new TrackTimetableState
                    {
                        trackId = item.trackId,
                        detectorType = detector,
                        rangeType = item.rangeType,
                        detectRange = item.detectRange,
                        yawAngle = item.yawAngle,
                        pitchAngle = item.pitchAngle,
                        hasYawAngle = true,
                        hasPitchAngle = true,
                        centerPoint = string.IsNullOrWhiteSpace(item.centerPoint) ? "中" : item.centerPoint,
                        centerPoint1 = string.IsNullOrWhiteSpace(item.centerPoint1) ? "中" : item.centerPoint1,
                        yawScanAngle = item.yawScanAngle,
                        pitchScanAngle = item.pitchScanAngle,
                        radarStatus = string.IsNullOrWhiteSpace(item.radarStatus) ? "有效" : item.radarStatus,
                    };
                }
            }
            foreach (string key in removedSensorKeys)
                latestSensorsByKey.Remove(key);
        }
    }

    public void SaveSensorCatalog()
    {
        var store = PanelMemory.Load();
        lock (dataLock)
        {
            store.sensorCatalog = new List<PanelMemory.SensorCatalogItem>();
            foreach (var kv in latestSensorsByKey)
            {
                var s = kv.Value;
                if (s == null || string.IsNullOrWhiteSpace(s.trackId) || string.IsNullOrWhiteSpace(s.detectorType))
                    continue;
                store.sensorCatalog.Add(new PanelMemory.SensorCatalogItem
                {
                    trackId = s.trackId,
                    detectorType = s.detectorType,
                    rangeType = s.rangeType,
                    detectRange = s.detectRange,
                    yawAngle = s.yawAngle,
                    pitchAngle = s.pitchAngle,
                    centerPoint = s.centerPoint,
                    centerPoint1 = s.centerPoint1,
                    yawScanAngle = s.yawScanAngle,
                    pitchScanAngle = s.pitchScanAngle,
                    radarStatus = s.radarStatus,
                });
            }
            store.removedSensors = new List<string>(removedSensorKeys);
        }
        PanelMemory.Save(store);
    }

    private static TrackTimetableState CloneSensor(TrackTimetableState src)
    {
        if (src == null)
            return new TrackTimetableState();
        return new TrackTimetableState
        {
            flightTime = src.flightTime,
            frameTimeMissing = src.frameTimeMissing,
            trackId = src.trackId,
            state = src.state,
            battery = src.battery,
            speed = src.speed,
            alarm = src.alarm,
            detectorType = src.detectorType,
            rangeType = src.rangeType,
            detectRange = src.detectRange,
            yawAngle = src.yawAngle,
            pitchAngle = src.pitchAngle,
            hasYawAngle = src.hasYawAngle,
            hasPitchAngle = src.hasPitchAngle,
            centerPoint = src.centerPoint,
            centerPoint1 = src.centerPoint1,
            yawScanAngle = src.yawScanAngle,
            pitchScanAngle = src.pitchScanAngle,
            radarStatus = src.radarStatus,
        };
    }
    public List<CycleTableRecord> SnapshotCycleRecords
    {
        get
        {
            lock (dataLock)
            {
                return new List<CycleTableRecord>(latestCycleRecords);
            }
        }
    }

    private void Awake()
    {
        mainThreadDispatcher = UnityMainThreadDispatcher.Instance();

        if (Instance == null)
        {
            Instance = this;
            Application.wantsToQuit -= OnWantsToQuit;
            Application.wantsToQuit += OnWantsToQuit;
        }
        else if (Instance != this)
        {
            isDuplicateInstance = true;
            DisconnectImmediate();
            Destroy(gameObject);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRuntimeState()
    {
        if (Instance != null)
        {
            Instance.DisconnectImmediate();
        }

        Instance = null;
        TaskTimeInfoReceived = null;
        FlightTimeReceived = null;
        DataChannelConnected = null;
    }

    public static void DisconnectAllReceivers()
    {
        if (Instance != null)
        {
            Instance.DisconnectImmediate();
        }

        var receivers = Resources.FindObjectsOfTypeAll<FlightDataStreamReceiver>();
        for (int i = 0; i < receivers.Length; i++)
        {
            var receiver = receivers[i];
            if (receiver != null)
            {
                receiver.DisconnectImmediate();
            }
        }
    }
    public bool HasPlannedRouteLines
    {
        get
        {
            lock (dataLock)
            {
                foreach (var kv in latestTrackLinesByTrack)
                {
                    TrackLine line = kv.Value;
                    if (line == null)
                    {
                        continue;
                    }

                    if (line.positions.Count >= 2)
                    {
                        return true;
                    }

                    for (int i = 0; i < line.plans.Count; i++)
                    {
                        if (line.plans[i] != null && line.plans[i].positions.Count >= 2)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }
    }

    private void Start()
    {
        // Fallback: if Awake somehow didn't set Instance, set it now
        if (Instance == null)
        {
            Instance = this;
            if (enableDebugLog)
                Debug.Log("[FlightDataStreamReceiver] Instance initialized in Start() (fallback from Awake)");
        }

        if (isDuplicateInstance)
        {
            return;
        }

        ApplySensorCatalog(PanelMemory.Load());
        if (connectOnStart)
        {
            if (!Connect())
            {
                listenRetryRoutine = StartCoroutine(RetryListenRoutine());
            }
        }
    }

    public bool Connect()
    {
        if (isDuplicateInstance)
        {
            return false;
        }

        lock (connectionLock)
        {
            if (listener != null && isRunning)
            {
                return true;
            }

            if (listenThread != null && listenThread.IsAlive)
            {
                LogWarning($"Listen thread is still stopping on port {port}; skip duplicate Connect.");
                return false;
            }

            try
            {
                isRunning = true;

                var bindAddress = listenOnAllInterfaces ? IPAddress.Any : IPAddress.Parse(host);
                StartListenerUnsafe(bindAddress);
                warnedPortInUse = false;

                Log($"Listening on {bindAddress}:{port}");
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                CleanupFailedConnectUnsafe();
                if (!warnedPortInUse)
                {
                    LogWarning($"Port {port} is temporarily unavailable. Unity will keep retrying until it can listen.");
                    warnedPortInUse = true;
                }
                return false;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
            {
                LogWarning($"Listen skipped: access denied while binding {host}:{port}. Check whether the port is reserved or blocked by Windows/network policy.");
                CleanupFailedConnectUnsafe();
                return false;
            }
            catch (Exception ex)
            {
                LogError($"Listen failed: {ex.Message}");
                CleanupFailedConnectUnsafe();
                return false;
            }
        }
    }

    private void StartListenerUnsafe(IPAddress bindAddress)
    {
        listener = new TcpListener(bindAddress, port);
        // A data client must always reach the current Unity play session. Allowing address
        // reuse can leave an old Unity process listening on the same port and receive data.
        listener.Server.ExclusiveAddressUse = true;

        listener.Start(Mathf.Max(1, listenBacklog));

        listenThread = new Thread(ListenLoop);
        listenThread.IsBackground = true;
        listenThread.Start();
    }

    private IEnumerator RetryListenRoutine()
    {
        while (!isDuplicateInstance && !isRunning)
        {
            yield return new WaitForSecondsRealtime(2f);
            if (Connect())
            {
                listenRetryRoutine = null;
                yield break;
            }
        }

        listenRetryRoutine = null;
    }

    public List<TrackLine> SnapshotTrackLines
    {
        get
        {
            lock (dataLock)
            {
                var result = new List<TrackLine>(latestTrackLinesByTrack.Count);
                foreach (var kv in latestTrackLinesByTrack)
                {
                    var source = kv.Value;
                    if (source == null)
                    {
                        continue;
                    }

                    var copy = new TrackLine
                    {
                        trackId = source.trackId,
                        flightTime = source.flightTime,
                    };

                    foreach (var position in source.positions)
                    {
                        copy.positions.Add(CloneTrackPosition(position));
                    }

                    foreach (var plan in source.plans)
                    {
                        if (plan == null)
                        {
                            continue;
                        }

                        var planCopy = new TrackPlanLine
                        {
                            trackId = plan.trackId,
                            planId = plan.planId,
                            flightTime = plan.flightTime,
                        };

                        foreach (var position in plan.positions)
                        {
                            planCopy.positions.Add(CloneTrackPosition(position));
                        }

                        copy.plans.Add(planCopy);
                    }

                    result.Add(copy);
                }

                return result;
            }
        }
    }

    public void Disconnect()
    {
        DisconnectInternal(500, false);
    }

    public void DisconnectImmediate()
    {
        DisconnectInternal(2500, true);
    }

    private void DisconnectInternal(int joinTimeoutMs, bool abortIfStillAlive)
    {
        isRunning = false;

        TcpListener oldListener = null;
        Thread oldListenThread = null;
        lock (connectionLock)
        {
            oldListener = listener;
            oldListenThread = listenThread;
            listener = null;
            listenThread = null;
        }

        CloseClientConnection();

        try
        {
            oldListener?.Stop();
        }
        catch { }

        if (oldListenThread != null && oldListenThread.IsAlive && Thread.CurrentThread != oldListenThread)
        {
            try
            {
                oldListenThread.Join(Mathf.Max(0, joinTimeoutMs));
            }
            catch { }

            if (abortIfStillAlive && oldListenThread.IsAlive)
            {
                try
                {
                    oldListenThread.Interrupt();
                }
                catch { }

                try
                {
                    oldListenThread.Abort();
                }
                catch { }
            }
        }
    }

    private void CleanupFailedConnectUnsafe()
    {
        isRunning = false;
        CloseClientConnection();

        try
        {
            listener?.Stop();
        }
        catch { }

        listener = null;
        listenThread = null;
    }

    private void ListenLoop()
    {
        try
        {
            while (isRunning && listener != null)
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }

                TcpClient acceptedClient = listener.AcceptTcpClient();
                acceptedClient.NoDelay = true;
                CloseClientConnection();
                client = acceptedClient;
                stream = client.GetStream();
                Log($"Data client connected from {client.Client.RemoteEndPoint}");
                DispatchDataChannelConnected();
                ReadLoop();
                CloseClientConnection();
            }
        }
        catch (Exception ex)
        {
            if (isRunning)
            {
                LogError($"Listen loop failed: {ex.Message}");
            }
        }
        finally
        {
            CloseClientConnection();
        }
    }

    private void DispatchDataChannelConnected()
    {
        try
        {
            if (mainThreadDispatcher != null)
            {
                mainThreadDispatcher.Enqueue(() => DataChannelConnected?.Invoke());
            }
            else
            {
                LogWarning("Data channel event skipped: main thread dispatcher is not initialized.");
            }
        }
        catch (Exception ex)
        {
            LogError($"Dispatch data channel event failed: {ex.Message}");
        }
    }

    private void CloseClientConnection()
    {
        try
        {
            stream?.Close();
        }
        catch { }
        stream = null;

        try
        {
            client?.Close();
        }
        catch { }
        client = null;
    }

    private void ReadLoop()
    {
        try
        {
            using (var reader = new StreamReader(stream))
            {
                while (isRunning && stream != null && client != null && client.Connected)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    HandleMessage(line);
                }
            }
        }
        catch (Exception ex)
        {
            if (isRunning)
            {
                LogError($"Read loop failed: {ex.Message}");
            }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            json = json.Trim();
            if (logIncomingRawPayloads)
                Debug.Log($"[FlightDataStreamReceiver] RAW len={json.Length} {json}");

            string type;
            JToken data;
            if (!TrySplitEnvelope(json, out type, out data))
            {
                LogError("Parse message failed: unrecognized envelope");
                return;
            }

            switch (type)
            {
                case "TRACK_POSITIONS":
                    LogIncomingType("TRACK_POSITIONS");
                    ParseTrackPositions(data, data);
                    break;
                case "TIMETABLE_STATE":
                    LogIncomingType("TIMETABLE_STATE");
                    ParseTimetableState(data, data);
                    break;
                case "CYCLE_TABLE_DATA":
                    LogIncomingType("CYCLE_TABLE_DATA");
                    ParseCycleTableData(data, data);
                    break;
                case "TASK_TIME_INFO":
                    LogIncomingType("TASK_TIME_INFO");
                    ParseTaskTimeInfo(data);
                    break;
                case "DATA_FIELDS":
                    LogIncomingType("DATA_FIELDS");
                    ParseDataFields(data);
                    break;
                case "EVENT_TABLE_DATA":
                    LogIncomingType("EVENT_TABLE_DATA");
                    ParseEventTableData(data, data);
                    break;
                default:
                    Log($"Received unknown message type={type}");
                    break;
            }

        }
        catch (Exception ex)
        {
            LogError($"Parse message failed: {ex.Message}");
        }
    }

    private static bool TrySplitEnvelope(string json, out string type, out JToken data)
    {
        type = null;
        data = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        JObject envelope = null;
        try
        {
            if (json[0] == '{')
                envelope = JObject.Parse(json);
        }
        catch
        {
            envelope = null;
        }

        if (envelope == null)
        {
            int brace = json.IndexOf('{');
            if (brace <= 0)
                return false;
            type = json.Substring(0, brace).Trim().Trim('"', '\'', ':', ',');
            envelope = JObject.Parse(json.Substring(brace));
        }

        type = type ?? envelope.Value<string>("type");
        data = envelope["data"];
        if (data == null || data.Type == JTokenType.Null)
            data = envelope;

        if (string.IsNullOrWhiteSpace(type))
        {
            if (data["records"] != null || data["hight"] != null || (data["lng"] != null && data["trackId"] != null))
                type = "CYCLE_TABLE_DATA";
            else if (data["flightStates"] != null || data["event"] != null)
                type = "EVENT_TABLE_DATA";
            else if (data["fields"] != null)
                type = "DATA_FIELDS";
            else if (data["tracks"] != null)
                type = "TRACK_POSITIONS";
        }

        return !string.IsNullOrWhiteSpace(type) && data != null;
    }

    private void ParseTrackPositions(JToken data, JToken envelope = null)
    {
        if (data == null)
        {
            return;
        }

        string messageFlightTime = GetFlightTime(data, envelope);
        PublishFlightTime(messageFlightTime);

        int parsedPositionCount = 0;
        int parsedLineCount = 0;
        string frameFlightTime = messageFlightTime;
        bool hasRenderableTrackLine = false;
        var routeSummaryParts = new List<string>();
        var parsedTrackLines = new Dictionary<string, TrackLine>();

        lock (dataLock)
        {
            trackPositionsResponseCount++;

            var tracks = data["tracks"] as JArray;
            if (tracks == null)
            {
                lastTrackPositionsHadRenderableRoute = false;
                lastTrackPositionsSummary = "no tracks array";
                return;
            }

            foreach (var trackToken in tracks)
            {
                string trackId = GetString(trackToken, "trackId");
                if (string.IsNullOrWhiteSpace(trackId))
                {
                    continue;
                }

                var trackLine = new TrackLine
                {
                    trackId = trackId,
                    flightTime = frameFlightTime,
                };

                AppendTrackPositions(trackToken["positions"] as JArray, trackId, null, frameFlightTime, trackLine, null, ref parsedPositionCount);
                if (trackLine.positions.Count >= 2)
                {
                    hasRenderableTrackLine = true;
                }

                var plans = trackToken["plans"] as JArray;
                if (plans != null)
                {
                    int planIndex = 0;
                    foreach (var planToken in plans)
                    {
                        string planId = GetString(planToken, "planId");
                        if (string.IsNullOrWhiteSpace(planId))
                        {
                            planId = $"plan_{planIndex + 1}";
                        }

                        var planLine = new TrackPlanLine
                        {
                            trackId = trackId,
                            planId = planId,
                            flightTime = frameFlightTime,
                        };

                        AppendTrackPositions(planToken["positions"] as JArray, trackId, planId, frameFlightTime, trackLine, planLine, ref parsedPositionCount);
                        routeSummaryParts.Add($"{trackId}/{planId}:{planLine.positions.Count}");
                        if (planLine.positions.Count > 0)
                        {
                            trackLine.plans.Add(planLine);
                        }

                        if (planLine.positions.Count >= 2)
                        {
                            hasRenderableTrackLine = true;
                        }

                        planIndex++;
                    }
                }

                if (trackLine.positions.Count > 0)
                {
                    parsedTrackLines[trackId] = trackLine;
                    parsedLineCount++;
                }
                else
                {
                    routeSummaryParts.Add($"{trackId}:0");
                }
            }

            if (hasRenderableTrackLine)
            {
                latestTrackLinesByTrack.Clear();
                foreach (var kv in parsedTrackLines)
                {
                    latestTrackLinesByTrack[kv.Key] = kv.Value;
                }
            }

            lastTrackPositionsHadRenderableRoute = hasRenderableTrackLine;
            lastTrackPositionsSummary = routeSummaryParts.Count > 0 ? string.Join(", ", routeSummaryParts) : "empty";
        }

        if (parsedPositionCount > 0)
        {
            if (logIncomingMessageTypes)
            {
                Log($"Track positions received: {parsedPositionCount}, lines={parsedLineCount}, routeUpdated={hasRenderableTrackLine}, planPointCounts={lastTrackPositionsSummary}");
            }

            if (!hasRenderableTrackLine && !warnedNonRenderableTrackPositions)
            {
                LogWarning("TRACK_POSITIONS did not contain a complete planned route. Expected at least one plan.positions array with 2 or more points.");
                warnedNonRenderableTrackPositions = true;
            }
        }
    }

    private void AppendTrackPositions(JArray positions, string trackId, string planId, string frameFlightTime, TrackLine trackLine, TrackPlanLine planLine, ref int parsedPositionCount)
    {
        if (positions == null)
        {
            return;
        }

        foreach (var posToken in positions)
        {
            var position = new TrackPosition
            {
                trackId = trackId,
                planId = planId,
                flightTime = GetString(posToken, "flightTime") ?? frameFlightTime,
                lng = GetMappedDouble(posToken, "lng") ?? 0d,
                lat = GetMappedDouble(posToken, "lat") ?? 0d,
                alt = GetMappedFloat(posToken, "alt", "高度") ?? 0f,
                heading = GetMappedFloat(posToken, "heading", "航向角(度)", "航向", "航向角") ?? 0f,
                pitch = GetMappedFloat(posToken, "pitch", "俯仰角(度)", "俯仰", "俯仰角") ?? 0f,
                roll = GetMappedFloat(posToken, "roll", "横滚角(度)", "横滚", "横滚角", "翻滚角") ?? 0f,
            };

            trackLine.positions.Add(position);
            if (planLine != null)
            {
                planLine.positions.Add(position);
            }

            // 计划航迹只用于画线，不更新飞机实时位置
            parsedPositionCount++;
        }
    }

    private static TrackPosition CloneTrackPosition(TrackPosition source)
    {
        if (source == null)
        {
            return null;
        }

        return new TrackPosition
        {
            trackId = source.trackId,
            planId = source.planId,
            flightTime = source.flightTime,
            lng = source.lng,
            lat = source.lat,
            alt = source.alt,
            heading = source.heading,
            pitch = source.pitch,
            roll = source.roll,
        };
    }

    private void ParseTaskTimeInfo(JToken data)
    {
        if (data == null)
        {
            return;
        }

        string startTime = data.Value<string>("startTime");
        string endTime = data.Value<string>("endTime");
        float durationSeconds = 0f;

        if (TryParseLocalTime(startTime, out DateTime start) && TryParseLocalTime(endTime, out DateTime end))
        {
            durationSeconds = Mathf.Max(0f, (float)(end - start).TotalSeconds);
            taskStartTime = start;
        }

        var info = new TaskTimeInfo
        {
            taskId = data.Value<string>("taskId"),
            startTime = startTime,
            endTime = endTime,
            durationSeconds = durationSeconds,
        };

        lock (dataLock)
        {
            latestTaskTimeInfo = info;
        }

        TaskTimeInfoReceived?.Invoke(info);
        Log($"Task time info: {info.startTime} ~ {info.endTime} ({info.durationSeconds:F3}s)");
    }

    private void ParseDataFields(JToken data)
    {
        if (data == null)
            return;

        var fieldsToken = data["fields"] as JObject ?? data as JObject;
        if (fieldsToken == null)
        {
            LogWarning("DATA_FIELDS has no fields object.");
            return;
        }

        lock (dataLock)
        {
            dataFields.Clear();
            foreach (var property in fieldsToken.Properties())
            {
                string jsonKey = (property.Name ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(jsonKey) || jsonKey == "fields")
                    continue;

                var aliases = new List<string>();
                if (property.Value is JArray array)
                {
                    foreach (var item in array)
                    {
                        string alias = item?.Value<string>();
                        if (!string.IsNullOrWhiteSpace(alias))
                            aliases.Add(alias.Trim());
                    }
                }
                else if (property.Value != null && property.Value.Type != JTokenType.Null)
                {
                    string alias = property.Value.Value<string>();
                    if (!string.IsNullOrWhiteSpace(alias))
                        aliases.Add(alias.Trim());
                }

                dataFields[jsonKey] = aliases;
            }
            dataFieldsVersion++;
        }

        Log($"DATA_FIELDS received: {dataFields.Count} json keys ({string.Join(", ", dataFields.Keys)})");
    }

    // Data-side implementations may keep the frame time beside a record rather
    // than at data level. Resolve it once so every visual consumer sees the same time.
    private string GetFlightTime(JToken data, JToken envelope)
    {
        // 记录层时间每帧会变；data/envelope 上的 flightTime 有时一直停在任务起点。
        JToken inner = data == null ? null : (data["records"] ?? data["flightStates"] ?? data["event"]);
        string nested = FindFlightTimeRecursive(inner);
        if (!string.IsNullOrWhiteSpace(nested))
            return nested;

        string flightTime = FindFlightTimeRecursive(data);
        if (!string.IsNullOrWhiteSpace(flightTime))
            return flightTime;

        return FindFlightTimeRecursive(envelope);
    }

    private string FindFlightTimeRecursive(JToken token)
    {
        if (token == null)
        {
            return null;
        }

        string flightTime = null;
        if (token is JObject)
        {
            flightTime = GetString(token, "flightTime");
            if (!string.IsNullOrWhiteSpace(flightTime))
            {
                return flightTime;
            }
        }

        if (token is JObject objectToken)
        {
            foreach (var property in objectToken.Properties())
            {
                flightTime = FindFlightTimeRecursive(property.Value);
                if (!string.IsNullOrWhiteSpace(flightTime))
                {
                    return flightTime;
                }
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
            {
                flightTime = FindFlightTimeRecursive(item);
                if (!string.IsNullOrWhiteSpace(flightTime))
                {
                    return flightTime;
                }
            }
        }

        return null;
    }

    private void PublishFlightTime(string flightTime)
    {
        if (string.IsNullOrWhiteSpace(flightTime))
        {
            return;
        }

        if (TryParseElapsedTime(flightTime, out float elapsedFromStart))
        {
            FlightTimeReceived?.Invoke(flightTime, elapsedFromStart);
            return;
        }

        if (!TryParseLocalTime(flightTime, out DateTime current))
        {
            return;
        }

        DateTime? start;
        lock (dataLock)
        {
            if (!taskStartTime.HasValue)
            {
                taskStartTime = current;
            }

            start = taskStartTime;
        }

        if (!start.HasValue)
        {
            return;
        }

        float elapsedSeconds = Mathf.Max(0f, (float)(current - start.Value).TotalSeconds);
        FlightTimeReceived?.Invoke(flightTime, elapsedSeconds);
    }

    private static bool TryParseElapsedTime(string value, out float elapsedSeconds)
    {
        elapsedSeconds = 0f;
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf(':') < 0 || value.IndexOf('-') >= 0)
        {
            return false;
        }

        string[] parts = value.Trim().Split(':');
        if (parts.Length != 2 && parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out int first) ||
            !int.TryParse(parts[parts.Length - 2], out int minutes) ||
            !float.TryParse(parts[parts.Length - 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float seconds))
        {
            return false;
        }

        elapsedSeconds = parts.Length == 2
            ? first * 60f + seconds
            : first * 3600f + minutes * 60f + seconds;
        return elapsedSeconds >= 0f;
    }

    private static readonly string[] FlightTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss.ffffff",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ff",
        "yyyy-MM-dd HH:mm:ss.f",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/M/d H:mm:ss.ffffff",
        "yyyy/M/d H:mm:ss",
    };

    private static bool IsNewerFlightTime(string candidate, string previous)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        if (string.IsNullOrWhiteSpace(previous))
            return true;
        if (TryParseLocalTime(candidate, out DateTime next) && TryParseLocalTime(previous, out DateTime prev))
            return next >= prev;
        return string.CompareOrdinal(candidate, previous) >= 0;
    }

    private static bool TryParseLocalTime(string value, out DateTime time)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            time = default;
            return false;
        }

        if (DateTime.TryParseExact(
            value.Trim(),
            FlightTimeFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out time))
        {
            return true;
        }

        return DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces,
            out time);
    }

    private string GetString(JToken token, params string[] names)
    {
        return GetStringMapped(token, null, names);
    }

    private float? GetFloat(JToken token, params string[] names)
    {
        return GetFloatMapped(token, null, names);
    }

    private string GetMappedString(JToken token, string canonical, params string[] extraNames)
    {
        return GetStringMapped(token, canonical, extraNames);
    }

    private float? GetMappedFloat(JToken token, string canonical, params string[] extraNames)
    {
        return GetFloatMapped(token, canonical, extraNames);
    }

    private double? GetMappedDouble(JToken token, string canonical, params string[] extraNames)
    {
        return GetDoubleMapped(token, canonical, extraNames);
    }

    private double? GetDoubleMapped(JToken token, string canonical, string[] extraNames)
    {
        if (token == null)
            return null;

        foreach (string name in EnumerateFieldNames(canonical, extraNames))
        {
            var value = GetPropertyIgnoreCase(token, name);
            if (value == null || value.Type == JTokenType.Null)
                continue;
            if (value.Type == JTokenType.String)
            {
                string text = value.Value<string>();
                if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                    return parsed;
                continue;
            }
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
                return value.Value<double>();
        }

        return null;
    }

    private static JToken GetPropertyIgnoreCase(JToken token, string name)
    {
        if (token == null || string.IsNullOrWhiteSpace(name))
            return null;
        if (token is JObject obj)
        {
            var property = obj.Property(name, StringComparison.OrdinalIgnoreCase);
            return property != null ? property.Value : null;
        }
        return token[name];
    }

    /// <summary>
    /// 取原始字符串：字段存在就返回 true（哪怕是空串），用来区分「没这个字段」和「字段是空的」。
    /// 告警这类会被覆盖的字段必须靠它判断要不要清空，否则空值会一直被忽略、旧值一直粘着。
    /// </summary>
    private bool TryGetRawString(JToken token, string canonical, out string value)
    {
        value = null;
        if (token == null)
            return false;

        foreach (string name in EnumerateFieldNames(canonical, new string[0]))
        {
            var prop = GetPropertyIgnoreCase(token, name);
            if (prop == null)
                continue;
            value = prop.Type == JTokenType.Null ? string.Empty : prop.ToString();
            return true;
        }

        return false;
    }

    private string GetStringMapped(JToken token, string canonical, string[] extraNames)
    {
        if (token == null)
            return null;

        foreach (string name in EnumerateFieldNames(canonical, extraNames))
        {
            var value = GetPropertyIgnoreCase(token, name);
            if (value == null || value.Type == JTokenType.Null)
                continue;
            string text = value.Type == JTokenType.String || value.Type == JTokenType.Integer || value.Type == JTokenType.Float
                ? value.ToString()
                : value.Value<string>();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private float? GetFloatMapped(JToken token, string canonical, string[] extraNames)
    {
        if (token == null)
            return null;

        foreach (string name in EnumerateFieldNames(canonical, extraNames))
        {
            var value = GetPropertyIgnoreCase(token, name);
            if (value == null || value.Type == JTokenType.Null)
                continue;
            if (value.Type == JTokenType.String)
            {
                string text = value.Value<string>();
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                    return parsed;
                continue;
            }
            float? number = value.Value<float?>();
            if (number.HasValue)
                return number.Value;
        }

        return null;
    }

    private IEnumerable<string> EnumerateFieldNames(string canonical, string[] extraNames)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(canonical) && seen.Add(canonical))
            yield return canonical;
        if (extraNames != null)
        {
            for (int i = 0; i < extraNames.Length; i++)
            {
                string name = extraNames[i];
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    yield return name;
            }
        }

        // 飞机高度驱动：DATA_FIELDS 里 alt 与 hight 是两个 json key，但周期表实测高度在 hight。
        // 只在取 alt（内部位置）时兼容 hight；面板取 hight 仍走独立 key。
        if (string.Equals(canonical, "alt", StringComparison.OrdinalIgnoreCase))
        {
            if (seen.Add("hight")) yield return "hight";
            if (seen.Add("height")) yield return "height";
        }

        // 事件表 detectors[] 用 detector_name，旧协议用 detector_type。
        if (string.Equals(canonical, "detector_type", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonical, "detectorType", StringComparison.OrdinalIgnoreCase))
        {
            if (seen.Add("detector_name")) yield return "detector_name";
            if (seen.Add("detectorName")) yield return "detectorName";
            if (seen.Add("detector_type")) yield return "detector_type";
            if (seen.Add("detectorType")) yield return "detectorType";
        }

        if (string.IsNullOrWhiteSpace(canonical))
            yield break;

        List<string> aliases = null;
        if (dataFields.TryGetValue(canonical, out aliases) && aliases != null)
        {
            for (int i = 0; i < aliases.Count; i++)
            {
                string alias = aliases[i];
                if (!string.IsNullOrWhiteSpace(alias) && seen.Add(alias))
                    yield return alias;
            }
        }
    }

    private int? GetInt(JToken token, params string[] names)
    {
        if (token == null || names == null)
            return null;

        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var value = token[name];
            if (value == null || value.Type == JTokenType.Null)
                continue;
            int? number = value.Value<int?>();
            if (number.HasValue)
                return number.Value;
        }

        return null;
    }

    private void ParseTimetableState(JToken data, JToken envelope = null)
    {
        if (data == null)
            return;

        string messageFlightTime = GetFlightTime(data, envelope);
        PublishFlightTime(messageFlightTime);

        lock (dataLock)
        {
            ApplyEventOrTimetableUnsafe(data, data["flightStates"] ?? data["event"], messageFlightTime);
        }
    }

    private void ParseEventTableData(JToken data, JToken envelope = null)
    {
        if (data == null)
            return;

        // 数据端用 EVENT_TABLE_DATA 推送事件/时刻表（与 TIMETABLE_STATE 同结构）。
        // 必须解析 state，并在记录带有经纬/姿态时增量合并位置；禁止整包丢弃。
        string messageFlightTime = GetFlightTime(data, envelope);
        PublishFlightTime(messageFlightTime);

        var eventToken = data["event"] ?? data["flightStates"] ?? data["records"];
        int recordCount = 0;
        if (eventToken != null)
        {
            recordCount = eventToken.Type == JTokenType.Array
                ? ((JArray)eventToken).Count
                : (eventToken.HasValues ? 1 : 0);
        }

        lock (dataLock)
        {
            latestEventTableFlightTime = messageFlightTime ?? string.Empty;
            eventTableFrameCount++;
            ApplyEventOrTimetableUnsafe(data, eventToken, messageFlightTime);
        }

        if (!eventTableSummaryLogged)
        {
            eventTableSummaryLogged = true;
            Log($"EVENT_TABLE_DATA first frame: flightTime={messageFlightTime ?? "-"}, records={recordCount}, source={(data["event"] != null ? "event" : (data["flightStates"] != null ? "flightStates" : (data["records"] != null ? "records" : "data")))}");
        }
    }

    private void ApplyEventOrTimetableUnsafe(JToken data, JToken recordsToken, string frameFlightTime)
    {
        string dataTrackId = GetMappedString(data, "trackId", "飞机ID", "飞机编号");
        IEnumerable<JToken> tokens;
        if (recordsToken == null)
        {
            if (string.IsNullOrWhiteSpace(dataTrackId) && data["lng"] == null && data["lat"] == null)
                return;
            tokens = new[] { data };
        }
        else if (recordsToken.Type == JTokenType.Array)
        {
            tokens = recordsToken.Children();
        }
        else
        {
            tokens = new[] { recordsToken };
        }

        foreach (var token in tokens)
        {
            if (token == null)
                continue;

            string trackId = dataTrackId ?? GetMappedString(token, "trackId", "飞机ID", "飞机编号");
            if (string.IsNullOrWhiteSpace(trackId))
                continue;

            MergeTimetableStateUnsafe(trackId, frameFlightTime, data, token);
            MergeDetectorsUnsafe(trackId, frameFlightTime, data, token);
            MergeFieldBagUnsafe(trackId, data, token);
        }
    }

    /// <summary>
    /// data.detectors[]：一架飞机多个雷达。detector_name 对面板上的雷达名称。
    /// </summary>
    private void MergeDetectorsUnsafe(string trackId, string frameFlightTime, JToken data, JToken token)
    {
        JToken detectors = GetPropertyIgnoreCase(token, "detectors")
            ?? GetPropertyIgnoreCase(data, "detectors");
        if (detectors == null || detectors.Type != JTokenType.Array)
            return;

        foreach (var item in detectors.Children())
        {
            if (item == null || item.Type != JTokenType.Object)
                continue;
            string detector = GetDetectorName(item);
            if (string.IsNullOrWhiteSpace(detector))
                continue;
            // 只读这一条雷达对象，不要回落到 data 根上的旧 detector_type。
            MergeTimetableStateUnsafe(trackId, frameFlightTime, item, item);
        }
    }

    private string GetDetectorName(JToken token)
    {
        if (token == null)
            return null;
        var direct = GetPropertyIgnoreCase(token, "detector_name")
            ?? GetPropertyIgnoreCase(token, "detectorName")
            ?? GetPropertyIgnoreCase(token, "detector_type")
            ?? GetPropertyIgnoreCase(token, "detectorType");
        if (direct != null && direct.Type != JTokenType.Null)
        {
            string text = direct.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }
        return GetMappedString(token, "detector_type", "探测器类型", "探测器名称");
    }

    private void MergeTimetableStateUnsafe(string trackId, string frameFlightTime, JToken data, JToken token)
    {
        latestStatesByTrack.TryGetValue(trackId, out var prev);
        var state = prev ?? new TrackTimetableState { trackId = trackId };
        state.trackId = trackId;

        // 每行自己的 flightTime（timetable 的「时间」列）优先，其次才是整帧时间。
        string rowFlightTime = GetMappedString(token, "flightTime") ?? GetMappedString(data, "flightTime");
        string effectiveTime = !string.IsNullOrWhiteSpace(rowFlightTime) ? rowFlightTime : frameFlightTime;

        // 本帧时间可能是空的：不沿用上一帧，标记出来让告警面板直接跳过。
        if (string.IsNullOrWhiteSpace(effectiveTime))
        {
            state.frameTimeMissing = true;
        }
        else
        {
            state.frameTimeMissing = false;
            state.flightTime = effectiveTime;
        }

        // 状态/告警：字段存在就覆盖（空值也要清掉），避免旧告警一直挂着。
        if (TryGetRawString(token, "state", out string nextState) || TryGetRawString(data, "state", out nextState))
            state.state = nextState ?? string.Empty;
        float? battery = GetMappedFloat(token, "battery", "电量(%)", "电量") ?? GetMappedFloat(data, "battery", "电量(%)", "电量");
        if (battery.HasValue) state.battery = battery.Value;
        float? speed = GetMappedFloat(token, "speed", "速度(米/秒)", "速度") ?? GetMappedFloat(data, "speed", "速度(米/秒)", "速度");
        if (speed.HasValue) state.speed = speed.Value;
        if (TryGetRawString(token, "alarm", out string alarm) || TryGetRawString(data, "alarm", out alarm))
            state.alarm = alarm ?? string.Empty;
        string detector = GetDetectorName(token) ?? GetDetectorName(data);
        if (detector != null) state.detectorType = detector;
        string rangeType = GetMappedString(token, "range_type") ?? GetMappedString(data, "range_type");
        if (rangeType != null) state.rangeType = rangeType;
        float? detectRange = GetMappedFloat(token, "detect_range") ?? GetMappedFloat(data, "detect_range");
        if (detectRange.HasValue) state.detectRange = detectRange.Value;
        float? yawAngle = GetMappedFloat(token, "yaw_angle", "扫描中心(度)", "传感器左右偏转角") ?? GetMappedFloat(data, "yaw_angle", "扫描中心(度)", "传感器左右偏转角");
        if (yawAngle.HasValue) { state.yawAngle = yawAngle.Value; state.hasYawAngle = true; }
        float? pitchAngle = GetMappedFloat(token, "pitch_angle", "俯仰中心(度)", "传感器上下偏转角") ?? GetMappedFloat(data, "pitch_angle", "俯仰中心(度)", "传感器上下偏转角");
        if (pitchAngle.HasValue) { state.pitchAngle = pitchAngle.Value; state.hasPitchAngle = true; }
        string center = GetMappedString(token, "center_point", "中心点") ?? GetMappedString(data, "center_point", "中心点");
        if (center != null) state.centerPoint = center;
        string center1 = GetMappedString(token, "center_point1", "中心点", "传感器位置") ?? GetMappedString(data, "center_point1", "中心点", "传感器位置");
        if (center1 != null) state.centerPoint1 = center1;
        float? yawScan = GetMappedFloat(token, "yaw_scan_angle", "扫描方位(度)", "传感器左右扫描范围") ?? GetMappedFloat(data, "yaw_scan_angle", "扫描方位(度)", "传感器左右扫描范围");
        if (yawScan.HasValue) state.yawScanAngle = yawScan.Value;
        float? pitchScan = GetMappedFloat(token, "pitch_scan_angle", "俯仰方位(度)", "传感器上下扫描范围") ?? GetMappedFloat(data, "pitch_scan_angle", "俯仰方位(度)", "传感器上下扫描范围");
        if (pitchScan.HasValue) state.pitchScanAngle = pitchScan.Value;
        string radar = GetMappedString(token, "radar_status", "探测器状态", "雷达状态") ?? GetMappedString(data, "radar_status", "探测器状态", "雷达状态");
        if (radar != null) state.radarStatus = radar;

        latestStatesByTrack[trackId] = state;
        UpsertSensorSnapshotUnsafe(trackId, detector, rangeType, detectRange, yawAngle, pitchAngle, center, center1, yawScan, pitchScan, radar, state.flightTime);
        if (string.IsNullOrWhiteSpace(primaryTrackId))
            primaryTrackId = trackId;
    }

    private void UpsertSensorSnapshotUnsafe(
        string trackId,
        string detector,
        string rangeType,
        float? detectRange,
        float? yawAngle,
        float? pitchAngle,
        string center,
        string center1,
        float? yawScan,
        float? pitchScan,
        string radarStatus,
        string flightTime)
    {
        if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(detector))
            return;

        detector = detector.Trim();
        string key = trackId + "|" + detector;
        if (removedSensorKeys.Contains(key))
            return;
        if (!latestSensorsByKey.TryGetValue(key, out var sensor) || sensor == null)
            sensor = new TrackTimetableState { trackId = trackId, detectorType = detector };

        sensor.trackId = trackId;
        sensor.detectorType = detector;
        if (!string.IsNullOrWhiteSpace(flightTime) && IsNewerFlightTime(flightTime, sensor.flightTime))
            sensor.flightTime = flightTime;
        if (rangeType != null) sensor.rangeType = rangeType;
        if (detectRange.HasValue) sensor.detectRange = detectRange.Value;
        if (yawAngle.HasValue) { sensor.yawAngle = yawAngle.Value; sensor.hasYawAngle = true; }
        if (pitchAngle.HasValue) { sensor.pitchAngle = pitchAngle.Value; sensor.hasPitchAngle = true; }
        if (center != null) sensor.centerPoint = center;
        if (center1 != null) sensor.centerPoint1 = center1;
        if (yawScan.HasValue) sensor.yawScanAngle = yawScan.Value;
        if (pitchScan.HasValue) sensor.pitchScanAngle = pitchScan.Value;
        if (radarStatus != null) sensor.radarStatus = radarStatus;
        latestSensorsByKey[key] = sensor;
    }

    private void MergePositionFromTokenUnsafe(string trackId, string frameFlightTime, JToken data, JToken token)
    {
        double? lng = GetMappedDouble(token, "lng") ?? GetMappedDouble(data, "lng");
        double? lat = GetMappedDouble(token, "lat") ?? GetMappedDouble(data, "lat");
        float? alt = GetMappedFloat(token, "alt") ?? GetMappedFloat(token, "hight") ?? GetMappedFloat(data, "alt") ?? GetMappedFloat(data, "hight");
        float? heading = GetMappedFloat(token, "heading") ?? GetMappedFloat(data, "heading");
        float? pitch = GetMappedFloat(token, "pitch") ?? GetMappedFloat(data, "pitch");
        float? roll = GetMappedFloat(token, "roll") ?? GetMappedFloat(data, "roll");

        if (!lng.HasValue && !lat.HasValue && !alt.HasValue && !heading.HasValue && !pitch.HasValue && !roll.HasValue)
            return;

        latestPositionsByTrack.TryGetValue(trackId, out var prev);
        var position = prev ?? new TrackPosition { trackId = trackId };
        position.trackId = trackId;
        if (!string.IsNullOrWhiteSpace(frameFlightTime))
            position.flightTime = frameFlightTime;
        if (lng.HasValue) position.lng = lng.Value;
        if (lat.HasValue) position.lat = lat.Value;
        if (alt.HasValue) position.alt = alt.Value;
        if (heading.HasValue) position.heading = heading.Value;
        if (pitch.HasValue) position.pitch = pitch.Value;
        if (roll.HasValue) position.roll = roll.Value;

        latestPositionsByTrack[trackId] = position;
        if (string.IsNullOrWhiteSpace(primaryTrackId))
            primaryTrackId = trackId;
        UpdateTurnRate(trackId, position);
    }

    private void ParseCycleTableData(JToken data, JToken envelope = null)
    {
        if (data == null)
        {
            return;
        }

        string messageFlightTime = GetFlightTime(data, envelope);
        PublishFlightTime(messageFlightTime);

        // 协议是 data.records；数据端也可能把 lng/hight 直接放在 data 上。
        var recordsToken = data["records"] ?? data["record"] ?? data["cycle"] ?? data["flightStates"];
        if (recordsToken == null)
        {
            if (GetPropertyIgnoreCase(data, "lng") != null ||
                GetPropertyIgnoreCase(data, "lat") != null ||
                GetPropertyIgnoreCase(data, "hight") != null ||
                GetPropertyIgnoreCase(data, "alt") != null)
            {
                recordsToken = data;
            }
            else
            {
                return;
            }
        }

        lock (dataLock)
        {
            string flightTime = messageFlightTime;
            string dataTrackId = GetMappedString(data, "trackId");
            if (latestCycleFlightTime != flightTime)
            {
                latestCycleRecords.Clear();
                latestCycleUpdatedTrackIds.Clear();
                latestCycleFlightTime = flightTime;
            }

            if (recordsToken is JArray recordArray)
            {
                foreach (var recordToken in recordArray)
                {
                    ParseCycleRecordUnsafe(flightTime, dataTrackId, recordToken);
                }
            }
            else
            {
                ParseCycleRecordUnsafe(flightTime, dataTrackId, recordsToken);
            }
        }

        if (!cycleTableSummaryLogged)
        {
            cycleTableSummaryLogged = true;
            var sample = recordsToken is JArray arr && arr.Count > 0 ? arr[0] : recordsToken;
            string keys = sample is JObject obj
                ? string.Join(",", obj.Properties().Select(p => p.Name))
                : sample?.Type.ToString();
            Log($"CYCLE_TABLE_DATA first frame: flightTime={messageFlightTime ?? "-"}, trackId={GetMappedString(data, "trackId") ?? "-"}, keys={keys}");
        }
    }

    private void ParseCycleRecordUnsafe(string flightTime, string dataTrackId, JToken recordToken)
    {
        if (recordToken == null)
        {
            return;
        }

        // 飞机ID优先用 data 层级的 trackId（如 "UAV-001"），不是 records 内部的
        string trackId = dataTrackId ?? GetMappedString(recordToken, "trackId");
        double? lng = GetMappedDouble(recordToken, "lng");
        double? lat = GetMappedDouble(recordToken, "lat");
        float? alt = GetMappedFloat(recordToken, "alt") ?? GetMappedFloat(recordToken, "hight");
        float? heading = GetMappedFloat(recordToken, "heading");
        float? pitch = GetMappedFloat(recordToken, "pitch");
        float? roll = GetMappedFloat(recordToken, "roll");

        if (string.IsNullOrWhiteSpace(trackId))
        {
            trackId = ResolveTrackIdForCycleRecordUnsafe(lng ?? 0d, lat ?? 0d);
        }

        int cycleId = GetInt(recordToken, "cycleId") ?? 0;
        PublishFlightTimeFromCycleId(flightTime, cycleId);
        var record = new CycleTableRecord
        {
            flightTime = flightTime,
            trackId = trackId,
            cycleId = cycleId,
            lng = lng ?? 0d,
            lat = lat ?? 0d,
            alt = alt ?? 0f,
            heading = heading ?? 0f,
            pitch = pitch ?? 0f,
            roll = roll ?? 0f,
            radarStatus = GetMappedString(recordToken, "radar_status", "探测器状态", "雷达状态") ?? string.Empty,
        };

        latestCycleRecords.Add(record);

        if (!string.IsNullOrWhiteSpace(trackId))
        {
            latestCycleUpdatedTrackIds.Add(trackId);
            MergeTimetableStateUnsafe(trackId, flightTime, null, recordToken);
            MergePositionFromTokenUnsafe(trackId, flightTime, null, recordToken);
            MergeFieldBagUnsafe(trackId, null, recordToken);
            ApplyFieldBagToPositionUnsafe(trackId, flightTime);
        }
    }

    private void ApplyFieldBagToPositionUnsafe(string trackId, string frameFlightTime)
    {
        if (string.IsNullOrWhiteSpace(trackId) ||
            !latestValuesByTrack.TryGetValue(trackId, out var bag) || bag == null)
            return;

        double? lng = ReadBagDouble(bag, "lng");
        double? lat = ReadBagDouble(bag, "lat");
        float? alt = ReadBagFloat(bag, "hight") ?? ReadBagFloat(bag, "alt") ?? ReadBagFloat(bag, "height");
        float? heading = ReadBagFloat(bag, "heading");
        float? pitch = ReadBagFloat(bag, "pitch");
        float? roll = ReadBagFloat(bag, "roll");
        if (!lng.HasValue && !lat.HasValue && !alt.HasValue && !heading.HasValue && !pitch.HasValue && !roll.HasValue)
            return;

        latestPositionsByTrack.TryGetValue(trackId, out var prev);
        var position = prev ?? new TrackPosition { trackId = trackId };
        position.trackId = trackId;
        if (!string.IsNullOrWhiteSpace(frameFlightTime))
            position.flightTime = frameFlightTime;
        if (lng.HasValue) position.lng = lng.Value;
        if (lat.HasValue) position.lat = lat.Value;
        if (alt.HasValue) position.alt = alt.Value;
        if (heading.HasValue) position.heading = heading.Value;
        if (pitch.HasValue) position.pitch = pitch.Value;
        if (roll.HasValue) position.roll = roll.Value;
        latestPositionsByTrack[trackId] = position;
        if (string.IsNullOrWhiteSpace(primaryTrackId))
            primaryTrackId = trackId;
        UpdateTurnRate(trackId, position);
    }

    private static float? ReadBagFloat(Dictionary<string, string> bag, string key)
    {
        if (bag == null || string.IsNullOrWhiteSpace(key))
            return null;
        if (!bag.TryGetValue(key, out string text) || string.IsNullOrWhiteSpace(text))
            return null;
        if (float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed))
            return parsed;
        return null;
    }

    private static double? ReadBagDouble(Dictionary<string, string> bag, string key)
    {
        if (bag == null || string.IsNullOrWhiteSpace(key))
            return null;
        if (!bag.TryGetValue(key, out string text) || string.IsNullOrWhiteSpace(text))
            return null;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return null;
    }

    private void MergeFieldBagUnsafe(string trackId, JToken data, JToken token)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return;
        if (!latestValuesByTrack.TryGetValue(trackId, out var bag) || bag == null)
        {
            bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            latestValuesByTrack[trackId] = bag;
        }

        MergeTokenIntoFieldBagUnsafe(bag, token);
        if (data != null && !ReferenceEquals(data, token))
            MergeTokenIntoFieldBagUnsafe(bag, data);
    }

    private void MergeTokenIntoFieldBagUnsafe(Dictionary<string, string> bag, JToken token)
    {
        if (!(token is JObject obj) || bag == null)
            return;

        foreach (var property in obj.Properties())
        {
            string name = property.Name;
            if (string.IsNullOrWhiteSpace(name) ||
                name == "records" || name == "record" || name == "event" ||
                name == "flightStates" || name == "tracks" || name == "plans" || name == "positions")
                continue;

            var value = property.Value;
            if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Object || value.Type == JTokenType.Array)
                continue;

            string text = value.Type == JTokenType.Float || value.Type == JTokenType.Integer
                ? value.ToString(Newtonsoft.Json.Formatting.None)
                : value.ToString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            bag[name] = text;
            if (dataFields.Count == 0)
                continue;

            foreach (var kv in dataFields)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    bag[kv.Key] = text;
                    break;
                }
                if (kv.Value == null)
                    continue;
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    if (string.Equals(kv.Value[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        bag[kv.Key] = text;
                        break;
                    }
                }
            }
        }
    }

    private void PublishFlightTimeFromCycleId(string flightTime, int cycleId)
    {
        if (cycleId <= 0 || HasUsableFlightTime(flightTime))
        {
            return;
        }

        // The transmitted cycle tables use cycleId 1..601 for 00:00.0..10:00.0.
        // This is only a fallback for senders that omit flightTime from the JSON frame.
        float elapsedSeconds = cycleId - 1;
        FlightTimeReceived?.Invoke($"cycle:{cycleId}", elapsedSeconds);
    }

    private static bool HasUsableFlightTime(string flightTime)
    {
        return TryParseElapsedTime(flightTime, out _) || TryParseLocalTime(flightTime, out _);
    }

    private string ResolveTrackIdForCycleRecordUnsafe(double lng, double lat)
    {
        string bestTrackId = null;
        float bestDistance = float.MaxValue;

        foreach (var kv in latestPositionsByTrack)
        {
            if (latestCycleUpdatedTrackIds.Contains(kv.Key) || kv.Value == null)
            {
                continue;
            }

            double dLng = kv.Value.lng - lng;
            double dLat = kv.Value.lat - lat;
            float distance = (float)(dLng * dLng + dLat * dLat);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestTrackId = kv.Key;
            }
        }

        if (string.IsNullOrWhiteSpace(bestTrackId))
        {
            foreach (var kv in latestStatesByTrack)
            {
                if (!latestCycleUpdatedTrackIds.Contains(kv.Key))
                {
                    return kv.Key;
                }
            }
        }

        return bestTrackId;
    }

    private void UpdateTurnRate(string trackId, TrackPosition position)
    {
        if (string.IsNullOrWhiteSpace(position.flightTime))
        {
            previousHeadingByTrack[trackId] = position.heading;
            return;
        }

        if (!DateTime.TryParse(position.flightTime, out var currentTime))
        {
            previousHeadingByTrack[trackId] = position.heading;
            return;
        }

        if (previousHeadingByTrack.TryGetValue(trackId, out var previousHeading) &&
            previousTimeByTrack.TryGetValue(trackId, out var previousTime))
        {
            double deltaSeconds = (currentTime - previousTime).TotalSeconds;
            if (deltaSeconds > 0.001)
            {
                float deltaHeading = Mathf.DeltaAngle(previousHeading, position.heading);
                _primaryTurnRateCacheByTrack[trackId] = deltaHeading / (float)deltaSeconds;
            }
        }

        previousHeadingByTrack[trackId] = position.heading;
        previousTimeByTrack[trackId] = currentTime;
    }

    private readonly Dictionary<string, float> _primaryTurnRateCacheByTrack = new Dictionary<string, float>();

    private TrackPosition GetPrimaryPositionUnsafe()
    {
        if (!string.IsNullOrWhiteSpace(primaryTrackId) && latestPositionsByTrack.TryGetValue(primaryTrackId, out var position))
        {
            return position;
        }

        foreach (var kv in latestPositionsByTrack)
        {
            primaryTrackId = kv.Key;
            return kv.Value;
        }

        return null;
    }

    private TrackTimetableState GetPrimaryStateUnsafe()
    {
        if (!string.IsNullOrWhiteSpace(primaryTrackId) && latestStatesByTrack.TryGetValue(primaryTrackId, out var state))
        {
            return state;
        }

        foreach (var kv in latestStatesByTrack)
        {
            primaryTrackId = kv.Key;
            return kv.Value;
        }

        return null;
    }

    private float GetPrimaryTurnRateUnsafe()
    {
        if (!string.IsNullOrWhiteSpace(primaryTrackId) && _primaryTurnRateCacheByTrack.TryGetValue(primaryTrackId, out var rate))
        {
            return rate;
        }

        return 0f;
    }

    private void OnDisable()
    {
        if (!isDuplicateInstance)
        {
            SaveSensorCatalog();
            DisconnectImmediate();
        }
    }

    private void OnApplicationQuit()
    {
        if (!isDuplicateInstance)
            SaveSensorCatalog();
        DisconnectImmediate();
    }

    private bool OnWantsToQuit()
    {
        DisconnectImmediate();
        return true;
    }

    private void OnDestroy()
    {
        Application.wantsToQuit -= OnWantsToQuit;
        DisconnectImmediate();

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Log(string message)
    {
        if (enableDebugLog)
        {
            Debug.Log($"[FlightDataStreamReceiver] {message}");
        }
    }

    private void LogIncomingType(string type)
    {
        if (enableDebugLog && (logIncomingMessageTypes || logIncomingRawPayloads))
        {
            Debug.Log($"[FlightDataStreamReceiver] Received message type={type}");
        }
    }

    private void LogError(string message)
    {
        if (enableDebugLog)
        {
            Debug.LogError($"[FlightDataStreamReceiver] {message}");
        }
    }

    private void LogWarning(string message)
    {
        if (enableDebugLog)
        {
            Debug.LogWarning($"[FlightDataStreamReceiver] {message}");
        }
    }
}
