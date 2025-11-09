using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ViveUltimateTrackerStandalone.Runtime.Scripts.Protocol;
using ViveUltimateTrackerStandalone.Runtime.Scripts.Infrastructure;
using ViveUltimateTrackerStandalone.Runtime.Scripts.IO;

namespace ViveUltimateTrackerStandalone.Runtime.Scripts.Protocol
{
    /// <summary>
    /// 入力レポートの解釈とイベント化を担当。状態配列は外部から供給される。
    /// </summary>
    public class TrackerReportParser
    {
        private readonly SimpleTrackerState[] _trackers;
        private readonly byte[][] _macs;
        private readonly TrackerLogger _log;
        private readonly Action<int> _onConnect;
        private readonly Action<int> _onDisconnect;
        private readonly Action<SimpleTrackerState> _onPose;
        private readonly Action<RfStatusInfo> _onRf;
        private readonly Action<VendorStatusInfo> _onVendor;
        private readonly Action<PairEventInfo> _onPair;
        private readonly Action<AckInfo> _onAck;

        private bool _verbose => _log?.Verbose == true;
        private int _poseLogInterval = 500;
        private int _currentHostTrackerId = -1;
        private string _wifiCountry = "US";

        public int ReportsParsed { get; private set; }
        public int PosePacketsParsed { get; private set; }

        public TrackerReportParser(SimpleTrackerState[] trackers, byte[][] macs, TrackerLogger log,
            Action<int> onConnect, Action<int> onDisconnect,
            Action<SimpleTrackerState> onPose, Action<RfStatusInfo> onRf,
            Action<VendorStatusInfo> onVendor, Action<PairEventInfo> onPair, Action<AckInfo> onAck)
        {
            _trackers = trackers;
            _macs = macs;
            _log = log;
            _onConnect = onConnect;
            _onDisconnect = onDisconnect;
            _onPose = onPose;
            _onRf = onRf;
            _onVendor = onVendor;
            _onPair = onPair;
            _onAck = onAck;
        }

