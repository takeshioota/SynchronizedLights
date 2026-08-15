using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// Setting画面View
    /// 概要：通信設定やポート設定などの環境設定画面を表示するView。
    /// </summary>
    public partial class SettingView : UserControl
    {
        /// <summary>
        /// SettingViewを生成する
        /// 概要：Setting画面の表示部品を初期化する。
        /// </summary>
        public SettingView()
        {
            InitializeComponent();
        }
    }
}
