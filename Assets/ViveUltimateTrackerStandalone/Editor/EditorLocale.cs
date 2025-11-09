#if UNITY_EDITOR
using System.Globalization;
using UnityEngine;

namespace ViveUltimateTrackerStandalone.Editor
{
    internal static class EditorLocale
    {
        public static bool IsJapanese
        {
            get
            {
                // 優先: Unity のシステム言語。フォールバック: OS の UI カルチャ
                if (Application.systemLanguage == SystemLanguage.Japanese) return true;
                try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja"; }
                catch { return false; }
            }
        }

        public static string T(string ja, string en) => IsJapanese ? ja : en;
        public static string YesNo(bool v) => IsJapanese ? (v ? "はい" : "いいえ") : (v ? "Yes" : "No");
    }
}
#endif