        public void ParseReport(byte[] buf, int len, Func<byte, IReadOnlyList<byte>, byte[]> sendFeature)
        {
            if (len < 2) return;
            int off = (buf[0] == 0x00 && len > 1) ? 1 : 0;
            byte cmdId = buf[off];
            bool shouldLogRaw = true;
            if (cmdId == TrackerProtocol.DRESP_TRACKER_INCOMING && (len - off) >= 12)
            {
                ushort peekType = (ushort)(buf[off + 9] | (buf[off + 10] << 8));
                if (peekType == TrackerProtocol.TYPE_POSE) shouldLogRaw = false;
            }

            if (_verbose && shouldLogRaw)
                _log.Info(
                    $"Raw report off={off} cmdId=0x{cmdId:X2} len={len} first16={ParseHelper.HexSlice(buf, 0, 16)}");

            if (cmdId == TrackerProtocol.DRESP_PAIR_EVENT)
            {
                var payload = DongleHidClient.ParseHidResponse(buf, off, len - off);
                if (_verbose)
                    _log.Info(
                        $"PairEvent payloadLen={payload.Length} payloadHead={ParseHelper.HexSlice(payload, 0, 16)}");
                HandlePairEvent(payload, sendFeature);
                return;
            }

            if (cmdId is TrackerProtocol.DRESP_TRACKER_RF_STATUS or TrackerProtocol.DRESP_TRACKER_NEW_RF_STATUS)
            {
                var payload = DongleHidClient.ParseHidResponse(buf, off, len - off);
                var rfInfo = ParseRfStatus(cmdId, payload);
                _onRf?.Invoke(rfInfo);
                if (_verbose)
                    _log.Info(
                        $"RF status parsed cmd=0x{cmdId:X2} fields={rfInfo.ToSummary()} raw={ParseHelper.HexSlice(payload, 0, Math.Min(32, payload.Length))}");
                return;
            }

            if (cmdId == 0x29)
            {
                var payload = DongleHidClient.ParseHidResponse(buf, off, len - off);
                var vendorInfo = ParseVendorStatus(payload);
                _onVendor?.Invoke(vendorInfo);
                if (_verbose)
                    _log.Info(
                        $"Vendor/Status cmd 0x29 parsed {vendorInfo.ToSummary()} raw={ParseHelper.HexSlice(payload, 0, Math.Min(32, payload.Length))}");
                return;
            }

            if (cmdId != TrackerProtocol.DRESP_TRACKER_INCOMING)
            {
                if (_verbose) _log.Info($"Unknown cmdId 0x{cmdId:X2}");
                return;
            }

            int minHeader = 12;
            if (len - off < minHeader)
            {
                if (_verbose) _log.Warn($"Incoming header too short newLayout: available={len - off}");
                return;
            }

            int pktIdxByte = buf[off + 1] & 0xFF;
            byte[] deviceAddr = new byte[6];
            Array.Copy(buf, off + 2, deviceAddr, 0, 6);
            ushort typeMaybe = (ushort)(buf[off + 9] | (buf[off + 10] << 8));
            int dataLen = buf[off + 11] & 0xFF;
            int dataStart = off + 12;
            if (dataStart + dataLen > len)
            {
                if (_verbose)
                    _log.Warn(
                        $"Data length invalid (newLayout) type=0x{typeMaybe:X4} dataLen={dataLen} remaining={len - dataStart}");
                goto OLD_LAYOUT_FALLBACK;
            }

            if (typeMaybe == TrackerProtocol.TYPE_ACK)
            {
                byte[] ackPayload = new byte[dataLen];
                Array.Copy(buf, dataStart, ackPayload, 0, dataLen);
                ParseAck(deviceAddr, ackPayload, pktIdxByte);
                return;
            }

            if (typeMaybe == TrackerProtocol.TYPE_POSE)
            {
                ParsePose(deviceAddr, buf, dataStart, dataLen, (ushort)pktIdxByte);
                return;
            }

            if (_verbose && typeMaybe != TrackerProtocol.TYPE_POSE)
                _log.Warn(
                    $"Unknown inner type(new)=0x{typeMaybe:X4} head={ParseHelper.HexSlice(buf, dataStart, Math.Min(32, len - dataStart))}");
            return;
            OLD_LAYOUT_FALLBACK:
            if (len - off < 18)
            {
                if (_verbose) _log.Warn("Fallback old layout: insufficient length");
                return;
            }

            ushort pktIdxOld = (ushort)(buf[off + 1] | (buf[off + 2] << 8));
            Array.Copy(buf, off + 3, deviceAddr, 0, 6);
            int typeIndexOld = off + 15;
            if (len < typeIndexOld + 3)
            {
                if (_verbose) _log.Warn("Fallback old layout: type index overflow");
                return;
            }

            ushort typeOld = (ushort)((buf[typeIndexOld] << 8) | buf[typeIndexOld + 1]);
            int dataLenOld = buf[typeIndexOld + 2] & 0xFF;
            int dataStartOld = off + 0x0C;
            if (dataStartOld + dataLenOld > len)
            {
                if (_verbose) _log.Warn($"Fallback old layout data invalid type=0x{typeOld:X4} dataLen={dataLenOld}");
                return;
            }

            if (typeOld == TrackerProtocol.TYPE_ACK)
            {
                byte[] ackPayload = new byte[dataLenOld];
                Array.Copy(buf, dataStartOld, ackPayload, 0, dataLenOld);
                ParseAck(deviceAddr, ackPayload, pktIdxOld);
                return;
            }

            if (typeOld == TrackerProtocol.TYPE_POSE)
            {
                ParsePose(deviceAddr, buf, dataStartOld, dataLenOld, pktIdxOld);
                return;
            }

            if (_verbose && typeOld != TrackerProtocol.TYPE_POSE)
                _log.Warn(
                    $"Unknown inner type(old)=0x{typeOld:X4} head={ParseHelper.HexSlice(buf, dataStartOld, Math.Min(32, len - dataStartOld))}");
            return;
        }

