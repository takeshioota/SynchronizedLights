using System;
using System.Windows;
using System.Windows.Controls;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// コマンドパネル UserControl
    /// 概要：色・Custom・動作・操作・テンプレのプリセットボタン群。
    ///       IntegratedWindow のエディタ下部、または CommandWindow（2モニタ時）に配置される。
    ///       DataContext には SequenceEditorViewModel を設定する。
    ///       煽りボタンは v3.9 でエディタ内（本番中使用エリア）に移動済み。
    /// </summary>
    public partial class CommandPanelView : UserControl
    {
        public CommandPanelView()
        {
            InitializeComponent();
        }

        private SequenceEditorViewModel? Vm => DataContext as SequenceEditorViewModel;

        #region コンテキストメニュー

        private void CustomColorEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not CustomColorPresetItem item) return;

            var vm = Vm;
            if (vm != null && vm.EditCustomColorCommand.CanExecute(item))
                vm.EditCustomColorCommand.Execute(item);
        }

        private void InternalProgramEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not InternalProgramPresetItem item) return;

            var vm = Vm;
            if (vm != null && vm.EditInternalProgramCommand.CanExecute(item))
                vm.EditInternalProgramCommand.Execute(item);
        }

        // FAB#4 修正: 旧実装は ContextMenu 内の MenuItem に Command を RelativeSource=UserControl で
        // バインドしていたが、ContextMenu はビジュアルツリー外のため解決できず無反応だった。
        // 他のメニュー同様、コードビハインドで PlacementTarget の DataContext から実体を取り出して実行する。
        private void RainbowColorDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not RgbColorItem item) return;

            var vm = Vm;
            if (vm != null && vm.RemoveRainbowColorCommand.CanExecute(item))
                vm.RemoveRainbowColorCommand.Execute(item);
        }

        private void RainbowColorEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not RgbColorItem item) return;

            var vm = Vm;
            if (vm != null && vm.EditRainbowColorCommand.CanExecute(item))
                vm.EditRainbowColorCommand.Execute(item);
        }

        #endregion コンテキストメニュー
    }
}
