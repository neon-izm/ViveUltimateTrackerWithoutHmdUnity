// UltimateTrackerPoseApplier.cs
// SimpleUltimateTrackerReceiver から受け取った Pose を Transform に適用する最小スクリプト。
// - スレッドセーフにイベントを受け取り、Update() で反映
// - 1台/複数台の両対応 (trackerIndex=-1 で最初にアクティブなスロットを使用)
// - 軽量な座標調整: スケール、Y/Z入替、Z反転、回転オフセット

using System;
using UnityEngine;
using ViveUltimateTrackerStandalone.Runtime.Scripts;
using ViveUltimateTrackerStandalone.Runtime.Scripts.Protocol;

namespace ViveUltimateTrackerStandalone.Runtime.Scripts
{
    [DisallowMultipleComponent]
    public class UltimateTrackerPoseApplier : MonoBehaviour
    {
        [Header("Source")] [SerializeField] private UltimateTrackerReceiver receiver;

        [Tooltip("-1=最初のアクティブなトラッカー。0..4=固定スロット")] [SerializeField]
        private int trackerIndex = -1;

        [Header("Target")] [SerializeField] private Transform target; // 未設定時は自分の transform

        // ランタイム状態
        private readonly object _lock = new object();
        private bool _hasPending;

        private Vector3 _pendingPos;

        private Quaternion _pendingRot;
        private int _pendingTrackerIndex = -1;
        private ushort _lastPacketIndex;
        private long _lastUpdateTicks;

        private long _lastAppliedTicks;

        private void Reset()
        {
            target = transform;
        }

        private void OnEnable()
        {
            if (receiver == null) receiver = FindObjectOfType<UltimateTrackerReceiver>();
            if (target == null) target = transform;
            if (receiver != null)
            {
                receiver.OnTrackerPose += OnTrackerUnityPose;
                receiver.OnTrackerDisconnected += OnTrackerDisconnected;
            }
        }

        private void OnDisable()
        {
            if (receiver != null)
            {
                receiver.OnTrackerPose -= OnTrackerUnityPose;
                receiver.OnTrackerDisconnected -= OnTrackerDisconnected;
            }
        }

        private void OnTrackerUnityPose(SimpleTrackerState state)
        {
            lock (_lock)
            {
                if (trackerIndex < 0 || trackerIndex == state.Index)
                {
                    _pendingPos = state.UnityPositionAdjusted;
                    _pendingRot = state.UnityRotationAdjusted;
                    _pendingTrackerIndex = state.Index;
                    _lastPacketIndex = state.PacketIndex;
                    _lastUpdateTicks = state.LastUpdateUtcTicks;
                    _hasPending = true;
                }
            }
        }

        private void Update()
        {
            bool hasPending;
            Vector3 pos = Vector3.zero;
            Quaternion rot = Quaternion.identity;
            lock (_lock)
            {
                hasPending = _hasPending;
                if (hasPending)
                {
                    pos = _pendingPos;
                    rot = _pendingRot;
                    _hasPending = false;
                }
            }

            if (hasPending)
            {
                target.position = pos;
                target.rotation = rot;
                _lastAppliedTicks = DateTime.UtcNow.Ticks;
            }
        }

        private void OnTrackerDisconnected(int idx)
        {
            if (trackerIndex < 0 && _pendingTrackerIndex == idx) _pendingTrackerIndex = -1;
        }

        public string GetDebugInfo()
        {
            TimeSpan age = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - _lastUpdateTicks));
            return $"trackerIndex={_pendingTrackerIndex} pkt={_lastPacketIndex} ageMs={age.TotalMilliseconds:F0}";
        }
    }
}