        /// <summary>
        /// 最も重要な部分、ポーズパケットの解釈
        /// </summary>
        /// <param name="deviceAddr"></param>
        /// <param name="src"></param>
        /// <param name="start"></param>
        /// <param name="length"></param>
        /// <param name="pktIdx"></param>
        private void ParsePose(byte[] deviceAddr, byte[] src, int start, int length, ushort pktIdx)
        {
            if (length < 37) return;
            int trackerIndex = GetOrAssignTrackerIndex(deviceAddr);
            if (trackerIndex < 0) return;
            int o = start;
            byte btns = src[o + 1];
            float px = BitConverter.ToSingle(src, o + 2);
            float py = BitConverter.ToSingle(src, o + 6);
            float pz = BitConverter.ToSingle(src, o + 10);
            float rx = ParseHelper.HalfToSingle(TrackerProtocol.ReadUInt16Le(src, o + 14));
            float ry = ParseHelper.HalfToSingle(TrackerProtocol.ReadUInt16Le(src, o + 16));
            float rz = ParseHelper.HalfToSingle(TrackerProtocol.ReadUInt16Le(src, o + 18));
            float rw = ParseHelper.HalfToSingle(TrackerProtocol.ReadUInt16Le(src, o + 20));
            int trackingState = src[o + 36] & 0xFF;

            var state = _trackers[trackerIndex];
            state.Position = new Vector3(px, py, pz);
            state.Rotation = new Quaternion(rx, ry, rz, rw);

            state.TrackingState = (TrackerTrackingState)trackingState; // enum 化対応
            state.Buttons = btns;
            state.PacketIndex = pktIdx;
            state.LastUpdateUtcTicks = DateTime.UtcNow.Ticks;
            state.PoseLogCounter++;

            // フォールバックID: PairEvent / ACK で未確定ならスロット番号を暫定IDにする
            if (state.TrackerIdNumber < 0)
            {
                state.TrackerIdNumber = trackerIndex;
                if (_verbose)
                    _log.Info(
                        $"Fallback assign tracker ID={trackerIndex} for slot={trackerIndex} MAC={TrackerProtocol.MacToString(deviceAddr)} (no pair event yet)");
            }

            PosePacketsParsed++;
            if (_verbose && _poseLogInterval > 0 && (state.PoseLogCounter % _poseLogInterval == 0))
            {
                _log.Info(
                    $"PoseLog[{trackerIndex}] count={state.PoseLogCounter} pkt={pktIdx} pos=({px:F3},{py:F3},{pz:F3}) rot=({rx:F3},{ry:F3},{rz:F3},{rw:F3}) track={trackingState} btn=0x{btns:X2}");
            }

            _onPose?.Invoke(state);
        }

