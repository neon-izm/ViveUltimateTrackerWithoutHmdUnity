using System;
using System.Collections.Generic;
using System.Linq;

namespace ViveUltimateTrackerStandalone.Runtime.Scripts.Protocol
{
    /// <summary>
    /// 軽量なプロトコル定数/構造体群。ロジックを持たない。
    /// </summary>
    public static class TrackerProtocol
    {
        public const int VendorId = 0x0BB4;
        public const int ProductId = 0x0350;

        public const byte DRESP_TRACKER_INCOMING = 0x28;
        public const byte DRESP_PAIR_EVENT = 0x18;
        public const byte DRESP_TRACKER_RF_STATUS = 0x1E;
        public const byte DRESP_TRACKER_NEW_RF_STATUS = 0x1D;
        public const ushort TYPE_ACK = 0x101;
        public const ushort TYPE_POSE = 0x110;

        public const string ACK_TRACKING_MODE = "ATM";
        public const string ACK_TRACKING_HOST = "ATH";
        public const string ACK_WIFI_HOST = "AWH";
        public const string ACK_ROLE_ID = "ARI";
        public const string ACK_NEW_ID = "ANI";
        public const string ACK_END_MAP = "ALE";
        public const string ACK_FW = "FW";
        public const string ACK_WIFI_COUNTRY = "Wc";

        public const int TRACKING_MODE_SLAM_HOST = 21;
        public const int TRACKING_MODE_SLAM_CLIENT = 20;

        public static string MacToString(byte[] mac) =>
            mac == null ? "null" : string.Join(":", mac.Select(x => x.ToString("X2")));

        public static bool MacEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 1; i < a.Length; i++)
                if (a[i] != b[i])
                    return false; // 先頭1バイト無視
            return true;
        }

        public static ushort ReadUInt16Le(byte[] data, int offset) => (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    /// <summary>
    /// トラッカーの状態情報
    /// </summary>
    [Serializable]
    public class SimpleTrackerState
    {
        public int Index;
        public UnityEngine.Vector3 Position;
        public UnityEngine.Quaternion Rotation;
        public TrackerTrackingState TrackingState; // int -> enum
        public int Buttons;
        public ushort PacketIndex;
        public long LastUpdateUtcTicks;
        public bool IsActive => (DateTime.UtcNow.Ticks - LastUpdateUtcTicks) / TimeSpan.TicksPerMillisecond < 1000;
        public int TrackerIdNumber = -1;
        public int PoseLogCounter = 0;
        public bool HasHostMap;
        public bool HasHostEd;
        public int MapState;
        public int PoseStatusCode;
        public string PoseStatus;
        /// <summary>
        /// Unity座標系の位置
        /// </summary>
        public UnityEngine.Vector3 UnityPosition;
        /// <summary>
        /// Unity座標系の回転
        /// </summary>
        public UnityEngine.Quaternion UnityRotation;
        /// <summary>
        /// オフセット適用後のUnity座標系の位置
        /// Unity position after applying offset
        /// </summary>
        public UnityEngine.Vector3 UnityPositionAdjusted;
        /// <summary>
        /// オフセット適用後のUnity座標系の回転
        /// Unity rotation after applying offset
        /// </summary>
        public UnityEngine.Quaternion UnityRotationAdjusted;
        public string TrackingStateString => TrackingState switch
        {
            TrackerTrackingState.PoseAndRotation => "Pose + Rot",
            TrackerTrackingState.RotationOnly => "Rot only",
            TrackerTrackingState.PoseLostRotation => "Pose(lost) Rot",
            _ => "Unknown"
        };
    }

    public enum TrackerTrackingState
    {
        Unknown = 0,
        PoseAndRotation = 2,
        RotationOnly = 3,
        PoseLostRotation = 4,
    }

    public class PairEventInfo
    {
        public byte[] Mac;
        public bool IsUnpair;
        public int Slot;
        public int TrackerId;
        public bool IsHost;
        public bool IsNewAssignment;
    }

    public class RfStatusPair
    {
        public byte A;
        public byte B;
    }

    public class RfStatusInfo
    {
        public byte CmdId;
        public byte[] Raw;
        public List<RfStatusPair> Pairs;
        public DateTime TimestampUtc;

        public string ToSummary() =>
            Pairs == null ? "(none)" : string.Join(" ", Pairs.Select(p => $"{p.A:X2}{p.B:X2}"));
    }

    public class VendorStatusInfo
    {
        public byte[] Raw;
        public DateTime TimestampUtc;
        public byte Field0;
        public byte Field1;
        public byte Field2;
        public byte Field3;
        public string ToSummary() => $"F0={Field0:X2} F1={Field1:X2} F2={Field2:X2} F3={Field3:X2}";
    }

    public enum AckType
    {
        DeviceInfo,
        PlayerStatus,
        LambdaStatus,
        MapStatus,
        Wifi,
        Control,
        Other
    }

    /// <summary>
    /// Ackの情報
    /// </summary>
    public class AckInfo
    {
        public byte[] Mac;
        public byte[] Raw;
        public string Ascii;
        public string Category;
        public AckType Type;
        public int PacketIndex;
        public int? LambdaId;
        public int? StatusKeyId;
        public int? StatusValue;
        public string[] Args;
        public DateTime TimestampUtc;
    }

    public static class DongleProtocol
    {
        // ドングルコマンド
        public const byte DCMD_TX = 0x18;
        public const byte DCMD_GET_CR_ID = 0xF0;
        public const byte DCMD_QUERY_ROM_VERSION = 0xFF;
        public const byte DCMD_F4 = 0xF4; // 追加: 挙動切替の可能性

        // CR_ID 定数
        public const byte CR_ID_PCBID = 0x06;    // PCB ID
        public const byte CR_ID_SKUID = 0x07;    // SKU ID
        public const byte CR_ID_SN = 0x08;       // シリアルナンバー
        public const byte CR_ID_SHIP_SN = 0x09;  // 出荷時シリアルナンバー
        public const byte CR_ID_CAP_FPC = 0x11;  // キャパシタ FPC

        // TX サブコマンド
        public const byte TX_ACK_TO_MAC = 0x03;
        public const byte TX_ACK_TO_PARTIAL_MAC = 0x04;
    }
}