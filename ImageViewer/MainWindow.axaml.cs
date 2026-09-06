using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DmitryBrant.ImageFormats;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ImageViewer
{
    public partial class MainWindow : Window
    {
        private const string AppName = "ImageViewer";

        private Bitmap bitmap;

        public MainWindow()
        {
            InitializeComponent();
            Title = AppName;

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // A MenuItem's InputGesture only draws the shortcut text next to the item;
            // it doesn't bind the key. Bind the ones the menu advertises.
            if (e.KeyModifiers == KeyModifiers.Control)
            {
                if (e.Key == Key.O) { e.Handled = true; _ = OpenViaPickerAsync(); }
                else if (e.Key == Key.S) { e.Handled = true; _ = SaveAsAsync(); }
            }
            base.OnKeyDown(e);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            var fileName = e.DataTransfer.TryGetFile()?.TryGetLocalPath();
            if (fileName == null) return;
            OpenFile(fileName);
        }

        private void OnOpenClick(object sender, RoutedEventArgs e) => _ = OpenViaPickerAsync();

        private async Task OpenViaPickerAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Properties.Resources.openDlgTitle,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(Properties.Resources.filterAllFiles) { Patterns = new[] { "*" } }
                }
            });

            var fileName = files.Count == 0 ? null : files[0].TryGetLocalPath();
            if (fileName == null) return;
            OpenFile(fileName);
        }

        private void OpenFile(string fileName)
        {
            try
            {
                var bmp = ImageData.Load(fileName).ToAvaloniaBitmap();
                if (bmp == null)
                {
                    //try loading the file natively...
                    try { bmp = new Bitmap(fileName); }
                    catch (Exception e) { Debug.WriteLine(e.Message); }
                }

                bitmap = bmp ?? throw new ImageDecodeException(Properties.Resources.errorLoadFailed);
                pictureBox.Source = bitmap;
                Title = fileName + " - " + AppName;
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
        }

        private void OnSaveAsClick(object sender, RoutedEventArgs e) => _ = SaveAsAsync();

        private async Task SaveAsAsync()
        {
            try
            {
                if (bitmap == null)
                {
                    return;
                }
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Properties.Resources.saveDlgTitle,
                    DefaultExtension = "png",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType(Properties.Resources.filterPngFiles) { Patterns = new[] { "*.png" } },
                        new FilePickerFileType(Properties.Resources.filterJpgFiles) { Patterns = new[] { "*.jpg" } }
                    }
                });
                if (file == null) return;

                BitmapEncoderOptions options = Path.GetExtension(file.Name).ToLower() == ".jpg"
                    ? JpegBitmapEncoderOptions.Default : PngBitmapEncoderOptions.Default;

                await using var stream = await file.OpenWriteAsync();
                bitmap.Save(stream, options);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ShowError(string message)
        {
            _ = MessageBox.Show(this, message, AppName);
        }
    }
}