        /// <summary>
        /// ペアリング処理
        /// 謎が多いため直接コードから呼ぶべきではない
        /// </summary>
        /// <param name="payload"></param>
        /// <param name="sendFeature"></param>
        private void HandlePairEvent(byte[] payload, Func<byte, IReadOnlyList<byte>, byte[]> sendFeature)
        {
            if (payload.Length < 8) return;
            // ログから payloadHead=01 01 23 ... となっており先頭の 01 は不明フラグ、2番目 01 が is_unpair と推測
            // しかし現在の実装では payload[1] を isUnpair と扱い tracker が即 unpair されている。解除を防ぐため0番目を unpair に再評価。
            int isUnpairCandidate0 = payload[0] & 0xFF;
            int isUnpairCandidate1 = payload[1] & 0xFF;
            bool treatAsUnpair = isUnpairCandidate0 > 0 && isUnpairCandidate1 == 0; // 仮ロジック: 先頭が非ゼロかつ次が0なら解除
            // 実際の解除条件が不明なため、当面解除を抑止: 常に false
            treatAsUnpair = false;

            byte[] mac = new byte[Math.Max(0, payload.Length - 2)];
            Array.Copy(payload, 2, mac, 0, mac.Length);
            int slot = FindTrackerIndexByMac(mac);
            bool isNewAssignment = false;
            if (slot < 0)
            {
                slot = GetOrAssignTrackerIndex(mac);
                isNewAssignment = true;
            }

            int trackerNumericId = ExtractIdFromMac(mac, slot);
            if (slot >= 0) _trackers[slot].TrackerIdNumber = trackerNumericId;

            if (treatAsUnpair)
            {
                if (slot >= 0)
                {
                    _macs[slot] = null;
                    _onDisconnect?.Invoke(slot);
                    if (_currentHostTrackerId == trackerNumericId) _currentHostTrackerId = -1;
                    _log.Info(
                        $"(Suppressed) Tracker {trackerNumericId} unpaired MAC={TrackerProtocol.MacToString(mac)}");
                    _onPair?.Invoke(new PairEventInfo
                        { Mac = mac, IsUnpair = true, Slot = slot, TrackerId = trackerNumericId, IsHost = false });
                }

                return;
            }

            // ACK 初期設定
            AckSetRoleId(sendFeature, mac, 1);
            AckSetTrackingMode(sendFeature, mac, -1);
            bool becameHost = false;
            if (_currentHostTrackerId == -1 || IsHost(mac))
            {
                _currentHostTrackerId = trackerNumericId;
                becameHost = true;
                AckSetWifiCountry(sendFeature, mac);
                AckSetNewId(sendFeature, mac, trackerNumericId);
                AckSetFW(sendFeature, mac, 2);
                AckSetTrackingMode(sendFeature, mac, TrackerProtocol.TRACKING_MODE_SLAM_HOST);
            }
            else
            {
                AckSetTrackingHost(sendFeature, mac, _currentHostTrackerId);
                AckSetWifiHost(sendFeature, mac, _currentHostTrackerId);
                AckSetNewId(sendFeature, mac, trackerNumericId);
                AckSetFW(sendFeature, mac, 2);
                AckSetTrackingMode(sendFeature, mac, TrackerProtocol.TRACKING_MODE_SLAM_CLIENT);
                AckEndMap(sendFeature, mac);
            }

            _log.Info(
                $"PairEvent processed: mac={TrackerProtocol.MacToString(mac)} slot={slot} newId={trackerNumericId} host={_currentHostTrackerId} rawFirst2={payload[0]:X2},{payload[1]:X2}");
            _onPair?.Invoke(new PairEventInfo
            {
                Mac = mac, IsUnpair = false, Slot = slot, TrackerId = trackerNumericId, IsHost = becameHost,
                IsNewAssignment = isNewAssignment
            });
        }

        private int ExtractIdFromMac(byte[] mac, int fallbackSlot)
        {
            if (mac == null || mac.Length < 2) return fallbackSlot;
            byte b = mac[1];
            // 期待: '0'～'9' の ASCII
            if (b >= (byte)'0' && b <= (byte)'9') return b - (byte)'0';
            // 期待: 16進 ASCII の可能性 -> 1桁のみ扱う
            if (b >= (byte)'A' && b <= (byte)'F') return 10 + (b - (byte)'A');
            if (b >= (byte)'a' && b <= (byte)'f') return 10 + (b - (byte)'a');
            // それ以外はスロット番号を採用
            return fallbackSlot;
        }

        // 追補: ホスト判定とステータス更新ヘルパ
        private bool IsHost(byte[] mac)
        {
            int idx = FindTrackerIndexByMac(mac);
            if (idx < 0) return false;
            var state = _trackers[idx];
            return state.TrackerIdNumber >= 0 && state.TrackerIdNumber == _currentHostTrackerId;
        }

        private void UpdateTrackerStatusFromAck(byte[] mac, int keyId, int state)
        {
            int idx = FindTrackerIndexByMac(mac);
            if (idx < 0) return;
            var t = _trackers[idx];
            switch (keyId)
            {
                case 3: t.HasHostMap = state > 0; break;
                case 2: t.HasHostEd = state > 0; break;
                case 5: t.MapState = state; break;
                case 6:
                    t.PoseStatusCode = state;
                    t.PoseStatus = PoseStatusToString(state);
                    break;
            }
        }

        private void UpdateTrackerMapState(byte[] mac, int mapState)
        {
            int idx = FindTrackerIndexByMac(mac);
            if (idx < 0) return;
            _trackers[idx].MapState = mapState;
        }

        private string PoseStatusToString(int code)
        {
            switch (code)
            {
                case 0: return "NO_IMAGES_YET";
                case 1: return "NOT_INITIALIZED";
                case 2: return "OK";
                case 3: return "LOST";
                case 4: return "RECENTLY_LOST";
                case 5: return "SYSTEM_NOT_READY";
                default: return code.ToString();
            }
        }

