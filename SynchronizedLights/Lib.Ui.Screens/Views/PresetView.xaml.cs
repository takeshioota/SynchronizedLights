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
    /// Preset画面View
    /// 概要：色変更、点灯、消灯などのPreset操作画面を表示するView。
    /// ViewModelとバインディングし、ユーザー操作を受け付ける。
    /// </summary>
    public partial class PresetView : UserControl
    {
        /// <summary>
        /// Preset画面View
        /// 概要：色変更、点灯、消灯などのPreset操作画面を表示するView。
        /// ViewModelとバインディングし、ユーザー操作を受け付ける。
        /// </summary>
        public PresetView()
        {
            InitializeComponent();
        }
    }
}
