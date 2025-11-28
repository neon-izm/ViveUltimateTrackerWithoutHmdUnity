using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ViveUltimateTrackerStandalone.Runtime.Scripts.Protocol;
using ViveUltimateTrackerStandalone.Runtime.Scripts.IO;
using ViveUltimateTrackerStandalone.Runtime.Scripts.Infrastructure;

namespace ViveUltimateTrackerStandalone.Runtime.Scripts
{
    /// <summary>
    /// Ultimate Tracker ドングル受信クライアント。
    /// - Connect()/Disconnect() でドングル接続管理
    /// - OnTrackerPose イベントでトラッカー姿勢を通知
    /// - グローバルオフセット適用機能付き
    /// </summary>
    public class UltimateTrackerReceiver : MonoBehaviour
    {
        // Vendor/Product IDs -> 移動: TrackerProtocol でも公開
        public const int VendorId = TrackerProtocol.VendorId;
        public const int ProductId = TrackerProtocol.ProductId;

        // Public events
        public event Action<ViveUltimateTrackerState> OnTrackerPose; // メインスレッドで発火
        public event Action<ViveUltimateTrackerState> OnTrackerRawPose; // 生データ（別スレッド）
        public event Action<int> OnTrackerConnected; // tracker index
        public event Action<int> OnTrackerDisconnected; // tracker index
        public event Action<RfStatusInfo> OnRfStatus;
        public event Action<VendorStatusInfo> OnVendorStatus;
        public event Action<PairEventInfo> OnPairEvent;
        public event Action<AckInfo> OnAck;

        // States
        private readonly Dictionary<string, ViveUltimateTrackerState> _trackersByMac =
            new Dictionary<string, ViveUltimateTrackerState>();

        private readonly ConcurrentQueue<ViveUltimateTrackerState> _mainThreadPoseQueue =
            new ConcurrentQueue<ViveUltimateTrackerState>();

        private Matrix4x4 _unityOffset = Matrix4x4.identity;

        private DongleHidClient _hidClient;
        private TrackerReportParser _parser;
        private TrackerLogger _logger;
        private TrackerIdMapper _idMapper;

        private CancellationTokenSource _cts;
        private Task _readTask;
        private volatile bool _connected;

        private byte[] _reportBuffer;

        public int ReportsParsed => _parser?.ReportsParsed ?? 0;
        public int PosePacketsParsed => _parser?.PosePacketsParsed ?? 0;

        [SerializeField] private bool verboseLog = false;
        [SerializeField] private bool autoConnectOnStart = true;
        [SerializeField] private bool fileLoggingEnabled = true;
        [SerializeField] private string logFilePath = "log.txt";
        [SerializeField] private bool appendLogFile = true;

        [Header("Tracker ID Mapping")]
        [SerializeField] private string idMappingFilePath = "";

        private bool _dongleInitialized = false; // pairing初期化フラグ

        private void Awake()
        {
            _logger = new TrackerLogger(verboseLog, fileLoggingEnabled, logFilePath, appendLogFile);

            string mappingPath = GetIdMappingFilePath();
            _idMapper = new TrackerIdMapper(mappingPath, _logger);
            _idMapper.Load();

            _hidClient = new DongleHidClient();
            _parser = new TrackerReportParser(_trackersByMac, _logger,
                idx => OnTrackerConnected?.Invoke(idx),
                idx => OnTrackerDisconnected?.Invoke(idx),
                st =>
                {
                    OnTrackerRawPose?.Invoke(st);
                    ApplyUnityOffsetAndEnqueue(st);
                }
                ,
                rf => OnRfStatus?.Invoke(rf),
                vend => OnVendorStatus?.Invoke(vend),
                pair => OnPairEvent?.Invoke(pair),
                ack => OnAck?.Invoke(ack));

            _parser.SetIdMapper(_idMapper);
        }

        private void Start()
        {
            if (autoConnectOnStart) Connect();
        }

