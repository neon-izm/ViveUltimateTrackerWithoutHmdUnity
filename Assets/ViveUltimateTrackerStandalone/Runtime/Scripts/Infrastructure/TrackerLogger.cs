using System;
using System.IO;
using UnityEngine;

namespace ViveUltimateTrackerStandalone.Runtime.Scripts.Infrastructure
{
    /// <summary>
    /// ファイル/コンソール兼用ロガー。Unity 依存はここに閉じ込める。
    /// </summary>
    public class TrackerLogger : IDisposable
    {
        public bool Verbose { get; set; }
        public bool FileLoggingEnabled => _writer != null;
        private readonly object _lock = new object();
        private StreamWriter _writer;

        public TrackerLogger(bool verbose, bool enableFile, string path, bool append)
        {
            Verbose = verbose;
            if (enableFile)
            {
                try
                {
                    if (!Path.IsPathRooted(path))
                    {
                        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                        path = Path.GetFullPath(Path.Combine(projectRoot, path));
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    _writer = new StreamWriter(path, append, System.Text.Encoding.UTF8) { AutoFlush = true };
                    WriteLine("LOGGER", $"File logging started: {path}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Tracker] File logger init failed: {ex.Message}");
                    _writer = null;
                }
            }
        }

        public void Info(string msg)
        {
            if (Verbose) Debug.Log("[Tracker] " + msg);
            WriteLine("INFO", msg);
        }
        public void Warn(string msg)
        {
            Debug.LogWarning("[Tracker] " + msg);
            WriteLine("WARN", msg);
        }
        public void Error(string msg)
        {
            Debug.LogError("[Tracker] " + msg);
            WriteLine("ERROR", msg);
        }
        private void WriteLine(string level, string message)
        {
            if (_writer == null) return;
            lock (_lock)
            {
                string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                _writer.WriteLine($"{ts}\t{level}\t{message}");
            }
        }

        public void Dispose()
        {
            if (_writer != null)
            {
                try { WriteLine("LOGGER", "File logging stopped"); } catch { }
                try { _writer.Flush(); } catch { }
                try { _writer.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}
