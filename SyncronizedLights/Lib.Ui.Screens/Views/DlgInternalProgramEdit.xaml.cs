using System.Windows;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// A1 内蔵プログラムプリセットの名前・フレーム番号を編集するダイアログ
    /// </summary>
    public partial class DlgInternalProgramEdit : Window
    {
        public DlgInternalProgramEdit()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                TxtName.Text = ProgramName;
                TxtFrameNo.Text = FrameNo.ToString();
                ChkEnabled.IsChecked = IsEnabled;
                TxtName.Focus();
                TxtName.SelectAll();
            };
        }

        /// <summary>表示名（双方向）</summary>
        public string ProgramName { get; set; } = "";

        /// <summary>フレーム番号（双方向）</summary>
        public uint FrameNo { get; set; }

        /// <summary>有効フラグ（双方向）</summary>
        public new bool IsEnabled { get; set; } = true;

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ProgramName = TxtName.Text.Trim();
            if (string.IsNullOrEmpty(ProgramName))
            {
                MessageBox.Show("名前を入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!uint.TryParse(TxtFrameNo.Text.Trim(), out var frame))
            {
                MessageBox.Show("フレーム番号は 0 以上の整数で入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            FrameNo = frame;
            IsEnabled = ChkEnabled.IsChecked == true;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