        private void Update()
        {
            while (_mainThreadPoseQueue.TryDequeue(out var state))
            {
                OnTrackerPose?.Invoke(state);
            }
        }

        private void OnDestroy()
        {
            Disconnect();
            _idMapper.Save();

            _logger?.Dispose();
            HidSharp.Utility.HidSharpLibrary.ManualShutdown();
        }


        public bool IsConnected => _connected;
        public IReadOnlyCollection<ViveUltimateTrackerState> TrackerStates => _trackersByMac.Values;

        public int ConnectedTrackerCount => _trackersByMac.Count;

        [Obsolete("UnityOffsets is deprecated. Use GlobalUnityOffsetMatrix instead.")]
        public IReadOnlyDictionary<int, (Vector3 posOffset, Quaternion rotOffset)> UnityOffsets => null;

        public Vector3 GlobalUnityPositionOffset => _unityOffset.GetColumn(3);

        public Quaternion GlobalUnityRotationOffset =>
            Quaternion.LookRotation(_unityOffset.GetColumn(2), _unityOffset.GetColumn(1));

        public Matrix4x4 GlobalUnityOffsetMatrix => _unityOffset;


        public bool Connect()
        {
            if (_connected) return true;
            try
            {
                if (!_hidClient.Open(VendorId, ProductId, Timeout.Infinite))
                {
                    _logger.Warn("Dongle not found or failed to open.");
                    return false;
                }

                _reportBuffer = _hidClient.CreateInputBuffer();
                _cts = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);
                _connected = true;
                _logger.Info("Dongle connected.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Connect exception: {ex.Message}\n{ex}");
                return false;
            }
        }


        public void Disconnect()
        {
            if (!_connected) return;
            try
            {
                _cts?.Cancel();
                _hidClient?.Close();
                _connected = false;
                try
                {
                    _readTask?.Wait(500);
                }
                catch
                {
                }

                _logger.Info("Dongle disconnected.");
                
                foreach (var kv in _trackersByMac)
                {
                    OnTrackerDisconnected?.Invoke(kv.Value.Index);
                }

                _trackersByMac.Clear();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Disconnect exception: {ex.Message}");
            }
        }

