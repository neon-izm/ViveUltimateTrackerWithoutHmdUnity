#if UNITY_EDITOR
using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using ViveUltimateTrackerStandalone.Runtime.Scripts;

namespace ViveUltimateTrackerStandalone.Editor
{
    [CustomEditor(typeof(UltimateTrackerReceiver))]
    public class UltimateTrackerReceiverEditor : UnityEditor.Editor
    {
        private SerializedProperty _verboseLog;
        private SerializedProperty _autoConnectOnStart;
        private SerializedProperty _fileLoggingEnabled;
        private SerializedProperty _logFilePath;
        private SerializedProperty _appendLogFile;

        private static double _nextRepaint;
        private const double LiveRefreshInterval = 0.1; // 安定したライブ更新間隔
        private bool _updateHooked;

        private void OnEnable()
        {
            _verboseLog = serializedObject.FindProperty("verboseLog");
            _autoConnectOnStart = serializedObject.FindProperty("autoConnectOnStart");
            _fileLoggingEnabled = serializedObject.FindProperty("fileLoggingEnabled");
            _logFilePath = serializedObject.FindProperty("logFilePath");
            _appendLogFile = serializedObject.FindProperty("appendLogFile");
            HookUpdate();
        }
        private void OnDisable()
        {
            UnhookUpdate();
        }
        private void HookUpdate()
        {
            if (_updateHooked) return;
            EditorApplication.update += OnEditorUpdate;
            _updateHooked = true;
        }
        private void UnhookUpdate()
        {
            if (!_updateHooked) return;
            EditorApplication.update -= OnEditorUpdate;
            _updateHooked = false;
        }
        private void OnEditorUpdate()
        {
            // Play中のみ一定間隔で Repaint。Inspector が非表示/破棄されたら自動解除。
            if (target == null) { UnhookUpdate(); return; }
            if (!Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now >= _nextRepaint)
            {
                _nextRepaint = now + LiveRefreshInterval;
                Repaint();
            }
        }

        public override void OnInspectorGUI()
        {
            var receiver = (UltimateTrackerReceiver)target;

            // ヘルプ
            EditorGUILayout.HelpBox(
                EditorLocale.T(
                    "使い方:\n" +
                    "1. このコンポーネントを空の GameObject に追加\n" +
                    "2. Play 実行で自動接続 (自動接続オン)\n" +
                    "3. [接続/切断] ボタンで手動制御も可能\n" +
                    "4. ログファイルを使う場合はチェックを付けパスを指定\n\n" +
                    "トラッカー状態は下部 Runtime セクションにライブ表示されます。",
                    "Usage:\n" +
                    "1. Add this component to an empty GameObject\n" +
                    "2. Auto connect on Play (if enabled)\n" +
                    "3. Use [Connect/Disconnect] buttons for manual control\n" +
                    "4. Enable log file to write tracking logs\n\n" +
                    "Tracker live data appears in the Runtime section below."),
                MessageType.Info);

            serializedObject.Update();
            EditorGUILayout.PropertyField(_autoConnectOnStart, new GUIContent(EditorLocale.T("起動時に自動接続", "Auto Connect On Start")));
            EditorGUILayout.PropertyField(_verboseLog, new GUIContent(EditorLocale.T("詳細ログを有効化", "Verbose Logging")));
           
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(EditorLocale.T("ログ設定", "Logging"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_fileLoggingEnabled, new GUIContent(EditorLocale.T("ログファイルを使う", "Use Log File")));
            using (new EditorGUI.DisabledScope(!_fileLoggingEnabled.boolValue))
            {
                if (_fileLoggingEnabled.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_logFilePath, new GUIContent(EditorLocale.T("ログファイルパス", "Log File Path")));
                    EditorGUILayout.PropertyField(_appendLogFile, new GUIContent(EditorLocale.T("追記モード", "Append")));
                    EditorGUI.indentLevel--;
                }
            }
            // 末尾で Apply
            serializedObject.ApplyModifiedProperties();

            // 操作セクション + ライブ表示
            DrawActions(receiver);
            DrawRuntimeSection(receiver);
        }

