
using System;
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
        public event Action<SimpleTrackerState> OnTrackerPose; // 追加: RawPose (従来OnTrackerPoseと同等)
        public event Action<int> OnTrackerConnected; // tracker index
        public event Action<int> OnTrackerDisconnected; // tracker index
        public event Action<RfStatusInfo> OnRfStatus;
        public event Action<VendorStatusInfo> OnVendorStatus;
        public event Action<PairEventInfo> OnPairEvent;
        public event Action<AckInfo> OnAck;

        // States
        private readonly SimpleTrackerState[] _trackers = new SimpleTrackerState[5];

        private readonly byte[][] _macAddresses = new byte[5][]; // 6-byte MAC

        // トラッカーID(TrackerIdNumber) ごとのオフセット (Position + Rotation)
        // private readonly System.Collections.Generic.Dictionary<int, (Vector3 posOffset, Quaternion rotOffset)> _unityOffsets = new System.Collections.Generic.Dictionary<int, (Vector3, Quaternion)>();
        // 単一グローバルオフセット（全トラッカーに適用）
        private Matrix4x4 _unityOffset = Matrix4x4.identity;

        // 新規分割: HID/Logger/Parser
        private DongleHidClient _hidClient;
        private TrackerReportParser _parser;
        private TrackerLogger _logger;

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


        private bool _dongleInitialized = false; // pairing初期化フラグ

        private void Awake()
        {
            for (int i = 0; i < _trackers.Length; i++) _trackers[i] = new SimpleTrackerState { Index = i };
            _logger = new TrackerLogger(verboseLog, fileLoggingEnabled, logFilePath, appendLogFile);
            _hidClient = new DongleHidClient();
            _parser = new TrackerReportParser(_trackers, _macAddresses, _logger,
                idx => OnTrackerConnected?.Invoke(idx),
                idx => OnTrackerDisconnected?.Invoke(idx),
                st =>
                {
                    ApplyUnityOffsetAndEmit(st);
                    OnTrackerPose?.Invoke(st);
                }
                ,
                rf => OnRfStatus?.Invoke(rf),
                vend => OnVendorStatus?.Invoke(vend),
                pair => OnPairEvent?.Invoke(pair),
                ack => OnAck?.Invoke(ack));
        }

        private void Start()
        {
            if (autoConnectOnStart) Connect();
        }

        private void OnDestroy()
        {
            Disconnect();
            _logger?.Dispose();
            HidSharp.Utility.HidSharpLibrary.ManualShutdown();
        }


        public bool IsConnected => _connected;
        public IReadOnlyList<SimpleTrackerState> TrackerStates => _trackers;

        public IReadOnlyDictionary<int, (Vector3 posOffset, Quaternion rotOffset)> UnityOffsets =>
            null; // 廃止: 互換性目的で null

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
                for (int i = 0; i < _trackers.Length; i++)
                {
                    if (_macAddresses[i] != null)
                    {
                        _macAddresses[i] = null;
                        OnTrackerDisconnected?.Invoke(i);
                    }
                }
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
                var payload = new List<byte> { 0x00, 1, 1, 1, 1, 0, 0 }; // RF_BEHAVIOR_PAIR_DEVICE
                _hidClient.SendFeature(0x1D, payload); // DCMD_REQUEST_RF_CHANGE_BEHAVIOR
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

        private void ApplyUnityOffsetAndEmit(SimpleTrackerState st)
        {
            // 変換パイプライン:
            // 1) Parser が設定した UnityPosition / UnityRotation (半生データ) を取得
            // 2) 軸反転 (要求: posX, yaw, roll) を適用
            // 3) 行列オフセット (_unityOffset) を合成 (final = _unityOffset * raw)
            // 4) 調整後の値を SimpleTrackerState.UnityPositionAdjusted / UnityRotationAdjusted に反映
            // 5) OnTrackerUnityPose を発火

            // 基本姿勢取得
            var p = st.Position;
            var r = st.Rotation;

            // ---- 軸反転処理 ----
            // 位置 X 反転
            p.x = -p.x;

            // 回転 (Yaw, Roll) 反転: Euler 角で符号反転後 Quaternion 再構築
            if (true)
            {
                var e = r.eulerAngles;
                e.y = -e.y;
                if (e.y < 0) e.y += 360f;
                e.z = -e.z;
                if (e.z < 0) e.z += 360f;
                r = Quaternion.Euler(e);
            }

            // Quaternion 正規化 (念のため誤差蓄積防止)
            r = Quaternion.Normalize(r);

            st.UnityPosition = p;
            st.UnityRotation = r;
            // ---- グローバルオフセット行列適用 ----
            // オフセットは raw → offset * raw の順で左乗
            var rawMat = Matrix4x4.TRS(p, r, Vector3.one);
            var finalMat = _unityOffset * rawMat;

            // 行列から最終 position / rotation を抽出
            var finalPos = finalMat.GetColumn(3);
            // LookRotation( forward=z列, up=y列 )
            var finalRot = Quaternion.LookRotation(finalMat.GetColumn(2), finalMat.GetColumn(1));
            finalRot = Quaternion.Normalize(finalRot);

            // 状態へ反映
            st.UnityPositionAdjusted = finalPos;
            st.UnityRotationAdjusted = finalRot;
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

        private SimpleTrackerState FindByTrackerId(int trackerId)
        {
            for (int i = 0; i < _trackers.Length; i++)
            {
                if (_trackers[i].TrackerIdNumber == trackerId) return _trackers[i];
            }

            return null;
        }
    }
}