        private void ReadLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _hidClient.IsOpen)
            {
                int read = 0;
                try
                {
                    read = _hidClient.Read(_reportBuffer);
                    if (read <= 0) continue;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    _logger.Warn($"Read error: {ex.Message}");
                    Thread.Sleep(50);
                    continue;
                }

                _parser.ParseReport(_reportBuffer, read, (cmd, data) => _hidClient.SendFeature(cmd, data));
            }
        }

        /// <summary>
        /// ペアリング初期化
        /// 信用できない挙動なので非推奨
        /// </summary>
        [Obsolete("InitPairingIfNeeded is deprecated due to unreliable behavior.")]
        public void InitPairingIfNeeded()
        {
            if (_dongleInitialized) return;
            try
            {
                var payload = new List<byte> { 0x00, 1, 1, 1, 1, 0, 0 };
                _hidClient.SendFeature(0x1D, payload);
                _logger.Info("Sent pairing behavior (DCMD 0x1D)");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Pairing init failed: {ex.Message}");
            }

            _dongleInitialized = true;
        }

        public string LastPcbId { get; private set; }
        public string LastSkuId { get; private set; }
        public string LastSerial { get; private set; }
        public string LastShipSerial { get; private set; }
        public string LastCapFpc { get; private set; }
        public string LastRomVersion { get; private set; }

        public void QueryDeviceInfoRaw()
        {
            if (!_connected)
            {
                _logger.Warn("Not connected");
                return;
            }

            try
            {
                LastPcbId = _hidClient.GetCrId(DongleProtocol.CR_ID_PCBID);
                LastSkuId = _hidClient.GetCrId(DongleProtocol.CR_ID_SKUID);
                LastSerial = _hidClient.GetCrId(DongleProtocol.CR_ID_SN);
                LastShipSerial = _hidClient.GetCrId(DongleProtocol.CR_ID_SHIP_SN);
                LastCapFpc = _hidClient.GetCrId(DongleProtocol.CR_ID_CAP_FPC);
                LastRomVersion = _hidClient.QueryRomVersion();
                _logger.Info(
                    $"CR_ID: PCBID='{LastPcbId}' SKU='{LastSkuId}' SN='{LastSerial}' ShipSN='{LastShipSerial}' CAP_FPC='{LastCapFpc}' ROM='{LastRomVersion}'");
            }
            catch (Exception ex)
            {
                _logger.Warn($"QueryDeviceInfoRaw failed: {ex.Message}");
            }
        }

        private void ApplyUnityOffsetAndEnqueue(ViveUltimateTrackerState st)
        {
            var p = st.RawPosition;
            var r = st.RawRotation;

            p.x = -p.x;

            if (true)
            {
                var e = r.eulerAngles;
                e.y = -e.y;
                if (e.y < 0) e.y += 360f;
                e.z = -e.z;
                if (e.z < 0) e.z += 360f;
                r = Quaternion.Euler(e);
            }

            r = Quaternion.Normalize(r);

            st.UnityPosition = p;
            st.UnityRotation = r;

            // オフセット行列を適用して最終的な位置と回転を計算
            var finalPos = _unityOffset.MultiplyPoint3x4(p);
            var offsetRotation = Quaternion.LookRotation(
                _unityOffset.GetColumn(2), 
                _unityOffset.GetColumn(1)
            );
            var finalRot = Quaternion.Normalize(offsetRotation * r* Quaternion.Euler(90, 0, 0)); // トラッカーの前方が Unity の上向きなので補正

            st.UnityPositionAdjusted = finalPos;
            st.UnityRotationAdjusted = finalRot;

            _mainThreadPoseQueue.Enqueue(st);
        }

        /// <summary>
        /// グローバルオフセットを直接設定（Matrix4x4）。
        /// </summary>
        public void SetGlobalUnityOffset(Matrix4x4 offset)
        {
            _unityOffset = offset;
            _logger.Info($"SetGlobalUnityOffset matrix set");
        }

        /// <summary>
        /// グローバルオフセットを直接設定（T+R）。
        /// </summary>
        public void SetGlobalUnityOffset(Vector3 positionOffset, Quaternion rotationOffset)
        {
            var t = Matrix4x4.TRS(positionOffset, rotationOffset, Vector3.one);
            SetGlobalUnityOffset(t);
            _logger.Info($"SetGlobalUnityOffset pos={positionOffset} rot={rotationOffset}");
        }

        /// <summary>
        /// グローバルオフセットを初期化 (Identity)。
        /// </summary>
        public void ClearGlobalUnityOffset()
        {
            _unityOffset = Matrix4x4.identity;
            _logger.Info("Cleared global Unity offset (identity)");
        }



        /// <summary>
        /// IDマッピングファイルのパスを取得。
        /// </summary>
        private string GetIdMappingFilePath()
        {
            if (!string.IsNullOrEmpty(idMappingFilePath))
            {
                return idMappingFilePath;
            }

            // デフォルトパス: Application.persistentDataPath + "/tracker_id_map.json"
            // StreamingAssetsがあればそちらを使う
            string streamingAssetsPath = System.IO.Path.Combine(Application.streamingAssetsPath, "tracker_id_map.json");
            if (System.IO.File.Exists(streamingAssetsPath))
            {
                return streamingAssetsPath;
            }

            return System.IO.Path.Combine(Application.persistentDataPath, "tracker_id_map.json");
        }

        public void SaveIdMapping()
        {
            _idMapper.Save();
            _logger?.Info("Manually saved tracker ID mapping.");
        }

        public void LoadIdMapping()
        {
            _idMapper.Load();
            _logger?.Info("Manually loaded tracker ID mapping.");
        }

        public IReadOnlyDictionary<string, int> GetIdMappings()
        {
            return _idMapper?.GetMappings() ?? new Dictionary<string, int>();
        }
    }
}