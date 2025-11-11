using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ViveUltimateTrackerStandalone.Runtime.Scripts.Infrastructure
{
    /// <summary>
    /// MACアドレスとトラッカーIDの対応表を管理し、JSONファイルで永続化する。
    /// </summary>
    public class TrackerIdMapper
    {
        private Dictionary<string, int> _macToId = new Dictionary<string, int>();
        private readonly string _filePath;
        private readonly TrackerLogger _logger;
        private const string FallbackMacKey = "23:31:33:B9:1B";
        private const int FallbackId = 1;

        public TrackerIdMapper(string filePath, TrackerLogger logger = null)
        {
            _filePath = filePath;
            _logger = logger;
        }

        /// <summary>
        /// JSONファイルから対応表を読み込む。ファイルが存在しない場合は空で初期化。
        /// </summary>
        public void Load()
        {
            if (!File.Exists(_filePath))
            {
                _logger?.Info($"TrackerIdMapper: File not found, starting with empty map. Path={_filePath}");
                _macToId = new Dictionary<string, int>();
                return;
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                var wrapper = JsonUtility.FromJson<MappingWrapper>(json);
                _macToId = new Dictionary<string, int>();
                
                if (wrapper?.mappings != null)
                {
                    foreach (var entry in wrapper.mappings)
                    {
                        _macToId[entry.mac] = entry.id;
                    }
                }

                _logger?.Info($"TrackerIdMapper: Loaded {_macToId.Count} entries from {_filePath}");
            }
            catch (Exception ex)
            {
                _logger?.Error($"TrackerIdMapper: Failed to load from {_filePath}: {ex.Message}");
                _macToId = new Dictionary<string, int>();
            }
        }

        /// <summary>
        /// 対応表をJSONファイルに保存する。
        /// </summary>
        public void Save()
        {
            try
            {
                var wrapper = new MappingWrapper();
                wrapper.mappings = new List<MappingEntry>();
                
                foreach (var kv in _macToId)
                {
                    wrapper.mappings.Add(new MappingEntry 
                    { 
                        mac = kv.Key, 
                        id = kv.Value
                    });
                }

                string json = JsonUtility.ToJson(wrapper, true);
                
                // ディレクトリが存在しない場合は作成
                string directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                File.WriteAllText(_filePath, json);
                _logger?.Info($"TrackerIdMapper: Saved {_macToId.Count} entries to {_filePath}");
            }
            catch (Exception ex)
            {
                _logger?.Error($"TrackerIdMapper: Failed to save to {_filePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// 指定されたMACアドレスに対応するトラッカーIDを取得または新規割り当て。
        /// </summary>
        public int GetOrAssignId(string macAddress, bool autoSave = true)
        {
            if (_macToId.TryGetValue(macAddress, out int existingId))
            {
                return existingId;
            }

            int newId = AllocateId(macAddress);
            if (newId < 0)
            {
                _logger?.Warn($"No free ID/slot available)");
                return -1;
            }

            _macToId[macAddress] = newId;
            _logger?.Info($"TrackerIdMapper: Assigned ID/Slot {newId} to MAC {macAddress}");

            if (autoSave)
            {
                Save();
            }

            return newId;
        }

        /// <summary>
        /// 指定されたMACアドレスのIDを取得（存在しない場合は-1）。
        /// </summary>
        public int GetId(string macAddress)
        {
            return _macToId.TryGetValue(macAddress, out int id) ? id : -1;
        }

        /// <summary>
        /// 空きIDを割り当てる。
        /// </summary>
        private int AllocateId(string macAddress)
        {
            if (string.Equals(macAddress, FallbackMacKey, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseId(FallbackId);
                return FallbackId;
            }

            var usedIds = _macToId.Values.ToHashSet();
            for (int i = 0; i < 100; i++)
            {
                if (!usedIds.Contains(i)) return i;
            }

            return -1;
        }

        /// <summary>
        /// 指定IDを使用しているマッピングを解放。
        /// </summary>
        private void ReleaseId(int id)
        {
            var toRemove = _macToId.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList();
            foreach (var mac in toRemove)
            {
                _macToId.Remove(mac);
                _logger?.Info($"TrackerIdMapper: Released ID/Slot {id} for MAC={mac}");
            }
        }

        /// <summary>
        /// 指定されたMACアドレスが登録済みか確認。
        /// </summary>
        public bool HasMapping(string macAddress)
        {
            return _macToId.ContainsKey(macAddress);
        }

        /// <summary>
        /// 現在の対応表のコピーを取得。
        /// </summary>
        public IReadOnlyDictionary<string, int> GetMappings()
        {
            return new Dictionary<string, int>(_macToId);
        }

        /// <summary>
        /// 対応表をクリア。
        /// </summary>
        public void Clear()
        {
            _macToId.Clear();
            _logger?.Info("TrackerIdMapper: Cleared all mappings");
        }

        /// <summary>
        /// 対応表の件数を取得。
        /// </summary>
        public int Count => _macToId.Count;

        // JSON シリアライズ用のラッパークラス
        [Serializable]
        private class MappingWrapper
        {
            public List<MappingEntry> mappings;
        }

        [Serializable]
        private class MappingEntry
        {
            public string mac;
            public int id;
        }
    }
}

