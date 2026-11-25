using NeeView.Properties;

namespace NeeView
{
    /// <summary>
    /// すべてのページ固定を解除するコマンド
    /// </summary>
    public class UnlockAllPageLocksCommand : CommandElement
    {
        public UnlockAllPageLocksCommand()
        {
            this.Group = TextResources.GetString("CommandGroup.PageSetting");
            this.ShortCutKey = new ShortcutKey("Ctrl+Shift+L");
            this.IsShowMessage = false;
        }

        public override bool CanExecute(object? sender, CommandContext e)
        {
            return NeeView.PageLock.Current.IsLocked;
        }

        [MethodArgument("UnlockAllPageLocksCommand.Remarks")]
        public override void Execute(object? sender, CommandContext e)
        {
            NeeView.PageLock.Current.UnlockAll();
        }
    }
}