        private void DrawActions(UltimateTrackerReceiver receiver)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(EditorLocale.T("操作", "Actions"), EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(EditorLocale.T("接続", "Connect"), GUILayout.Height(22))) receiver.Connect();
                if (GUILayout.Button(EditorLocale.T("切断", "Disconnect"), GUILayout.Height(22))) receiver.Disconnect();
                if (GUILayout.Button(EditorLocale.T("ペアリング初期化", "Init Pairing"), GUILayout.Height(22))) receiver.InitPairingIfNeeded();
                EditorGUILayout.EndHorizontal();
                

                if (GUILayout.Button(EditorLocale.T("ドングル情報を取得 (CR_ID/ROM)", "Query Dongle Info (CR_ID/ROM)"), GUILayout.Height(22)))
                {
                    receiver.QueryDeviceInfoRaw();
                }

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawRuntimeSection(UltimateTrackerReceiver receiver)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(EditorLocale.T("Runtime ステータス", "Runtime Status"), EditorStyles.boldLabel);
            bool isConnected = false; int reports = 0; int poses = 0;
            try { isConnected = receiver.IsConnected; reports = receiver.ReportsParsed; poses = receiver.PosePacketsParsed; } catch { }
            EditorGUILayout.LabelField(EditorLocale.T("接続状態", "Connection"), isConnected ? EditorLocale.T("接続中", "Connected") : EditorLocale.T("未接続", "Disconnected"));
            EditorGUILayout.LabelField(EditorLocale.T("受信統計", "Stats"), $"Reports: {reports} / Poses: {poses}");
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(EditorLocale.T("Play モードでトラッカー情報が更新されます。", "Tracker info updates in Play mode."), MessageType.None);
                return;
            }
            var list = receiver.TrackerStates; if (list == null) return;
            int trackerCount = list.Count;
            for (int i = 0; i < trackerCount; i++)
            {
                var t = list[i];
                if (t == null) continue;
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField($"Tracker {i}", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(EditorLocale.T("アクティブ", "Active"), EditorLocale.YesNo(t.IsActive));
                    EditorGUILayout.LabelField("ID", t.TrackerIdNumber.ToString());
                    var pos = t.Position; var rotEuler = t.Rotation.eulerAngles;
                    EditorGUILayout.LabelField(EditorLocale.T("位置", "Position"), $"{pos.x:F3}, {pos.y:F3}, {pos.z:F3}");
                    EditorGUILayout.LabelField(EditorLocale.T("回転(Euler)", "Rotation(Euler)"), $"{rotEuler.x:F1}, {rotEuler.y:F1}, {rotEuler.z:F1}");
                    EditorGUILayout.LabelField("TrackingState", t.TrackingState.ToString());
                    EditorGUILayout.LabelField("Buttons", $"0x{t.Buttons:X2}");
                    EditorGUILayout.LabelField("PacketIndex", t.PacketIndex.ToString());
                    if (!string.IsNullOrEmpty(t.PoseStatus)) EditorGUILayout.LabelField("PoseStatus", t.PoseStatus);
                    if (t.MapState != 0) EditorGUILayout.LabelField("MapState", t.MapState.ToString());
                    long ageMs = (long)((DateTime.UtcNow.Ticks - t.LastUpdateUtcTicks) / TimeSpan.TicksPerMillisecond);
                    EditorGUILayout.LabelField(EditorLocale.T("最終更新(ms)", "Age(ms)"), ageMs.ToString());
                }
            }

            // 取得した CR_ID 情報表示
            if (Application.isPlaying)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField(EditorLocale.T("ドングル情報", "Dongle Info"), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("PCBID", receiver.LastPcbId);
                EditorGUILayout.LabelField("SKUID", receiver.LastSkuId);
                EditorGUILayout.LabelField("SN", receiver.LastSerial);
                EditorGUILayout.LabelField("ShipSN", receiver.LastShipSerial);
                EditorGUILayout.LabelField("CAP_FPC", receiver.LastCapFpc);
                EditorGUILayout.LabelField("ROM", receiver.LastRomVersion);
            }
        }
    }
}
#endif
