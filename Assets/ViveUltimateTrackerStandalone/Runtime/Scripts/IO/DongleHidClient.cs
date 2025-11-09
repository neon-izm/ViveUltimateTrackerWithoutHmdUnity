using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using HidSharp;
using ViveUltimateTrackerStandalone.Runtime.Scripts.Protocol;

namespace ViveUltimateTrackerStandalone.Runtime.Scripts.IO
{
    /// <summary>
    /// HID I/O 担当。Open/Close と Feature/Input レポート単位の送受信のみを担当。
    /// </summary>
    public class DongleHidClient : IDisposable
    {
        private HidStream _stream;
        private int _maxInputReportLen;
        private int _maxFeatureReportLen;
        private byte[] _featureBuffer;

        public bool IsOpen => _stream != null;
        public HidDevice Device { get; private set; }

        public bool Open(int vid, int pid, int readTimeout)
        {
            Close();
            var device = DeviceList.Local.GetHidDevices(vid, pid).FirstOrDefault();
            if (device == null) return false;
            if (!device.TryOpen(out _stream)) return false;
            Device = device;
            _stream.ReadTimeout = readTimeout;
            _maxInputReportLen = Math.Max(64, device.GetMaxInputReportLength());
            _maxFeatureReportLen = Math.Max(65, device.GetMaxFeatureReportLength());
            _featureBuffer = new byte[_maxFeatureReportLen];
            return true;
        }

        public int Read(byte[] buffer)
        {
            if (_stream == null) return 0;
            return _stream.Read(buffer, 0, buffer.Length);
        }

        public byte[] SendFeature(byte cmdId, IReadOnlyList<byte> dataBytes)
        {
            if (_stream == null) return Array.Empty<byte>();
            Array.Clear(_featureBuffer, 0, _featureBuffer.Length);
            _featureBuffer[0] = 0x00; // Report ID
            int p = 1;
            _featureBuffer[p++] = cmdId;
            _featureBuffer[p++] = (byte)((dataBytes?.Count ?? 0) + 2);
            if (dataBytes != null)
            {
                for (int i = 0; i < dataBytes.Count && p < _featureBuffer.Length; i++) _featureBuffer[p++] = dataBytes[i];
            }
            while (p < 1 + 0x40 && p < _featureBuffer.Length) _featureBuffer[p++] = 0;
            _stream.SetFeature(_featureBuffer);

            var resp = new byte[_featureBuffer.Length];
            resp[0] = 0x00;
            for (int i = 0; i < 8; i++)
            {
                _stream.GetFeature(resp);
                if (resp[1] == cmdId) return ParseHidResponse(resp, 1, resp.Length - 1);
                Thread.Sleep(5);
            }
            return Array.Empty<byte>();
        }

        public byte[] CreateInputBuffer() => new byte[_maxInputReportLen > 0 ? _maxInputReportLen : 64];

        public static byte[] ParseHidResponse(byte[] buf, int off, int len)
        {
            if (len < 5) return Array.Empty<byte>();
            int dataLen = buf[off + 1] & 0xFF;
            int responseLen = Math.Max(0, dataLen - 4);
            int payloadStart = off + 4;
            if (payloadStart + responseLen > off + len)
            {
                responseLen = Math.Max(0, (off + len) - payloadStart);
            }
            var resp = new byte[responseLen];
            Array.Copy(buf, payloadStart, resp, 0, responseLen);
            return resp;
        }

        public void Close()
        {
            if (_stream != null)
            {
                try { _stream.Close(); } catch { }
                try { _stream.Dispose(); } catch { }
                _stream = null;
                Device = null;
            }
        }

        public void Dispose() => Close();
        public string GetCrId(byte crId)
        {
            var resp = SendFeature(DongleProtocol.DCMD_GET_CR_ID, new byte[] { crId });
            if (resp == null || resp.Length == 0) return string.Empty;
            try { return System.Text.Encoding.ASCII.GetString(resp).Trim('\0'); } catch { return string.Empty; }
        }
        public string QueryRomVersion()
        {
            var resp = SendFeature(DongleProtocol.DCMD_QUERY_ROM_VERSION, Array.Empty<byte>());
            if (resp == null || resp.Length == 0) return string.Empty;
            try { return System.Text.Encoding.ASCII.GetString(resp).Trim('\0'); } catch { return string.Empty; }
        }
        public bool SendTxAckPartialMac(byte[] mac, string ascii)
        {
            if (_stream == null) return false;
            if (mac == null || mac.Length != 6 || string.IsNullOrEmpty(ascii)) return false;
            try
            {
                var asciiBytes = System.Text.Encoding.ASCII.GetBytes(ascii);
                // preamble: TX_ACK_TO_PARTIAL_MAC, mac[0..5], 0, 1
                var data = new List<byte>(9 + 1 + asciiBytes.Length);
                data.Add(DongleProtocol.TX_ACK_TO_PARTIAL_MAC);
                for (int i = 0; i < 6; i++) data.Add(mac[i]);
                data.Add(0); // reserved
                data.Add(1); // channel?
                data.Add((byte)asciiBytes.Length);
                data.AddRange(asciiBytes);
                var resp = SendFeature(DongleProtocol.DCMD_TX, data);
                return resp.Length > 0;
            }
            catch { return false; }
        }
        public bool SendTxAckFullMac(byte[] mac, string ascii, byte flag8 = 0x10, byte flag9 = 0x01)
        {
            if (_stream == null) return false;
            if (mac == null || mac.Length != 6 || string.IsNullOrEmpty(ascii)) return false;
            try
            {
                var asciiBytes = System.Text.Encoding.ASCII.GetBytes(ascii);
                if (asciiBytes.Length > 0x2C) return false; // 制約
                var data = new List<byte>(10 + asciiBytes.Length);
                data.Add(DongleProtocol.TX_ACK_TO_MAC);
                for (int i = 0; i < 6; i++) data.Add(mac[i]);
                data.Add(flag8); // 仕様: 0x10
                data.Add(flag9); // 仕様: < 0x10
                data.Add((byte)asciiBytes.Length);
                data.AddRange(asciiBytes);
                var resp = SendFeature(DongleProtocol.DCMD_TX, data);
                return resp.Length > 0;
            }
            catch { return false; }
        }
        public bool SendF4Subcommand(byte sub, byte a=1, byte b=1, byte c=1, byte d=1, byte e=1, byte f=0)
        {
            if (_stream == null) return false;
            try
            {
                var data = new List<byte>(8) { sub, a, b, c, d, e, f };
                var resp = SendFeature(DongleProtocol.DCMD_F4, data);
                return resp.Length > 0;
            }
            catch { return false; }
        }
    }
}
