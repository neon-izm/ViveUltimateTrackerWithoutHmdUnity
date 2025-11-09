#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using ViveUltimateTrackerStandalone.Editor;

namespace ViveUltimateTrackerStandalone.Editor
{
    public class UltimateTrackerHelpWindow : EditorWindow
    {
        [MenuItem("Vive Ultimate Tracker/使い方のメモ", priority = 10)]
        public static void Open()
        {
            var w = GetWindow<UltimateTrackerHelpWindow>(true, "Vive Ultimate Tracker - 使い方", true);
            w.minSize = new Vector2(500, 380);
            w.Show();
        }

        private Vector2 _scroll;
        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            GUILayout.Label(EditorLocale.T("Vive Ultimate Tracker - 使い方", "Vive Ultimate Tracker - Help"), EditorStyles.boldLabel);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(EditorLocale.T(
                "このプロジェクトは HTC Vive Ultimate Tracker を HMD なしで使うためのサンプルです。\n\n" +
                "1. USB ドングルを PC に接続します\n" +
                "2. シーンに UltimateTrackerReceiver を配置します\n" +
                "3. Play で接続・ペアリングメニューからトラッカーを登録します\n" +
                "4. 最新の位置・回転は Receiver のインスペクタ下部に表示されます\n\n" +
                "トラブルシュート:\n" +
                "- デバイスが見つからない: 管理者権限や USB ポートを変更\n" +
                "- 受信が途切れる: 他の 2.4GHz 干渉やドングルの距離を確認\n" +
                "- ログ出力を有効にして log.txt を添付いただくと調査が容易です",
                "This project demonstrates using HTC Vive Ultimate Tracker without an HMD.\n\n" +
                "1. Plug the USB dongle into the PC\n" +
                "2. Add UltimateTrackerReceiver to the scene\n" +
                "3. Press Play, pair trackers via pairing menu\n" +
                "4. Live pose appears in the receiver inspector\n\n" +
                "Troubleshooting:\n" +
                "- Device not found: try admin privileges or a different USB port\n" +
                "- Drops: check 2.4GHz interference / distance to dongle\n" +
                "- Enable file logging and attach log.txt for investigation"),
                MessageType.Info);
            EditorGUILayout.Space();
            if (GUILayout.Button(EditorLocale.T("GitHub リポジトリを開く", "Open GitHub Repository"))) { Application.OpenURL("https://github.com/neon-izm/ViveUltimateTrackerWithoutHmdUnity"); }
            EditorGUILayout.EndScrollView();
        }
    }
}
#endif
