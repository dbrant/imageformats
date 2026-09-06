using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace ImageViewer
{
    /// <summary>
    /// A minimal modal message dialog. Avalonia has no built-in equivalent of
    /// System.Windows.Forms.MessageBox, and this app only ever needs to report an error.
    /// </summary>
    internal static class MessageBox
    {
        public static Task Show(Window owner, string message, string title)
        {
            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                IsCancel = true,
                MinWidth = 80,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var dialog = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    Spacing = 24,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 420
                        },
                        okButton
                    }
                }
            };

            okButton.Click += (_, _) => dialog.Close();
            return dialog.ShowDialog(owner);
        }
    }
}