        /// <summary>
        /// ACK ペイロードの解釈
        /// まだ理解できていない
        /// </summary>
        /// <param name="mac"></param>
        /// <param name="dataRaw"></param>
        /// <param name="pktIdx"></param>
        private void ParseAck(byte[] mac, byte[] dataRaw, int pktIdx)
        {
            if (dataRaw == null || dataRaw.Length == 0) return;
            string asciiFull = System.Text.Encoding.ASCII.GetString(dataRaw);
            string ascii = TrimLeadingNonPrintable(asciiFull);
            if (string.IsNullOrEmpty(ascii)) return;
            string sUpper = ascii.ToUpperInvariant();
            string category = sUpper.Substring(0, 1);
            var ack = new AckInfo
            {
                Mac = mac, Raw = dataRaw, Ascii = ascii, Category = category, PacketIndex = pktIdx,
                TimestampUtc = DateTime.UtcNow
            };
            if (category == "N") ack.Type = AckType.DeviceInfo;
            else if (category == "P")
            {
                ack.Type = AckType.PlayerStatus;
                if (sUpper.StartsWith("P61:") || sUpper.StartsWith("P63:") || sUpper.StartsWith("P64:"))
                {
                    int colon = ascii.IndexOf(':');
                    if (colon > 0)
                    {
                        string idStr = ascii.Substring(1, colon - 1);
                        if (int.TryParse(idStr, out int lambdaId)) ack.LambdaId = lambdaId;
                        string argsStr = ascii.Substring(colon + 1);
                        ack.Args = argsStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (ack.Args.Length >= 2 && ack.LambdaId == 61)
                        {
                            int keyId;
                            int state;
                            if (int.TryParse(ack.Args[0], out keyId) && int.TryParse(ack.Args[1], out state))
                            {
                                ack.StatusKeyId = keyId;
                                ack.StatusValue = state;
                                UpdateTrackerStatusFromAck(mac, keyId, state);
                            }
                        }
                    }
                }
            }
            else if (sUpper.StartsWith("LS"))
            {
                ack.Type = AckType.LambdaStatus;
                ack.Args = ascii.Substring(2).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            }
            else if (sUpper.StartsWith("MS"))
            {
                ack.Type = AckType.MapStatus;
                ack.Args = ascii.Substring(2).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (ack.Args.Length > 0)
                {
                    int mapState;
                    if (int.TryParse(ack.Args[0], out mapState)) UpdateTrackerMapState(mac, mapState);
                }
            }
            else if (sUpper.StartsWith("WC") || sUpper.StartsWith("WE")) ack.Type = AckType.Wifi;
            else if (sUpper.StartsWith("ATM") || sUpper.StartsWith("ATH") || sUpper.StartsWith("AWH") ||
                     sUpper.StartsWith("ANI") || sUpper.StartsWith("ARI") || sUpper.StartsWith("FW"))
            {
                ack.Type = AckType.Control;
                // ID 更新: ANI(新ID), ARI(ロール), ATM/ATH/AWH は ID を含む可能性あり
                TryUpdateIdFromAck(mac, ascii);
            }
            else ack.Type = AckType.Other;

            _onAck?.Invoke(ack);
            if (_verbose) _log.Info($"ACK parsed cat={ack.Category} type={ack.Type} ascii='{Shorten(ascii, 128)}'");
        }

        private static string TrimLeadingNonPrintable(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= ' ' && c <= '~') break; // printable ASCII
                i++;
            }

