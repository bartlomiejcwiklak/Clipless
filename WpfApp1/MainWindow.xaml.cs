using LibVLCSharp.Shared;
using Microsoft.WindowsAPICodePack.Shell;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace ClipManager
{
    public partial class MainWindow : FluentWindow
    {
        public ObservableCollection<GameFolder> Folders
        {
            get; set;
        } = new ObservableCollection<GameFolder>();
        public ObservableCollection<VideoClip> Clips
        {
            get; set;
        } = new ObservableCollection<VideoClip>();
        private Dictionary<string, ImageSource> _thumbnailCache =
            new Dictionary<string, ImageSource>();

        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private bool _isUpdatingFromPlayer = false;
        private string _basePath = "";
        private string _settingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClipManager_Settings.txt");

        private FileSystemWatcher _folderWatcher;

        private VideoClip _currentClip;
        private long _videoDurationMs = 1;
        private long _trimStartMs = 0;
        private long _trimEndMs = 0;

        public MainWindow()
        {
            Core.Initialize();
            InitializeComponent();
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;

            _folderWatcher = new FileSystemWatcher
            {
                Filter = "*.mp4",
                EnableRaisingEvents = false
            };
            _folderWatcher.Created += FolderWatcher_FilesChanged;
            _folderWatcher.Deleted += FolderWatcher_FilesChanged;
            _folderWatcher.Renamed += FolderWatcher_FilesChanged;

            _libVLC = new LibVLC();
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            VideoPlayer.MediaPlayer = _mediaPlayer;

            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;

            GameFolderList.ItemsSource = Folders;
            ClipList.ItemsSource = Clips;

            LoadSettings();
            ScanForGameFolders();
        }

        private string GetString(string key) => TryFindResource(key) as string
                                                ?? key;

        private void LanguageComboBox_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (LanguageComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string langCode)
            {
                var dict = new ResourceDictionary
                {
                    Source =
                      new Uri($"Dictionaries/Strings.{langCode}.xaml", UriKind.Relative)
                };
                this.Resources.MergedDictionaries.Clear();
                this.Resources.MergedDictionaries.Add(dict);
                if (GameFolderList.SelectedItem is GameFolder selectedFolder)
                    RefreshCurrentFolder(selectedFolder.FullPath);
            }
        }

        private void LoadSettings()
        {
            _basePath = File.Exists(_settingsFilePath)
                            ? File.ReadAllText(_settingsFilePath)
                            : Path.Combine(Environment.GetFolderPath(
                                               Environment.SpecialFolder.MyVideos),
                                           "NVIDIA");
            TxtFolderPath.Text = _basePath;
        }

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
            if (Directory.Exists(_basePath))
                dialog.InitialDirectory = _basePath;
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                _basePath = dialog.FileName;
                TxtFolderPath.Text = _basePath;
                File.WriteAllText(_settingsFilePath, _basePath);
                Clips.Clear();
                ScanForGameFolders();
            }
        }

        private void ScanForGameFolders()
        {
            Folders.Clear();
            if (Directory.Exists(_basePath))
            {
                foreach (string path in Directory.GetDirectories(_basePath))
                    Folders.Add(new GameFolder
                    {
                        Name = new DirectoryInfo(path).Name,
                        FullPath = path
                    });
            }
            else
                System.Windows.MessageBox.Show(
                    $"{GetString("TxtFolderNotFound")} {_basePath}",
                    GetString("TxtFolderNotFoundTitle"));
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            ScanForGameFolders();
            if (GameFolderList.SelectedItem is GameFolder selectedFolder)
                RefreshCurrentFolder(selectedFolder.FullPath);
        }

        private void GameFolderList_SelectionChanged(object sender,
                                                     SelectionChangedEventArgs e)
        {
            if (GameFolderList.SelectedItem is GameFolder folder &&
                Directory.Exists(folder.FullPath))
            {
                TxtNoFolderMessage.Visibility = Visibility.Collapsed;
                _folderWatcher.Path = folder.FullPath;
                _folderWatcher.EnableRaisingEvents = true;
                RefreshCurrentFolder(folder.FullPath);
            }
            else
            {
                TxtNoFolderMessage.Visibility = Visibility.Visible;
                Clips.Clear();
            }
        }

        private void RefreshCurrentFolder(string path)
        {
            Clips.Clear();
            List<VideoClip> temp = new List<VideoClip>();
            foreach (string file in Directory.GetFiles(path, "*.mp4"))
            {
                DateTime ct = File.GetCreationTime(file);
                temp.Add(new VideoClip
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    Thumbnail = null,
                    CreationDate = $"{GetString("TxtFileCreated")} {ct:MMM dd, yyyy}",
                    ActualCreationTime = ct
                });
            }
            foreach (var clip in SortClipList(temp))
            {
                Clips.Add(clip);
                LoadThumbnailAsync(clip);
            }
        }

        private DateTime _lastUp = DateTime.MinValue;
        private async void FolderWatcher_FilesChanged(object sender,
                                                      FileSystemEventArgs e)
        {
            if ((DateTime.Now - _lastUp).TotalMilliseconds < 1000)
                return;
            _lastUp = DateTime.Now;
            await Task.Delay(500);
            Dispatcher.BeginInvoke(new Action(() => {
                if (GameFolderList.SelectedItem is GameFolder f)
                    RefreshCurrentFolder(f.FullPath);
            }));
        }

        private void MainWindow_PreviewKeyDown(
            object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (PlayerOverlay.Visibility != Visibility.Visible ||
                _mediaPlayer == null)
                return;
            if (e.Key == System.Windows.Input.Key.Space)
            {
                e.Handled = true;
                PlayPauseButton_Click(null, null);
            }
            else if (e.Key == System.Windows.Input.Key.Up)
            {
                e.Handled = true;
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
            }
            else if (e.Key == System.Windows.Input.Key.Down)
            {
                e.Handled = true;
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
            }
            else if (e.Key == System.Windows.Input.Key.Right)
            {
                e.Handled = true;
                _mediaPlayer.Time =
                    Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 5000);
            }
            else if (e.Key == System.Windows.Input.Key.Left)
            {
                e.Handled = true;
                _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 5000);
            }
        }

        private List<VideoClip> SortClipList(List<VideoClip> list)
        {
            int idx = SortComboBox?.SelectedIndex ?? 0;
            return idx == 0
                       ? list.OrderByDescending(c => c.ActualCreationTime).ToList()
                       : (idx == 1 ? list.OrderBy(c => c.ActualCreationTime).ToList()
                                   : list.OrderBy(c => c.Name).ToList());
        }

        private async Task<bool> WaitForFileAsync(string p)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using (File.Open(p, FileMode.Open, FileAccess.Read,
                                     FileShare.ReadWrite)) return true;
                }
                catch
                {
                    await Task.Delay(200);
                }
            }
            return false;
        }

        private void SortComboBox_SelectionChanged(object sender,
                                                   SelectionChangedEventArgs e)
        {
            if (Clips == null || Clips.Count == 0)
                return;
            var sorted = SortClipList(Clips.ToList());
            Clips.Clear();
            foreach (var clip in sorted) Clips.Add(clip);
        }

        private async void ClipList_MouseDoubleClick(
            object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ClipList.SelectedItem is VideoClip clip)
            {
                if (!await WaitForFileAsync(clip.FullPath))
                {
                    System.Windows.MessageBox.Show(GetString("TxtFileBeingSaved"),
                                                   GetString("TxtPleaseWaitTitle"));
                    return;
                }
                _currentClip = clip;
                _trimStartMs = 0;
                _trimEndMs = _videoDurationMs;
                TrimText.Text = $"00:00 - {GetString("TxtEnd")}";
                _isUpdatingFromPlayer = true;
                ProgressSlider.Value = 0;
                _isUpdatingFromPlayer = false;
                PlayerOverlay.Visibility = Visibility.Visible;
                VideoPlayer.Visibility = Visibility.Visible;
                await Task.Delay(100);
                VideoPlayer.MediaPlayer = null;
                VideoPlayer.MediaPlayer = _mediaPlayer;
                PlayPauseButton.Content = "II";
                _mediaPlayer.Play(new Media(_libVLC, new Uri(clip.FullPath)));
            }
        }

        private void ClosePlayer_Click(object sender, RoutedEventArgs e)
        {
            _mediaPlayer.Stop();
            if (_mediaPlayer.Media != null)
            {
                _mediaPlayer.Media.Dispose();
                _mediaPlayer.Media = null;
            }
            PlayerOverlay.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Hidden;
        }
        private void MediaPlayer_LengthChanged(
            object sender, MediaPlayerLengthChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                _isUpdatingFromPlayer = true;
                ProgressSlider.Maximum = e.Length;
                _videoDurationMs = e.Length;
                _trimEndMs = e.Length;
                _trimStartMs = 0;
                UpdateBracketsUI();
                _isUpdatingFromPlayer = false;
            }));
        }
        private void MediaPlayer_TimeChanged(object sender,
                                             MediaPlayerTimeChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                _isUpdatingFromPlayer = true;
                ProgressSlider.Value = e.Time;
                TimeDisplay.Text =
                    $"{TimeSpan.FromMilliseconds(e.Time):mm\\:ss} / {TimeSpan.FromMilliseconds(_mediaPlayer.Length):mm\\:ss}";
                _isUpdatingFromPlayer = false;
            }));
        }
        private void ProgressSlider_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isUpdatingFromPlayer && _mediaPlayer != null)
                _mediaPlayer.Time = (long)e.NewValue;
        }
        private void VolumeSlider_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
                _mediaPlayer.Volume = (int)e.NewValue;
        }
        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null)
                return;
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                PlayPauseButton.Content = "▶️";
            }
            else
            {
                _mediaPlayer.Play();
                PlayPauseButton.Content = "II";
            }
        }

        private void ThumbStart_DragDelta(
            object sender,
            System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (_videoDurationMs <= 1)
                return;
            double nl = Math.Max(
                0, Math.Min(Canvas.GetLeft(ThumbStart) + e.HorizontalChange,
                            Canvas.GetLeft(ThumbEnd) - ThumbStart.ActualWidth));
            Canvas.SetLeft(ThumbStart, nl);
            _trimStartMs = (long)((nl / TrimCanvas.ActualWidth) * _videoDurationMs);
            if (_mediaPlayer != null)
                _mediaPlayer.Time = _trimStartMs;
            UpdateBracketsUI();
        }
        private void ThumbEnd_DragDelta(
            object sender,
            System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (_videoDurationMs <= 1)
                return;
            double nl =
                Math.Max(Canvas.GetLeft(ThumbStart) + ThumbStart.ActualWidth,
                         Math.Min(Canvas.GetLeft(ThumbEnd) + e.HorizontalChange,
                                  TrimCanvas.ActualWidth - ThumbEnd.ActualWidth));
            Canvas.SetLeft(ThumbEnd, nl);
            _trimEndMs = (long)((nl / TrimCanvas.ActualWidth) * _videoDurationMs);
            if (_mediaPlayer != null)
                _mediaPlayer.Time = _trimEndMs;
            UpdateBracketsUI();
        }

        private void UpdateBracketsUI()
        {
            if (TrimCanvas.ActualWidth == 0 || _videoDurationMs <= 1)
                return;
            TrimText.Text =
                $"{TimeSpan.FromMilliseconds(_trimStartMs):mm\\:ss} - {(_trimEndMs == _videoDurationMs ? GetString("TxtEnd") : TimeSpan.FromMilliseconds(_trimEndMs).ToString("mm\\:ss"))}";
            double sx = (_trimStartMs / (double)_videoDurationMs) *
                        TrimCanvas.ActualWidth,
                   ex = (_trimEndMs / (double)_videoDurationMs) *
                        TrimCanvas.ActualWidth;
            Canvas.SetLeft(ThumbStart, sx);
            Canvas.SetLeft(ThumbEnd, Math.Min(ex, TrimCanvas.ActualWidth -
                                                      ThumbEnd.ActualWidth));
            Canvas.SetLeft(TrimHighlight, sx + ThumbStart.ActualWidth);
            TrimHighlight.Width = Math.Max(0, ex - sx - ThumbStart.ActualWidth);
        }

        private TaskCompletionSource<System.Windows.MessageBoxResult> _saveTcs,
            _delTcs;

        private async Task<System.Windows.MessageBoxResult>
        ShowSaveConfirmOverlayAsync()
        {
            _saveTcs = new TaskCompletionSource<System.Windows.MessageBoxResult>();
            SaveConfirmOverlay.Visibility = Visibility.Visible;
            return await _saveTcs.Task;
        }
        private void BtnReplaceOverlay_Click(object sender, RoutedEventArgs e)
        {
            SaveConfirmOverlay.Visibility = Visibility.Collapsed;
            _saveTcs?.TrySetResult(System.Windows.MessageBoxResult.Yes);
        }
        private void BtnSaveNewOverlay_Click(object sender, RoutedEventArgs e)
        {
            SaveConfirmOverlay.Visibility = Visibility.Collapsed;
            _saveTcs?.TrySetResult(System.Windows.MessageBoxResult.No);
        }
        private void BtnCancelOverlay_Click(object sender, RoutedEventArgs e)
        {
            SaveConfirmOverlay.Visibility = Visibility.Collapsed;
            _saveTcs?.TrySetResult(System.Windows.MessageBoxResult.Cancel);
        }

        private void MenuCopy_Click(object sender, RoutedEventArgs e)
        {
            if (ClipList.SelectedItems.Count == 0)
                return;
            var f = new System.Collections.Specialized.StringCollection();
            foreach (VideoClip c in ClipList.SelectedItems) f.Add(c.FullPath);
            Clipboard.SetFileDropList(f);
        }
        private void MenuCut_Click(object sender, RoutedEventArgs e)
        {
            if (ClipList.SelectedItems.Count == 0)
                return;
            var f = new System.Collections.Specialized.StringCollection();
            foreach (VideoClip c in ClipList.SelectedItems) f.Add(c.FullPath);
            DataObject d = new DataObject();
            d.SetFileDropList(f);
            d.SetData("Preferred DropEffect",
                      new MemoryStream(new byte[] { 2, 0, 0, 0 }));
            Clipboard.SetDataObject(d);
        }
        private void MenuClipPaste_Click(object sender, RoutedEventArgs e)
        {
            if (GameFolderList.SelectedItem is GameFolder f &&
                Clipboard.ContainsFileDropList())
            {
                foreach (string file in Clipboard.GetFileDropList())
                    if (File.Exists(file) &&
                        file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                        try
                        {
                            File.Copy(file, Path.Combine(f.FullPath, Path.GetFileName(file)),
                                      true);
                        }
                        catch
                        {
                        }
                RefreshCurrentFolder(f.FullPath);
            }
        }

        private async Task<System.Windows.MessageBoxResult>
        ShowDeleteConfirmOverlayAsync(string tk, string mk)
        {
            // Explicitly use System.Windows.Controls.TextBlock to resolve ambiguity
            if (TxtDeleteTitle is System.Windows.Controls.TextBlock titleLabel)
                titleLabel.SetResourceReference(
                    System.Windows.Controls.TextBlock.TextProperty, tk);
            if (TxtDeleteMessage is System.Windows.Controls.TextBlock msgLabel)
                msgLabel.SetResourceReference(
                    System.Windows.Controls.TextBlock.TextProperty, mk);

            _delTcs = new TaskCompletionSource<System.Windows.MessageBoxResult>();
            VideoPlayer.Visibility = Visibility.Hidden;
            DeleteConfirmOverlay.Visibility = Visibility.Visible;
            var res = await _delTcs.Task;
            if (PlayerOverlay.Visibility == Visibility.Visible)
                VideoPlayer.Visibility = Visibility.Visible;
            return res;
        }
        private void BtnConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
            _delTcs?.TrySetResult(System.Windows.MessageBoxResult.Yes);
        }
        private void BtnCancelDelete_Click(object sender, RoutedEventArgs e)
        {
            DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
            _delTcs?.TrySetResult(System.Windows.MessageBoxResult.No);
        }

        private async void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ClipList.SelectedItems.Count == 0)
                return;
            if (await ShowDeleteConfirmOverlayAsync("TxtConfirmDeleteTitle",
                                                    "TxtConfirmDelete") ==
                System.Windows.MessageBoxResult.Yes)
                foreach (var c in ClipList.SelectedItems.Cast<VideoClip>().ToList())
                    try
                    {
                        File.Delete(c.FullPath);
                    }
                    catch
                    {
                    }
        }

        private VideoClip _clipRen;
        private void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            if (ClipList.SelectedItems.Count == 0)
                return;
            _clipRen = ClipList.SelectedItem as VideoClip;
            TxtRenameInput.Text = Path.GetFileNameWithoutExtension(_clipRen.Name);
            RenameOverlay.Visibility = Visibility.Visible;
            VideoPlayer.Visibility = Visibility.Hidden;
            TxtRenameInput.Focus();
            TxtRenameInput.SelectAll();
        }
        private void BtnSaveRename_Click(object sender, RoutedEventArgs e)
        {
            if (_clipRen != null && !string.IsNullOrWhiteSpace(TxtRenameInput.Text))
            {
                string np = Path.Combine(Path.GetDirectoryName(_clipRen.FullPath),
                                         TxtRenameInput.Text + ".mp4");
                try
                {
                    if (_clipRen.FullPath != np)
                        File.Move(_clipRen.FullPath, np);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.Message);
                }
            }
            RenameOverlay.Visibility = Visibility.Collapsed;
            if (PlayerOverlay.Visibility == Visibility.Visible)
                VideoPlayer.Visibility = Visibility.Visible;
        }
        private void BtnCancelRename_Click(object sender, RoutedEventArgs e)
        {
            RenameOverlay.Visibility = Visibility.Collapsed;
            if (PlayerOverlay.Visibility == Visibility.Visible)
                VideoPlayer.Visibility = Visibility.Visible;
        }

        private void GameFolderList_PreviewMouseRightButtonDown(
            object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item)
                item.IsSelected = true;
        }
        private enum FolderMode { None, Create, Rename }
        private FolderMode _fMode = FolderMode.None;
        private GameFolder _fRen = null;
        private void BtnCreateFolder_Click(object sender, RoutedEventArgs e)
        {
            _fMode = FolderMode.Create;
            if (TxtFolderInputTitle is System.Windows.Controls.TextBlock titleLabel)
                titleLabel.SetResourceReference(
                    System.Windows.Controls.TextBlock.TextProperty,
                    "TxtCreateFolderTitle");
            TxtFolderInput.Text = "";
            FolderInputOverlay.Visibility = Visibility.Visible;
            VideoPlayer.Visibility = Visibility.Hidden;
            TxtFolderInput.Focus();
        }
        private void MenuFolderRename_Click(object sender, RoutedEventArgs e)
        {
            if (GameFolderList.SelectedItem is GameFolder f)
            {
                _fMode = FolderMode.Rename;
                _fRen = f;
                if (TxtFolderInputTitle is System.Windows.Controls.TextBlock titleLabel)
                    titleLabel.SetResourceReference(
                        System.Windows.Controls.TextBlock.TextProperty,
                        "TxtRenameFolderTitle");
                TxtFolderInput.Text = f.Name;
                FolderInputOverlay.Visibility = Visibility.Visible;
                VideoPlayer.Visibility = Visibility.Hidden;
                TxtFolderInput.Focus();
                TxtFolderInput.SelectAll();
            }
        }
        private void BtnSaveFolderInput_Click(object sender, RoutedEventArgs e)
        {
            string inp = TxtFolderInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(inp))
                try
                {
                    if (_fMode == FolderMode.Create)
                        Directory.CreateDirectory(Path.Combine(_basePath, inp));
                    else if (_fMode == FolderMode.Rename && _fRen != null)
                        Directory.Move(_fRen.FullPath, Path.Combine(_basePath, inp));
                    ScanForGameFolders();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.Message);
                }
            FolderInputOverlay.Visibility = Visibility.Collapsed;
            if (PlayerOverlay.Visibility == Visibility.Visible)
                VideoPlayer.Visibility = Visibility.Visible;
        }
        private void BtnCancelFolderInput_Click(object sender, RoutedEventArgs e)
        {
            FolderInputOverlay.Visibility = Visibility.Collapsed;
            if (PlayerOverlay.Visibility == Visibility.Visible)
                VideoPlayer.Visibility = Visibility.Visible;
        }
        private async void MenuFolderDelete_Click(object sender,
                                                  RoutedEventArgs e)
        {
            if (GameFolderList.SelectedItem is GameFolder f &&
                await ShowDeleteConfirmOverlayAsync("TxtConfirmDeleteTitle",
                                                    "TxtConfirmDeleteFolder") ==
                    System.Windows.MessageBoxResult.Yes)
                try
                {
                    Directory.Delete(f.FullPath, true);
                    Clips.Clear();
                    ScanForGameFolders();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.Message);
                }
        }
        private void MenuFolderPaste_Click(object sender, RoutedEventArgs e)
        {
            if (GameFolderList.SelectedItem is GameFolder f &&
                Clipboard.ContainsFileDropList())
            {
                foreach (string file in Clipboard.GetFileDropList())
                    if (File.Exists(file) &&
                        file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                        try
                        {
                            File.Copy(file, Path.Combine(f.FullPath, Path.GetFileName(file)),
                                      true);
                        }
                        catch
                        {
                        }
                RefreshCurrentFolder(f.FullPath);
            }
        }

        private async void BtnCut_Click(object sender, RoutedEventArgs e)
        {
            if (_currentClip == null || _trimStartMs >= _trimEndMs)
            {
                if (_currentClip != null)
                    System.Windows.MessageBox.Show(GetString("TxtInvalidCut"),
                                                   GetString("TxtInvalidCutTitle"));
                return;
            }
            _mediaPlayer.Pause();
            VideoPlayer.Visibility = Visibility.Hidden;
            var res = await ShowSaveConfirmOverlayAsync();
            if (res == System.Windows.MessageBoxResult.Cancel)
            {
                VideoPlayer.Visibility = Visibility.Visible;
                return;
            }
            bool repl = res == System.Windows.MessageBoxResult.Yes;
            _mediaPlayer.Stop();
            if (_mediaPlayer.Media != null)
            {
                _mediaPlayer.Media.Dispose();
                _mediaPlayer.Media = null;
            }
            await Task.Delay(250);
            // Changed to use the unique BtnPerformCut name
            BtnPerformCut.IsEnabled = false;
            ExportOverlay.Visibility = Visibility.Visible;
            ExportProgressBar.Value = 0;
            ExportProgressText.Text = "0%";
            _folderWatcher.EnableRaisingEvents = false;
            try
            {
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
                string inp = _currentClip.FullPath, dir = Path.GetDirectoryName(inp),
                       name = Path.GetFileNameWithoutExtension(inp),
                       tmp = Path.Combine(dir, name + "_temp.mp4"),
                       nfp = Path.Combine(dir, $"Clipless_{name}.mp4");
                int c = 1;
                while (File.Exists(nfp))
                    nfp = Path.Combine(dir, $"Clipless_{c++}_{name}.mp4");
                var conv = FFmpeg.Conversions.New()
                               .AddStream((await FFmpeg.GetMediaInfo(inp)).Streams)
                               .SetInputTime(TimeSpan.FromMilliseconds(_trimStartMs))
                               .SetOutputTime(
                                   TimeSpan.FromMilliseconds(_trimEndMs - _trimStartMs))
                               .AddParameter("-c copy")
                               .SetOutput(tmp);
                conv.OnProgress += (s, a) => Dispatcher.Invoke(() => {
                    ExportProgressBar.Value = a.Percent;
                    ExportProgressText.Text = $"{a.Percent}%";
                });
                await conv.Start();
                if (repl)
                {
                    File.Delete(inp);
                    File.Move(tmp, inp);
                }
                else
                    File.Move(tmp, nfp);
                PlayerOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"{GetString("TxtErrorCutting")} {ex.Message}");
            }
            finally
            {
                BtnPerformCut.IsEnabled = true;
                ExportOverlay.Visibility = Visibility.Collapsed;
                if (GameFolderList.SelectedItem is GameFolder folder)
                    RefreshCurrentFolder(folder.FullPath);
                _folderWatcher.EnableRaisingEvents = true;
            }
        }

        private async void LoadThumbnailAsync(VideoClip clip)
        {
            if (_thumbnailCache.TryGetValue(clip.FullPath, out var t))
            {
                clip.Thumbnail = t;
                return;
            }
            var bt = await Task.Run(() => {
                try
                {
                    using var sf = ShellFile.FromFilePath(clip.FullPath);
                    var b = sf.Thumbnail.BitmapSource;
                    b.Freeze();
                    return b;
                }
                catch
                {
                    return null;
                }
            });
            if (bt != null)
            {
                _thumbnailCache[clip.FullPath] = bt;
                clip.Thumbnail = bt;
            }
        }

        private void TrimCanvas_SizeChanged(
            object sender, SizeChangedEventArgs e) => UpdateBracketsUI();
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Visible;
            VideoPlayer.Visibility = Visibility.Hidden;
        }
        private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            if (PlayerOverlay.Visibility == Visibility.Visible)
                VideoPlayer.Visibility = Visibility.Visible;
        }
    }

    public class GameFolder
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
    }
    public class VideoClip : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string CreationDate { get; set; }
        public DateTime ActualCreationTime { get; set; }
        private ImageSource _t;
        public ImageSource Thumbnail
        {
            get => _t;
            set
            {
                _t = value;
                OnPropertyChanged();
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}