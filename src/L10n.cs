// 表示の言語: Windows の表示言語が日本語なら日本語、それ以外は英語。
// 文言は L.T("日本語", "English") のように、使う場所に両方を並べて書く（リソースファイルは使わない）。
using System.Globalization;

namespace AdGuardHomeTray
{
    static class L
    {
        // 環境変数 ADGUARD_TRAY_LANG（ja / en）で指定もできる（確認やスクリーンショット用）
        public static readonly bool Ja = DetectJapanese();

        static bool DetectJapanese()
        {
            string forced = System.Environment.GetEnvironmentVariable("ADGUARD_TRAY_LANG");
            if (forced == "ja") return true;
            if (forced == "en") return false;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
        }

        public static string T(string ja, string en) { return Ja ? ja : en; }

        // 画面の文字（日本語は Yu Gothic UI、英語は Windows 標準の Segoe UI）
        public static string UiFont { get { return Ja ? "Yu Gothic UI" : "Segoe UI"; } }
        public static string UiFontSemibold { get { return Ja ? "Yu Gothic UI Semibold" : "Segoe UI Semibold"; } }
    }
}