            return i > 0 && i < s.Length ? s.Substring(i) : (i >= s.Length ? string.Empty : s);
        }

        private void TryUpdateIdFromAck(byte[] mac, string ascii)
        {
            if (mac == null) return;
            int idx = FindTrackerIndexByMac(mac);
            if (idx < 0) return;
            string sUpper = ascii.ToUpperInvariant();
            string tailDigits = ExtractTrailingDigits(sUpper);
            if (tailDigits != null && int.TryParse(tailDigits, out int id))
            {
                if (id >= 0 && id < 64)
                {
                    _trackers[idx].TrackerIdNumber = id;
                    if (_verbose) _log.Info($"Updated tracker slot {idx} ID={id} from ACK '{ascii}'");
                }
            }
        }

        private string ExtractTrailingDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int end = s.Length - 1;
            int start = end;
            while (start >= 0 && char.IsDigit(s[start])) start--;
            if (start == end) return null; // no digits
            return s.Substring(start + 1);
        }

        // RF/Vendor ステータスの簡易パーサ
        private RfStatusInfo ParseRfStatus(byte cmdId, byte[] payload)
        {
            var info = new RfStatusInfo
                { CmdId = cmdId, Raw = payload, TimestampUtc = DateTime.UtcNow, Pairs = new List<RfStatusPair>() };
            if (payload != null)
            {
                for (int i = 0; i + 1 < payload.Length; i += 2)
                {
                    info.Pairs.Add(new RfStatusPair { A = payload[i], B = payload[i + 1] });
                }
            }

            return info;
        }

        private VendorStatusInfo ParseVendorStatus(byte[] payload)
        {
            var v = new VendorStatusInfo { Raw = payload, TimestampUtc = DateTime.UtcNow };
            if (payload != null && payload.Length >= 4)
            {
                v.Field0 = payload[0];
                v.Field1 = payload[1];
                v.Field2 = payload[2];
                v.Field3 = payload[3];
            }

            return v;
        }

        
        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        // ACK 送信ユーティリティ群
        private void SendAckTo(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac, string ackPayload)
        {
            if (mac == null || mac.Length != 6) return;
            var ackAscii = System.Text.Encoding.ASCII.GetBytes(ackPayload);
            var payloadBytes = new List<byte>(16 + ackAscii.Length);
            payloadBytes.Add(0x04);
            for (int i = 0; i < 6; i++) payloadBytes.Add(mac[i]);
            payloadBytes.Add(0);
            payloadBytes.Add(1);
            payloadBytes.Add((byte)ackAscii.Length);
            payloadBytes.AddRange(ackAscii);
            sendFeature(0x18, payloadBytes);
            if (_verbose) _log.Info($"SendAck '{ackPayload}' to {TrackerProtocol.MacToString(mac)}");
        }

        private void AckSetTrackingMode(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac, int mode) =>
            SendAckTo(sendFeature, mac, $"{TrackerProtocol.ACK_TRACKING_MODE}{mode}");

        private void AckSetRoleId(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac, int roleId) =>
            SendAckTo(sendFeature, mac, $"{TrackerProtocol.ACK_ROLE_ID}{roleId}");

        private void AckSetWifiCountry(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac) =>
            SendAckTo(sendFeature, mac, $"{TrackerProtocol.ACK_WIFI_COUNTRY}{_wifiCountry}");

        private void AckSetTrackingHost(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac, int host) =>
            SendAckTo(sendFeature, mac, $"{TrackerProtocol.ACK_TRACKING_HOST}{host}");

        private void AckSetWifiHost(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac, int host) =>
            SendAckTo(sendFeature, mac, $"{TrackerProtocol.ACK_WIFI_HOST}{host}");

        private void AckSetNewId(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac, int id) =>
            SendAckTo(sendFeature, mac, $"{TrackerProtocol.ACK_NEW_ID}{id}");

        private void AckEndMap(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac) =>
            SendAckTo(sendFeature, mac, TrackerProtocol.ACK_END_MAP);

        private void AckSetFW(Func<byte, IReadOnlyList<byte>, byte[]> sendFeature, byte[] mac, int fw) =>
            SendAckTo(sendFeature, mac, $"{TrackerProtocol.ACK_FW}{fw}");

        // スロット割り当て
        private int FindTrackerIndexByMac(byte[] mac)
        {
            for (int i = 0; i < _macs.Length; i++)
            {
                var m = _macs[i];
                if (m != null && TrackerProtocol.MacEquals(m, mac)) return i;
            }

            return -1;
        }

        private int GetOrAssignTrackerIndex(byte[] mac)
        {
            int existing = FindTrackerIndexByMac(mac);
            if (existing >= 0) return existing;
            for (int i = 0; i < _macs.Length; i++)
            {
                if (_macs[i] == null)
                {
                    _macs[i] = mac.ToArray();
                    _trackers[i].PoseLogCounter = 0;
                    _onConnect?.Invoke(i);
                    _log.Info($"Tracker slot {i} assigned MAC {TrackerProtocol.MacToString(mac)}");
                    return i;
                }
            }

            return -1;
        }
        
    }
}

