using NeeView.Properties;
using System.Windows.Data;

namespace NeeView
{
    /// <summary>
    /// ページ固定コマンド
    /// </summary>
    /// <remarks>
    /// 表示中のページを参照表示として固定する。
    /// メニューから実行したときは右クリックしたページ、それ以外はマウス位置のページを対象とする。
    /// </remarks>
    public class TogglePageLockCommand : CommandElement
    {
        public TogglePageLockCommand()
        {
            this.Group = TextResources.GetString("CommandGroup.PageSetting");
            this.ShortCutKey = new ShortcutKey("Ctrl+L");
            this.IsShowMessage = false;

            this.ParameterSource = new CommandParameterSource(new ToggleCommandParameter());
        }

        public override BindingBase CreateIsCheckedBinding()
        {
            return new Binding(nameof(NeeView.PageLock.IsLocked)) { Source = NeeView.PageLock.Current, Mode = BindingMode.OneWay };
        }

        public override bool CanExecute(object? sender, CommandContext e)
        {
            return NeeView.PageLock.Current.IsAvailable;
        }

        [MethodArgument("ToggleCommand.Execute.Remarks")]
        public override void Execute(object? sender, CommandContext e)
        {
            var toggleMode = CommandTools.GetToggleMode(e);

            // メニューから実行されたときは右クリックしたページを対象にする
            var target = e.ByMenu ? MainViewComponent.Current.MainView.GetContextMenuTargetViewContent() : null;

            NeeView.PageLock.Current.Toggle(toggleMode, target);
        }
    }
}
