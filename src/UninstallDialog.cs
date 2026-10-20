// アンインストールの確認画面（uninstall.ps1 から AdGuardHomeTray.exe --uninstall-dialog で呼ばれる）。
// 終了コードで結果を返す: 0 = すべて削除する / 10 = 設定とデータを残す / 1 = キャンセル
using System;
using System.Windows.Forms;

namespace AdGuardHomeTray
{
    class UninstallDialog : ModernDialog
    {
        public const int ExitRemoveAll = 0, ExitKeepSettings = 10, ExitCancel = 1;
        readonly ModernCheckBox keep;

        public UninstallDialog(bool keepDefault) : base("AdGuard Home のアンインストール", "AdGuard Home をアンインストールしますか？")
        {
            AddText("AdGuard Home とトレイアプリをこの PC から削除します。", true);
            AddText("この PC やほかの端末の DNS サーバーに AdGuard Home を指定している場合は、先に元の設定へ戻してください。" +
                    "戻さずに削除すると、インターネットにつながりにくくなります。");
            AddText("既定では次のデータも一緒に削除します。");
            AddBullets("AdGuard Home の設定（フィルター・ブロックリスト・ログイン情報など）とクエリログ",
                       "トレイアプリに保存したログイン情報と、管理画面ウィンドウのデータ");
            keep = AddCheck("設定とデータを残す（再インストールしたときに引き継ぐ）", keepDefault);

            var cancel = AddButton("キャンセル", ButtonKind.Normal);
            var ok = AddButton("アンインストール", ButtonKind.Primary);
            cancel.DialogResult = DialogResult.Cancel;
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;
            CancelButton = cancel;
            TopMost = true;
            Shown += delegate { Activate(); cancel.Focus(); };   // 誤って Enter で消さないよう、最初はキャンセルにフォーカス
        }

        public bool KeepSettings { get { return keep.Checked; } }

        public static int Run(bool keepDefault)
        {
            using (var d = new UninstallDialog(keepDefault))
            {
                if (d.ShowDialog() != DialogResult.OK) return ExitCancel;
                return d.KeepSettings ? ExitKeepSettings : ExitRemoveAll;
            }
        }
    }
}
