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
    /// Mode画面View
    /// 概要：点滅、フェード、ブレスなどの演出モード操作画面を表示するView。
    /// </summary>
    public partial class ModeView : UserControl
    {
        /// <summary>
        /// ModeViewを生成する
        /// 概要：Mode画面の表示部品を初期化する。
        /// </summary>
        public ModeView()
        {
            InitializeComponent();
        }
    }
}
