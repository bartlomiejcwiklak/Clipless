using LibVLCSharp.Shared;
using Microsoft.WindowsAPICodePack.Shell;
using System;
using System.Collections.Concurrent;
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
using NAudio.CoreAudioApi;

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
        public ObservableCollection<ClipTag> TagsList
        {
            get; set;
        } = new ObservableCollection<ClipTag>();

        private Dictionary<string, HashSet<string>> _clipTagMap = new Dictionary<string, HashSet<string>>();
        private string _tagsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipManager_Tags.txt");
        private string _clipTagsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipManager_ClipTags.txt");

        private Dictionary<string, ImageSource> _thumbnailCache =
            new Dictionary<string, ImageSource>();

        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private bool _isUpdatingFromPlayer = false;
        private string _basePath = "";
        private string _outPath = "";
        private string _language = "en";
        private int _targetSizeMb = 10;
        private string _settingsIniFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClipManager_Settings.ini");
        private string _renderEngine = "CPU";
        private bool _autoStartEnabled = false;
        private bool _autoDeleteEnabled = false;
        private int _autoDeleteWeeks = 4;
        private int _volume = 100;

        private System.Windows.Threading.DispatcherTimer _hoverTimer;
        private int _hoverFrameIndex = 1;
        private BlockingCollection<Action> _staJobs = new BlockingCollection<Action>();

        private FileSystemWatcher _folderWatcher;

        private VideoClip _currentClip;
        private long _videoDurationMs = 1;
        private long _trimStartMs = 0;
        private long _trimEndMs = 0;

        private HashSet<string> _favorites = new HashSet<string>();
        private string _favoritesFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClipManager_Favorites.txt");

        private bool _isScrubbing = false;
        private bool _isLoaded = false;
        private bool _wasPlayingBeforeScrub = false;
        private DateTime _lastScrubTime = DateTime.MinValue;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private LowLevelKeyboardProc _hookProc;
        private IntPtr _hookID = IntPtr.Zero;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        private const int CLIP_HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CTRL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;

        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private System.Diagnostics.Process _bgRecorder;
        private bool _isRecording = false;
        private int _bufferMinutes = 5;
        private bool _recordEnabled = true;
        private string _captureFramerate = "60";
        private string _captureResolution = "Native";
        private string _audioOutputId = "";
        private string _audioInputId = "";
        private string _cameraId = "";
        private bool _cameraOverlayEnabled = false;
        private AudioPipeRecorder _audioOutputRecorder;
        private AudioPipeRecorder _audioInputRecorder;
        private bool _actualClose = false;
        private int _recorderSessionId = 0;
        private int _sliderDebounceCounter = 0;

        private bool _normalizeOnClip = false;
        private int _captureMonitorIndex = 0;
        private uint _hotkeyModifiers = MOD_CTRL | MOD_SHIFT;
        private int _hotkeyKey = 0x53; // 'S' by default

        public MainWindow()
        {
            Core.Initialize();
            InitializeComponent();
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;

            ProgressSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent, new System.Windows.Controls.Primitives.DragStartedEventHandler(ProgressSlider_DragStarted));
            ProgressSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, new System.Windows.Controls.Primitives.DragCompletedEventHandler(ProgressSlider_DragCompleted));

            _hoverTimer = new System.Windows.Threading.DispatcherTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(250);
            _hoverTimer.Tick += HoverTimer_Tick;

            var staThread = new System.Threading.Thread(() => {
                foreach (var job in _staJobs.GetConsumingEnumerable()) job();
            });
            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            _folderWatcher = new FileSystemWatcher
            {
                Filter = "*.mp4",
                EnableRaisingEvents = false
            };
            _folderWatcher.Created += FolderWatcher_FilesChanged;
            _folderWatcher.Deleted += FolderWatcher_FilesChanged;
            _folderWatcher.Renamed += FolderWatcher_FilesChanged;

            _libVLC = new LibVLC("--play-and-pause");
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            VideoPlayer.MediaPlayer = _mediaPlayer;

            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
            _mediaPlayer.EndReached += MediaPlayer_EndReached;

            GameFolderList.ItemsSource = Folders;
            TagsListBox.ItemsSource = TagsList;
            ClipList.ItemsSource = Clips;

            LoadSettings();
            ScanForGameFolders();

            InitTrayIcon();
            StartBackgroundRecorder();

            if (Environment.GetCommandLineArgs().Contains("-autostart"))
            {
                this.WindowState = WindowState.Minimized;
                this.ShowInTaskbar = false;
                this.Loaded += (s, e) => { this.Visibility = Visibility.Hidden; };
            }
        }

        private void UpdateTrayMenuUI()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Text = GetString("TxtTrayTitle");
                if (_notifyIcon.ContextMenuStrip != null && _notifyIcon.ContextMenuStrip.Items.Count >= 4)
                {
                    string hotkey = TxtSaveHotkey?.Text ?? "Ctrl+Shift+S";
                    _notifyIcon.ContextMenuStrip.Items[0].Text = $"{GetString("TxtTraySaveClip")} ({hotkey})";
                    _notifyIcon.ContextMenuStrip.Items[2].Text = GetString("TxtTrayOpen");
                    _notifyIcon.ContextMenuStrip.Items[3].Text = GetString("TxtTrayQuit");
                }
            }
        }

        private void InitTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "Clipless Background Recorder";
            _notifyIcon.DoubleClick += (s, e) => {
                this.ShowInTaskbar = true;
                this.WindowState = WindowState.Normal;
                this.Visibility = Visibility.Visible;
                this.Activate();
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Save Clip", null, (s, e) => {
                SaveBufferedClip();
            });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Open Clipless", null, (s, e) => {
                this.ShowInTaskbar = true;
                this.WindowState = WindowState.Normal;
                this.Visibility = Visibility.Visible;
                this.Activate();
            });
            menu.Items.Add("Quit", null, (s, e) => {
                _actualClose = true;
                this.Close();
            });
            _notifyIcon.ContextMenuStrip = menu;
            UpdateTrayMenuUI();
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                if (vkCode == _hotkeyKey)
                {
                    bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                    bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                    bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;

                    uint currentMods = 0;
                    if (ctrl) currentMods |= MOD_CTRL;
                    if (shift) currentMods |= MOD_SHIFT;
                    if (alt) currentMods |= MOD_ALT;

                    if (currentMods == _hotkeyModifiers)
                    {
                        string activeGame = GetForegroundProcessName();

                        Dispatcher.BeginInvoke(new Action(() => SaveBufferedClip(activeGame)));
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hookProc = HookCallback;
            _hookID = SetHook(_hookProc);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_notifyIcon != null && !_actualClose)
            {
                e.Cancel = true;
                this.Visibility = Visibility.Hidden;
                _notifyIcon.ShowBalloonTip(2000, "Clipless", string.Format(GetString("TxtTrayRunning"), TxtSaveHotkey?.Text ?? "Ctrl+Shift+S"), System.Windows.Forms.ToolTipIcon.Info);
            }
            else
            {
                base.OnClosing(e);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            UnhookWindowsHookEx(_hookID);
            StopBackgroundRecorder();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            base.OnClosed(e);
        }

        private void SetStatus(string text)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                if (TxtStatusBar != null)
                    TxtStatusBar.Text = text;
            }));
        }

        private void StartBackgroundRecorder()
        {
            if (!_recordEnabled) return;
            StopBackgroundRecorder();

            int currentSessionId = ++_recorderSessionId;

            string tempDir = Path.Combine(Path.GetTempPath(), "CliplessBuffer");
            Directory.CreateDirectory(tempDir);

            foreach (string f in Directory.GetFiles(tempDir))
            {
                try { File.Delete(f); } catch { }
            }

            string playlistPath = Path.Combine(tempDir, "buffer.m3u8");

            Task.Run(async () => {
                if (currentSessionId != _recorderSessionId || !_recordEnabled) return;
                try { await DownloadFFmpegIfNeeded(); } catch { } 
                if (currentSessionId != _recorderSessionId || !_recordEnabled) return;
                string ffmpegPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "ffmpeg", "ffmpeg.exe");

                if (!File.Exists(ffmpegPath)) return;

                int segmentTime = 5;
                int maxSegments = (_bufferMinutes * 60) / segmentTime;

                string encoder = _renderEngine == "GPU" ? "h264_nvenc -preset fast -cq 28" : "libx264 -preset superfast -crf 28";

                var screens = System.Windows.Forms.Screen.AllScreens;
                int monIndex = _captureMonitorIndex >= 0 && _captureMonitorIndex < screens.Length ? _captureMonitorIndex : 0;
                var screen = screens[monIndex];
                int w = screen.Bounds.Width;
                int h = screen.Bounds.Height;
                int x = screen.Bounds.X;
                int y = screen.Bounds.Y;

                // Encoders require even dimensions
                w = w % 2 != 0 ? w - 1 : w;
                h = h % 2 != 0 ? h - 1 : h;

                string fps = _captureFramerate == "30" ? "30" : "60";

                string camInput = "";
                int currentInputIdx = 1;
                int camIndexIdx = -1;

                if (_cameraOverlayEnabled && !string.IsNullOrEmpty(_cameraId)) {
                    camInput = $"-use_wallclock_as_timestamps 1 -thread_queue_size 2048 -f dshow -rtbufsize 2048M -i video=\"{_cameraId}\" ";
                    camIndexIdx = currentInputIdx++;
                }

                var enumerator = new MMDeviceEnumerator();
                string audioInputs = "";
                int audioOutIdx = -1;
                int audioInIdx = -1;

                try {
                    if (!string.IsNullOrEmpty(_audioOutputId)) {
                        var dev = _audioOutputId == "Default" 
                                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                                : enumerator.GetDevice(_audioOutputId);
                        if (dev != null) {
                            var outRec = new AudioPipeRecorder(dev, true);
                            _audioOutputRecorder = outRec;
                            _ = outRec.StartAsync(); 
                            audioInputs += $"-use_wallclock_as_timestamps 1 -thread_queue_size 2048 -f {outRec.FFmpegFormat} -ar {outRec.SampleRate} -ac {outRec.Channels} -i \\\\.\\pipe\\{outRec.PipeName} ";
                            audioOutIdx = currentInputIdx++;
                        }
                    }
                } catch { }

                try {
                    if (!string.IsNullOrEmpty(_audioInputId)) {
                        var dev = _audioInputId == "Default" 
                                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                                : enumerator.GetDevice(_audioInputId);
                        if (dev != null) {
                            var inRec = new AudioPipeRecorder(dev, false);
                            _audioInputRecorder = inRec;
                            _ = inRec.StartAsync();
                            audioInputs += $"-use_wallclock_as_timestamps 1 -thread_queue_size 2048 -f {inRec.FFmpegFormat} -ar {inRec.SampleRate} -ac {inRec.Channels} -i \\\\.\\pipe\\{inRec.PipeName} ";
                            audioInIdx = currentInputIdx++;
                        }
                    }
                } catch { }

                List<string> filters = new List<string>();

                string preScale = "hwdownload,format=bgra";
                if (_captureResolution == "1080p") preScale += ",scale=-2:1080";
                else if (_captureResolution == "720p") preScale += ",scale=-2:720";

                string videoMap = "0:v";
                if (camIndexIdx != -1) {
                    filters.Add($"[0:v]{preScale}[main];[{camIndexIdx}:v]scale=320:-2[cam];[main][cam]overlay=20:H-h-20[vout]");
                    videoMap = "[vout]";
                } else {
                    filters.Add($"[0:v]{preScale}[vout]");
                    videoMap = "[vout]";
                }

                string audioMap = "";
                if (audioOutIdx != -1 && audioInIdx != -1) {
                    filters.Add($"[{audioOutIdx}:a]aresample=async=1[a1];[{audioInIdx}:a]aresample=async=1[a2];[a1][a2]amix=inputs=2:duration=longest[aout]");
                    audioMap = " -map \"[aout]\" ";
                } else if (audioOutIdx != -1) {
                    filters.Add($"[{audioOutIdx}:a]aresample=async=1[aout]");
                    audioMap = " -map \"[aout]\" ";
                } else if (audioInIdx != -1) {
                    filters.Add($"[{audioInIdx}:a]aresample=async=1[aout]");
                    audioMap = " -map \"[aout]\" ";
                }

                string filterArg = filters.Count > 0 ? $"-filter_complex \"{string.Join(";", filters)}\" " : "";
                string audioCodec = (audioOutIdx != -1 || audioInIdx != -1) ? "-c:a aac -b:a 192k " : "";

                _bgRecorder = new System.Diagnostics.Process();
                _bgRecorder.StartInfo.FileName = ffmpegPath;
                // Specify offset and video_size to capture only the target monitor.
                // This massively reduces file size, permits NVENC to work (it limits at 4096 max width), and makes saving nearly instant.
                _bgRecorder.StartInfo.Arguments = $"-y -use_wallclock_as_timestamps 1 -thread_queue_size 2048 -f lavfi -i \"ddagrab=output_idx={monIndex}:framerate={fps}:draw_mouse=1:video_size={w}x{h}\" {camInput}{audioInputs}{filterArg}-map \"{videoMap}\"{audioMap}{audioCodec}-c:v {encoder} -pix_fmt yuv420p -vsync 1 -f hls -hls_time {segmentTime} -hls_list_size {maxSegments} -hls_flags delete_segments \"{playlistPath}\"";
                _bgRecorder.StartInfo.UseShellExecute = false;
                _bgRecorder.StartInfo.CreateNoWindow = true;
                _bgRecorder.StartInfo.RedirectStandardInput = true;

                if (currentSessionId != _recorderSessionId || !_recordEnabled) return;

                _bgRecorder.Start();
                _isRecording = true;
                Dispatcher.BeginInvoke(new Action(() => {
                    SetStatus(GetString("TxtStatusReadyToClip"));
                }));
            });
        }

        private void StopBackgroundRecorder()
        {
            _recorderSessionId++; // Cancel any pending starting tasks
            _isRecording = false;
            try { _audioOutputRecorder?.Dispose(); _audioOutputRecorder = null; } catch { }
            try { _audioInputRecorder?.Dispose(); _audioInputRecorder = null; } catch { }

            if (_bgRecorder != null)
            {
                try
                {
                    if (!_bgRecorder.HasExited)
                    {
                        try {
                            _bgRecorder.StandardInput.WriteLine("q");
                            _bgRecorder.WaitForExit(3000);
                        } catch { }

                        if (!_bgRecorder.HasExited)
                        {
                            _bgRecorder.Kill();
                            _bgRecorder.WaitForExit(1000);
                        }
                    }
                }
                catch { }
                _bgRecorder = null;
            }

            SetStatus(GetString("TxtStatusBgRecDisabled"));

            // aggressive cleanup of old background recorders if needed just in case it orphaned before the fix
            try {
                var localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless").ToLower();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("ffmpeg"))
                {
                    try {
                        if (p.MainModule?.FileName.ToLower().StartsWith(localApp) == true)
                        {
                            p.Kill();
                        }
                    } catch {}
                }
            } catch {}
        }

        private string GetForegroundProcessName()
        {
            string gameName = "Desktop";
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                IntPtr shellHwnd = GetShellWindow();
                if (hwnd != IntPtr.Zero && hwnd != shellHwnd)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid > 0)
                    {
                        var process = System.Diagnostics.Process.GetProcessById((int)pid);
                        string procName = process.ProcessName;
                        if (!string.Equals(procName, "explorer", StringComparison.OrdinalIgnoreCase))
                        {
                            try { gameName = process.MainModule.FileVersionInfo.ProductName; } catch { }
                            if (string.IsNullOrWhiteSpace(gameName)) gameName = procName;
                        }
                    }
                }
            }
            catch { }

            // Ensure valid characters for folder name
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                gameName = gameName.Replace(c.ToString(), "");
            }

            if (string.IsNullOrWhiteSpace(gameName)) gameName = "Desktop";

            return gameName;
        }

        private void SaveBufferedClip(string forcedGameName = null)
        {
            if (!_isRecording) return;

            string sourceDir = Path.Combine(Path.GetTempPath(), "CliplessBuffer");
            string playlistSrc = Path.Combine(sourceDir, "buffer.m3u8");

            if (!File.Exists(playlistSrc)) return;

            // Cleanly stop background recorder to flush last segment
            StopBackgroundRecorder();

            string stitchDir = Path.Combine(Path.GetTempPath(), "CliplessStitch_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.Move(sourceDir, stitchDir);
            }
            catch
            {
                StartBackgroundRecorder();
                return;
            }

            // Immediately restart empty buffer for fresh clip timeline
            StartBackgroundRecorder();

            string playlistPath = Path.Combine(stitchDir, "buffer.m3u8");

            if (!File.Exists(playlistPath)) return;

            try
            {
                var uri = new Uri("pack://application:,,,/shot.wav");
                var streamInfo = System.Windows.Application.GetResourceStream(uri);
                if (streamInfo != null)
                {
                    using (var sp = new System.Media.SoundPlayer(streamInfo.Stream))
                    {
                        sp.Play();
                    }
                }
            }
            catch { }

            string gameName = forcedGameName ?? GetForegroundProcessName();

            string baseClipDir = string.IsNullOrEmpty(_basePath) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "NVIDIA") : _basePath;
            string outDir = Path.Combine(baseClipDir, gameName);
            Directory.CreateDirectory(outDir);

            string clipName = $"{gameName}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            string outPath = Path.Combine(outDir, clipName);

            if (_notifyIcon != null)
                _notifyIcon.ShowBalloonTip(2000, "Clipless", GetString("TxtStatusSaving"), System.Windows.Forms.ToolTipIcon.Info);

            SetStatus(GetString("TxtStatusSaving"));

            Task.Run(() => {
                try
                {
                    string ffmpegPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "ffmpeg", "ffmpeg.exe");
                    var stitchProcess = new System.Diagnostics.Process();
                    stitchProcess.StartInfo.FileName = ffmpegPath;

                    // We must allow ALL extensions before input mapping for HLS, then stream copy.
                    if (_normalizeOnClip)
                    {
                        stitchProcess.StartInfo.Arguments = $"-y -allowed_extensions ALL -i \"{playlistPath}\" -c:v copy -c:a aac -b:a 192k -af loudnorm -avoid_negative_ts make_zero -fflags +genpts \"{outPath}\"";
                    }
                    else
                    {
                        stitchProcess.StartInfo.Arguments = $"-y -allowed_extensions ALL -i \"{playlistPath}\" -c copy -avoid_negative_ts make_zero -fflags +genpts \"{outPath}\"";
                    }
                    stitchProcess.StartInfo.UseShellExecute = false;
                    stitchProcess.StartInfo.CreateNoWindow = true;
                    stitchProcess.Start();
                    stitchProcess.WaitForExit();

                    try { Directory.Delete(stitchDir, true); } catch { }

                    Dispatcher.BeginInvoke(new Action(() => {
                        if (File.Exists(outPath))
                        {
                            string savedText = GetString("TxtStatusClipSaved");
                            SetStatus($"{savedText} {clipName}");
                            if (_notifyIcon != null)
                                _notifyIcon.ShowBalloonTip(3000, "Clipless", $"{GetString("TxtTraySavedTo")} {gameName}\n{clipName}", System.Windows.Forms.ToolTipIcon.Info);

                            ScanForGameFolders();
                            var targetFolder = Folders.FirstOrDefault(f => string.Equals(f.FullPath, outDir, StringComparison.OrdinalIgnoreCase));
                            if (targetFolder != null && GameFolderList.SelectedItem == targetFolder)
                            {
                                RefreshCurrentFolder(targetFolder.FullPath);
                            }
                            else if (targetFolder != null)
                            {
                                GameFolderList.SelectedItem = targetFolder;
                            }
                        }
                        else
                        {
                            SetStatus(GetString("TxtStatusSaveError"));
                        }
                    }));
                }
                catch { Dispatcher.BeginInvoke(new Action(() => { SetStatus(GetString("TxtStatusSaveError")); })); }
            });
        }

        private string GetString(string key) => TryFindResource(key) as string
                                                ?? key;

        private void LanguageComboBox_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (LanguageComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string langCode)
            {
                if (_language != langCode)
                {
                    _language = langCode;
                    SaveSettings();
                }

                var dict = new ResourceDictionary
                {
                    Source =
                      new Uri($"Dictionaries/Strings.{langCode}.xaml", UriKind.Relative)
                };
                this.Resources.MergedDictionaries.Clear();
                this.Resources.MergedDictionaries.Add(dict);

                var favTag = TagsList.FirstOrDefault(t => t.Id == "FAVORITES_SPECIAL_TAG");
                if (favTag != null)
                {
                    favTag.Name = $"⭐ {GetString("TxtFavorites")}";
                }

                if (GameFolderList.SelectedItem is GameFolder selectedFolder)
                    RefreshCurrentFolder(selectedFolder.FullPath);
                if (TagsListBox.SelectedItem is ClipTag t && t.Id == "FAVORITES_SPECIAL_TAG")
                    RefreshFavorites();

                if (TxtAutoDeleteValue != null)
                    TxtAutoDeleteValue.Text = $"{_autoDeleteWeeks} {GetString("TxtWeeks")}";
                if (TxtBufferMinutesValue != null)
                    TxtBufferMinutesValue.Text = $"{_bufferMinutes} {GetString("TxtMin")}";

                LoadMonitors();
                LoadAudioDevices();
                LoadCameraDevices();
                UpdateTrayMenuUI();

                if (_isRecording) SetStatus(GetString("TxtStatusReadyToClip"));
                else SetStatus(GetString("TxtStatusBgRecDisabled"));
            }
        }

        private void LoadSettings()
        {
            TagsList.Clear();
            TagsList.Add(new ClipTag { Id = "FAVORITES_SPECIAL_TAG", Name = $"⭐ {GetString("TxtFavorites")}", Color = "#FFD700" });

            if (File.Exists(_tagsFilePath))
            {
                foreach(string line in File.ReadAllLines(_tagsFilePath))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 3 && parts[0] != "FAVORITES_SPECIAL_TAG")
                    {
                        TagsList.Add(new ClipTag { Id = parts[0], Name = parts[1], Color = parts[2] });
                    }
                }
            }
            BuildTagContextMenu();

            _clipTagMap.Clear();
            if (File.Exists(_clipTagsFilePath))
            {
                foreach(string line in File.ReadAllLines(_clipTagsFilePath))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 2)
                    {
                        string path = parts[0];
                        string[] tg = parts[1].Split(',');
                        _clipTagMap[path] = new HashSet<string>(tg);
                    }
                }
            }

            _basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "NVIDIA");
            _outPath = "";

            if (File.Exists(_settingsIniFilePath))
            {
                foreach(string line in File.ReadAllLines(_settingsIniFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("=")) continue;
                    var parts = line.Split(new[] { '=' }, 2);
                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    switch(key)
                    {
                        case "BasePath": _basePath = value; break;
                        case "Language": _language = value; break;
                        case "OutPath": _outPath = value; break;
                        case "TargetSizeMb": if(int.TryParse(value, out int size)) _targetSizeMb = size; break;
                        case "RenderEngine": _renderEngine = value; break;
                        case "AutoDeleteEnabled": if(bool.TryParse(value, out bool enabled)) _autoDeleteEnabled = enabled; break;
                        case "AutoDeleteWeeks": if(int.TryParse(value, out int w)) _autoDeleteWeeks = Math.Max(1, Math.Min(12, w)); break;
                        case "Volume": if(int.TryParse(value, out int vol)) { _volume = Math.Max(0, Math.Min(100, vol)); } break;
                        case "RecordEnabled": if(bool.TryParse(value, out bool recEn)) _recordEnabled = recEn; break;
                        case "BufferMinutes": if(int.TryParse(value, out int bMin)) _bufferMinutes = Math.Max(1, Math.Min(30, bMin)); break;
                        case "CaptureFramerate": _captureFramerate = value; break;
                        case "CaptureResolution": _captureResolution = value; break;
                        case "AudioOutputId": _audioOutputId = value; break;
                        case "AudioInputId": _audioInputId = value; break;
                        case "CameraId": _cameraId = value; break;
                        case "CameraOverlayEnabled": if(bool.TryParse(value, out bool cEn)) _cameraOverlayEnabled = cEn; break;
                        case "NormalizeOnClip": if(bool.TryParse(value, out bool nOn)) _normalizeOnClip = nOn; break;
                        case "CaptureMonitorIndex": if(int.TryParse(value, out int mIdx)) _captureMonitorIndex = mIdx; break;
                        case "HotkeyModifiers": if(uint.TryParse(value, out uint hMod)) _hotkeyModifiers = hMod; break;
                        case "HotkeyKey": if(int.TryParse(value, out int hKey)) _hotkeyKey = hKey; break;
                    }
                }
            }

            TxtFolderPath.Text = _basePath;
            TxtOutputFolderPath.Text = string.IsNullOrEmpty(_outPath) ? $"({GetString("TxtSameAsSource")})" : _outPath;

            if (LanguageComboBox != null)
            {
                foreach (ComboBoxItem item in LanguageComboBox.Items)
                {
                    if (item.Tag?.ToString() == _language)
                    {
                        LanguageComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            LoadAudioDevices();
            LoadCameraDevices();

            if (TargetSizeSlider != null)
            {
                TargetSizeSlider.Value = _targetSizeMb;
            }

            _favorites.Clear();
            if (File.Exists(_favoritesFilePath))
            {
                foreach (var line in File.ReadAllLines(_favoritesFilePath))
                {
                    if (!string.IsNullOrWhiteSpace(line)) _favorites.Add(line);
                }
            }

            if (RenderEngineComboBox != null)
            {
                RenderEngineComboBox.SelectedIndex = _renderEngine == "GPU" ? 1 : 0;
            }
            if (ResolutionComboBox != null)
            {
                ResolutionComboBox.SelectedIndex = _captureResolution == "1080p" ? 1 : (_captureResolution == "720p" ? 2 : 0);
            }
            if (FramerateComboBox != null)
            {
                FramerateComboBox.SelectedIndex = _captureFramerate == "30" ? 1 : 0;
            }

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("Clipless") as string;
                        _autoStartEnabled = (val != null);
                        if (_autoStartEnabled && !val.Contains("-autostart"))
                        {
                            key.SetValue("Clipless", $"\"{Environment.ProcessPath}\" -autostart");
                        }
                    }
                }
            }
            catch { }

            if (ChkAutoStart != null)
            {
                ChkAutoStart.IsChecked = _autoStartEnabled;
            }

            if (ChkAutoDelete != null)
            {
                ChkAutoDelete.IsChecked = _autoDeleteEnabled;
            }

            if (AutoDeleteSlider != null)
            {
                AutoDeleteSlider.Value = _autoDeleteWeeks;
            }
            if (TxtAutoDeleteValue != null)
                TxtAutoDeleteValue.Text = $"{_autoDeleteWeeks} {GetString("TxtWeeks")}";

            if (ChkRecordEnabled != null)
            {
                ChkRecordEnabled.IsChecked = _recordEnabled;
            }
            if (BufferMinutesSlider != null)
            {
                BufferMinutesSlider.Value = _bufferMinutes;
            }
            if (TxtBufferMinutesValue != null)
            {
                TxtBufferMinutesValue.Text = $"{_bufferMinutes} {GetString("TxtMin")}";
            }

            if (VolumeSlider != null) VolumeSlider.Value = _volume;

            if (ChkNormalizeOnClip != null) ChkNormalizeOnClip.IsChecked = _normalizeOnClip;

            LoadMonitors();
            UpdateHotkeyUI();

            if (_autoDeleteEnabled)
            {
                RunAutoDelete();
            }

            _isLoaded = true;
        }

        private void LoadMonitors()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var outList = new List<MonitorItem>();
            for (int i = 0; i < screens.Length; i++)
            {
                outList.Add(new MonitorItem { Index = i, Name = $"{GetString("TxtMonitor")} {i + 1} ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})" });
            }

            if (MonitorComboBox != null)
            {
                MonitorComboBox.ItemsSource = outList;
                MonitorComboBox.SelectedIndex = _captureMonitorIndex >= 0 && _captureMonitorIndex < outList.Count ? _captureMonitorIndex : 0;
            }
        }

        private void UpdateHotkeyUI()
        {
            if (TxtSaveHotkey != null)
            {
                string mods = "";
                if ((_hotkeyModifiers & MOD_CTRL) != 0) mods += "Ctrl + ";
                if ((_hotkeyModifiers & MOD_SHIFT) != 0) mods += "Shift + ";
                if ((_hotkeyModifiers & MOD_ALT) != 0) mods += "Alt + ";
                TxtSaveHotkey.Text = mods + System.Windows.Input.KeyInterop.KeyFromVirtualKey(_hotkeyKey).ToString();
            }
            UpdateTrayMenuUI();
        }

        private void UpdateHotkey()
        {
            UpdateHotkeyUI();
        }

        private void TxtSaveHotkey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true;

            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl ||
                key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift ||
                key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt ||
                key == System.Windows.Input.Key.LWin || key == System.Windows.Input.Key.RWin)
            {
                return; // wait for actual key
            }

            uint mods = 0;
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) mods |= MOD_CTRL;
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) mods |= MOD_SHIFT;
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) mods |= MOD_ALT;

            _hotkeyModifiers = mods;
            _hotkeyKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);

            UpdateHotkey();
            SaveSettings();

            // Reset focus
            System.Windows.Input.Keyboard.ClearFocus();
        }

        private void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MonitorComboBox.SelectedItem is MonitorItem item)
            {
                _captureMonitorIndex = item.Index;
            }
        }

        private void ChkNormalizeOnClip_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkNormalizeOnClip != null)
            {
                _normalizeOnClip = ChkNormalizeOnClip.IsChecked == true;
            }
        }

        private void SaveSettings()
        {
            if (!_isLoaded) return;

            var lines = new List<string>
            {
                $"BasePath={_basePath}",
                $"Language={_language}",
                $"OutPath={_outPath}",
                $"TargetSizeMb={_targetSizeMb}",
                $"RenderEngine={_renderEngine}",
                $"AutoDeleteEnabled={_autoDeleteEnabled}",
                $"AutoDeleteWeeks={_autoDeleteWeeks}",
                $"Volume={_volume}",
                $"RecordEnabled={_recordEnabled}",
                $"BufferMinutes={_bufferMinutes}",
                $"CaptureFramerate={_captureFramerate}",
                $"CaptureResolution={_captureResolution}",
                $"AudioOutputId={_audioOutputId}",
                $"AudioInputId={_audioInputId}",
                $"CameraId={_cameraId}",
                $"CameraOverlayEnabled={_cameraOverlayEnabled}",
                $"NormalizeOnClip={_normalizeOnClip}",
                $"CaptureMonitorIndex={_captureMonitorIndex}",
                $"HotkeyModifiers={_hotkeyModifiers}",
                $"HotkeyKey={_hotkeyKey}"
            };
            try { File.WriteAllLines(_settingsIniFilePath, lines); } catch { }
        }

        private void SaveFavorites()
        {
            File.WriteAllLines(_favoritesFilePath, _favorites);
        }

        private void BtnToggleTags_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (TagsListBox.Visibility == Visibility.Visible)
            {
                TagsListBox.Visibility = Visibility.Collapsed;
                TagsListRow.Height = GridLength.Auto;
                if (TxtTagsArrow != null) TxtTagsArrow.Text = "▶ ";
            }
            else
            {
                TagsListBox.Visibility = Visibility.Visible;
                TagsListRow.Height = new GridLength(1, GridUnitType.Star);
                if (TxtTagsArrow != null) TxtTagsArrow.Text = "▼ ";
            }
        }

        private void BtnToggleFolders_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GameFolderList.Visibility == Visibility.Visible)
            {
                GameFolderList.Visibility = Visibility.Collapsed;
                FoldersListRow.Height = GridLength.Auto;
                if (TxtFoldersArrow != null) TxtFoldersArrow.Text = "▶ ";
            }
            else
            {
                GameFolderList.Visibility = Visibility.Visible;
                FoldersListRow.Height = new GridLength(1, GridUnitType.Star);
                if (TxtFoldersArrow != null) TxtFoldersArrow.Text = "▼ ";
            }
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is VideoClip clip)
            {
                if (clip.IsFavorite) _favorites.Add(clip.FullPath);
                else _favorites.Remove(clip.FullPath);
                SaveFavorites();

                if (TagsListBox.SelectedItem is ClipTag t && t.Id == "FAVORITES_SPECIAL_TAG" && !clip.IsFavorite)
                {
                    Clips.Remove(clip);
                }
            }
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
                SaveSettings();
                Clips.Clear();
                ScanForGameFolders();
            }
        }

        private void BtnBrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
            if (Directory.Exists(_outPath))
                dialog.InitialDirectory = _outPath;
            else if (Directory.Exists(_basePath))
                dialog.InitialDirectory = _basePath;

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                _outPath = dialog.FileName;
                TxtOutputFolderPath.Text = _outPath;
                SaveSettings();
            }
        }

        private void BtnClearOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            _outPath = "";
            TxtOutputFolderPath.Text = $"({GetString("TxtSameAsSource")})";
            SaveSettings();
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

        private void BuildTagContextMenu()
        {
            if (MenuTagClip != null)
            {
                MenuTagClip.Items.Clear();
                foreach (var tag in TagsList.Where(t => t.Id != "FAVORITES_SPECIAL_TAG"))
                {
                    var mi = new System.Windows.Controls.MenuItem { Header = tag.Name, Tag = tag.Id };
                    mi.Click += (s, e) => ToggleClipTag(tag.Id);
                    MenuTagClip.Items.Add(mi);
                }
            }
        }

        private void ToggleClipTag(string tagId)
        {
            if (ClipList.SelectedItems.Count > 0)
            {
                foreach(VideoClip clip in ClipList.SelectedItems)
                {
                    if (!_clipTagMap.ContainsKey(clip.FullPath)) _clipTagMap[clip.FullPath] = new HashSet<string>();

                    if (_clipTagMap[clip.FullPath].Contains(tagId))
                        _clipTagMap[clip.FullPath].Remove(tagId);
                    else
                        _clipTagMap[clip.FullPath].Add(tagId);

                    if (_clipTagMap[clip.FullPath].Count == 0)
                        _clipTagMap.Remove(clip.FullPath);
                }
                SaveClipTags();
            }
        }

        private void SaveTags()
        {
            File.WriteAllLines(_tagsFilePath, TagsList.Where(g => g.Id != "FAVORITES_SPECIAL_TAG").Select(t => $"{t.Id}|{t.Name}|{t.Color}"));
            BuildTagContextMenu();
        }

        private void SaveClipTags()
        {
            var lines = new List<string>();
            foreach(var kvp in _clipTagMap)
            {
                lines.Add($"{kvp.Key}|{string.Join(",", kvp.Value)}");
            }
            File.WriteAllLines(_clipTagsFilePath, lines);
        }

        private void BtnCreateTag_Click(object sender, RoutedEventArgs e)
        {
            TxtTagInput.Text = "";
            TagInputOverlay.Visibility = Visibility.Visible;
            VideoPlayer.Visibility = Visibility.Hidden;
            TxtTagInput.Focus();
        }

        private void BtnSaveTagInput_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtTagInput.Text.Trim();
            string color = (CmbTagColor.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FF0000";
            if (!string.IsNullOrWhiteSpace(name))
            {
                TagsList.Add(new ClipTag { Name = name, Color = color });
                SaveTags();
            }
            TagInputOverlay.Visibility = Visibility.Collapsed;
            if (PlayerOverlay.Visibility == Visibility.Visible) VideoPlayer.Visibility = Visibility.Visible;
        }

        private void BtnCancelTagInput_Click(object sender, RoutedEventArgs e)
        {
            TagInputOverlay.Visibility = Visibility.Collapsed;
            if (PlayerOverlay.Visibility == Visibility.Visible) VideoPlayer.Visibility = Visibility.Visible;
        }

        private void TagsListBox_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item) item.IsSelected = true;
        }

        private async void MenuTagDelete_Click(object sender, RoutedEventArgs e)
        {
            if (TagsListBox.SelectedItem is ClipTag t && t.Id != "FAVORITES_SPECIAL_TAG")
            {
                if (await ShowDeleteConfirmOverlayAsync("TxtConfirmDeleteTitle", "TxtConfirmDelete") == System.Windows.MessageBoxResult.Yes)
                {
                    TagsList.Remove(t);
                    SaveTags();

                    // clean orphans
                    var keys = _clipTagMap.Keys.ToList();
                    bool modified = false;
                    foreach(var k in keys)
                    {
                        if (_clipTagMap[k].Contains(t.Id))
                        {
                            _clipTagMap[k].Remove(t.Id);
                            if (_clipTagMap[k].Count == 0) _clipTagMap.Remove(k);
                            modified = true;
                        }
                    }
                    if (modified) SaveClipTags();
                }
            }
        }

        private VideoClip CreateClipModel(string file, string fileCreatedStr, bool isFavorite)
        {
            DateTime ct = File.GetCreationTime(file);
            long fileSizeInBytes = new FileInfo(file).Length;
            string fileSizeText = fileSizeInBytes >= 1048576 
                ? $"{fileSizeInBytes / 1048576.0:F1} MB" 
                : $"{fileSizeInBytes / 1024.0:F1} KB";

            return new VideoClip
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                Thumbnail = null,
                CreationDate = $"{ct:MMM dd, yyyy HH:mm}",
                FileSize = fileSizeText,
                ActualFileSize = fileSizeInBytes,
                ActualCreationTime = ct,
                IsFavorite = isFavorite
            };
        }

        private async void TagsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagsListBox.SelectedItem is ClipTag tag)
            {
                if (PlayerOverlay.Visibility == Visibility.Visible)
                {
                    ClosePlayer_Click(null, null);
                }

                GameFolderList.SelectedItem = null;
                TxtNoFolderMessage.Visibility = Visibility.Collapsed;
                _folderWatcher.EnableRaisingEvents = false;

                if (tag.Id == "FAVORITES_SPECIAL_TAG")
                {
                    RefreshFavorites();
                    return;
                }

                Clips.Clear();
                var clipMapSnapshot = new Dictionary<string, HashSet<string>>(_clipTagMap);
                var favoritesSnapshot = new HashSet<string>(_favorites);
                string fileCreatedStr = GetString("TxtFileCreated");

                var result = await Task.Run(() => {
                    var temp = new List<VideoClip>();
                    var validMapUpdates = new Dictionary<string, HashSet<string>>();
                    bool cleanupNeeded = false;

                    foreach (var kvp in clipMapSnapshot)
                    {
                        if (kvp.Value.Contains(tag.Id))
                        {
                            string file = kvp.Key;
                            if (File.Exists(file))
                            {
                                validMapUpdates[file] = kvp.Value;
                                temp.Add(CreateClipModel(file, fileCreatedStr, favoritesSnapshot.Contains(file)));
                            }
                            else
                            {
                                cleanupNeeded = true;
                            }
                        }
                        else
                        {
                             validMapUpdates[kvp.Key] = kvp.Value;
                        }
                    }
                    return (temp, cleanupNeeded, validMapUpdates);
                });

                if (result.cleanupNeeded)
                {
                    _clipTagMap = result.validMapUpdates;
                    SaveClipTags();
                }

                foreach (var clip in SortClipList(result.temp))
                {
                    Clips.Add(clip);
                    LoadThumbnailAsync(clip);
                }
            }
        }

        private void GameFolderList_SelectionChanged(object sender,
                                                     SelectionChangedEventArgs e)
        {
            if (GameFolderList.SelectedItem is GameFolder folder)
            {
                if (PlayerOverlay.Visibility == Visibility.Visible)
                {
                    ClosePlayer_Click(null, null);
                }

                TagsListBox.SelectedItem = null;
                TxtNoFolderMessage.Visibility = Visibility.Collapsed;
                if (Directory.Exists(folder.FullPath))
                {
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
        }

        private async void RefreshFavorites()
        {
            Clips.Clear();
            var favoritesSnapshot = new HashSet<string>(_favorites);
            string fileCreatedStr = GetString("TxtFileCreated");

            var result = await Task.Run(() => {
                var temp = new List<VideoClip>();
                var validFavorites = new List<string>();
                foreach (string file in favoritesSnapshot)
                {
                    if (File.Exists(file))
                    {
                        validFavorites.Add(file);
                        temp.Add(CreateClipModel(file, fileCreatedStr, true));
                    }
                }
                return (temp, validFavorites);
            });

            if (result.validFavorites.Count != _favorites.Count)
            {
                _favorites = new HashSet<string>(result.validFavorites);
                SaveFavorites();
            }
            foreach (var clip in SortClipList(result.temp))
            {
                Clips.Add(clip);
                LoadThumbnailAsync(clip);
            }
        }

        private async void RefreshCurrentFolder(string path)
        {
            Clips.Clear();
            var favoritesSnapshot = new HashSet<string>(_favorites);
            string fileCreatedStr = GetString("TxtFileCreated");

            var temp = await Task.Run(() => {
                var list = new List<VideoClip>();
                foreach (string file in Directory.GetFiles(path, "*.mp4"))
                {
                    list.Add(CreateClipModel(file, fileCreatedStr, favoritesSnapshot.Contains(file)));
                }
                return list;
            });

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
                {
                    if (Directory.Exists(f.FullPath)) RefreshCurrentFolder(f.FullPath);
                }
                else if (TagsListBox.SelectedItem is ClipTag t)
                {
                    if (t.Id == "FAVORITES_SPECIAL_TAG") RefreshFavorites();
                    else TagsListBox_SelectionChanged(null, null);
                }
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
                if (_mediaPlayer.Length - _mediaPlayer.Time > 5000)
                {
                    long newTime = _mediaPlayer.Time + 5000;
                    _mediaPlayer.Time = newTime;
                    if (!_mediaPlayer.IsPlaying)
                    {
                        _isUpdatingFromPlayer = true;
                        ProgressSlider.Value = newTime;
                        TimeDisplay.Text = $"{TimeSpan.FromMilliseconds(newTime):mm\\:ss\\.ff} / {TimeSpan.FromMilliseconds(_mediaPlayer.Length):mm\\:ss\\.ff}";
                        _isUpdatingFromPlayer = false;
                    }
                }
            }
            else if (e.Key == System.Windows.Input.Key.Left)
            {
                e.Handled = true;
                long newTime = (long)Math.Max(0, _mediaPlayer.Time - 5000);
                _mediaPlayer.Time = newTime;
                if (!_mediaPlayer.IsPlaying)
                {
                    _isUpdatingFromPlayer = true;
                    ProgressSlider.Value = newTime;
                    TimeDisplay.Text = $"{TimeSpan.FromMilliseconds(newTime):mm\\:ss\\.ff} / {TimeSpan.FromMilliseconds(_mediaPlayer.Length):mm\\:ss\\.ff}";
                    _isUpdatingFromPlayer = false;
                }
            }
            else if (e.Key == System.Windows.Input.Key.OemComma)
            {
                e.Handled = true;
                if (_mediaPlayer.IsPlaying) { _mediaPlayer.Pause(); PlayPauseButton.Content = "▶️"; }
                long newTime = (long)Math.Max(0, _mediaPlayer.Time - 33);
                _mediaPlayer.Time = newTime;
                _isUpdatingFromPlayer = true;
                ProgressSlider.Value = newTime;
                TimeDisplay.Text = $"{TimeSpan.FromMilliseconds(newTime):mm\\:ss\\.ff} / {TimeSpan.FromMilliseconds(_mediaPlayer.Length):mm\\:ss}";
                _isUpdatingFromPlayer = false;
            }
            else if (e.Key == System.Windows.Input.Key.OemPeriod)
            {
                e.Handled = true;
                if (_mediaPlayer.IsPlaying) { _mediaPlayer.Pause(); PlayPauseButton.Content = "▶️"; }
                if (_mediaPlayer.Length - _mediaPlayer.Time > 16)
                {
                    long oldTime = _mediaPlayer.Time;
                    _mediaPlayer.NextFrame();

                    // Poll for new time since VLC updates it asynchronously
                    Task.Run(async () => {
                        for (int i = 0; i < 20; i++) {
                            await Task.Delay(10);
                            long t = _mediaPlayer.Time;
                            if (t != oldTime && t >= 0) {
                                Dispatcher.BeginInvoke(new Action(() => {
                                    _isUpdatingFromPlayer = true;
                                    ProgressSlider.Value = t;
                                    TimeDisplay.Text = $"{TimeSpan.FromMilliseconds(t):mm\\:ss\\.ff} / {TimeSpan.FromMilliseconds(_mediaPlayer.Length):mm\\:ss}";
                                    _isUpdatingFromPlayer = false;
                                }));
                                break;
                            }
                        }
                    });
                }
            }
        }

        private List<VideoClip> SortClipList(List<VideoClip> list)
        {
            int idx = SortComboBox?.SelectedIndex ?? 0;
            return idx == 0
                       ? list.OrderByDescending(c => c.ActualCreationTime).ToList()
                       : (idx == 1 ? list.OrderBy(c => c.ActualCreationTime).ToList()
                                   : (idx == 2 ? list.OrderBy(c => c.Name).ToList()
                                               : (idx == 3 ? list.OrderByDescending(c => c.ActualFileSize).ToList()
                                                           : list.OrderBy(c => c.ActualFileSize).ToList())));
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

        private async Task GenerateAndLoadWaveformAsync(VideoClip clip)
        {
            try
            {
                string cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "WaveformCache");
                Directory.CreateDirectory(cachePath);

                string hash = GetCacheFolderName(clip);
                string waveFile = Path.Combine(cachePath, $"{hash}_v2.png");

                if (!File.Exists(waveFile))
                {
                    try { await DownloadFFmpegIfNeeded(); } catch { }
                    string ffmpegPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "ffmpeg", "ffmpeg.exe");

                    if (File.Exists(ffmpegPath))
                    {
                        var process = new System.Diagnostics.Process();
                        process.StartInfo.FileName = ffmpegPath;
                        process.StartInfo.Arguments = $"-y -i \"{clip.FullPath}\" -filter_complex \"[0:a]aformat=channel_layouts=mono,volume=5.0,showwavespic=s=800x80:colors=Yellow:scale=cbrt\" -frames:v 1 \"{waveFile}\"";
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.CreateNoWindow = true;
                        process.Start();
                        await process.WaitForExitAsync();
                    }
                }

                if (File.Exists(waveFile) && _currentClip == clip)
                {
                    Dispatcher.BeginInvoke(new Action(() => {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(waveFile);
                        bmp.EndInit();
                        bmp.Freeze();
                        if (WaveformImage != null) WaveformImage.Source = bmp;
                    }));
                }
            }
            catch { }
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
                if (SpeedSlider != null) SpeedSlider.Value = 1.0;

                if (WaveformImage != null) WaveformImage.Source = null;
                _ = GenerateAndLoadWaveformAsync(clip);

                await Task.Delay(100);
                VideoPlayer.MediaPlayer = null;
                VideoPlayer.MediaPlayer = _mediaPlayer;
                PlayPauseButton.Content = "II";
                _mediaPlayer.Play(new Media(_libVLC, new Uri(clip.FullPath)));
                if (VolumeSlider != null) _mediaPlayer.Volume = (int)VolumeSlider.Value;
            }
        }

        private void ClosePlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
                if (_mediaPlayer.Media != null)
                {
                    _mediaPlayer.Media.Dispose();
                    _mediaPlayer.Media = null;
                }
            }
            if (PlayerOverlay != null) PlayerOverlay.Visibility = Visibility.Collapsed;
            if (VideoPlayer != null) VideoPlayer.Visibility = Visibility.Hidden;
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
            if (_isScrubbing) return;
            Dispatcher.BeginInvoke(new Action(() => {
                if (_isScrubbing) return;
                _isUpdatingFromPlayer = true;
                ProgressSlider.Value = e.Time;
                TimeDisplay.Text =
                    $"{TimeSpan.FromMilliseconds(e.Time):mm\\:ss\\.ff} / {TimeSpan.FromMilliseconds(_mediaPlayer.Length):mm\\:ss}";
                _isUpdatingFromPlayer = false;
            }));
        }

        private void MediaPlayer_EndReached(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                PlayPauseButton.Content = "▶️";
            }));
        }

        private void ProgressSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isScrubbing = true;
            if (_mediaPlayer != null)
            {
                _wasPlayingBeforeScrub = _mediaPlayer.IsPlaying;
                if (_wasPlayingBeforeScrub)
                {
                    _mediaPlayer.Pause();
                    PlayPauseButton.Content = "▶️";
                }
            }
        }

        private void ProgressSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isScrubbing = false;
            if (_mediaPlayer != null)
            {
                long t = (long)Math.Max(0, Math.Min(_mediaPlayer.Length > 1000 ? _mediaPlayer.Length - 1000 : 0, ProgressSlider.Value));
                _mediaPlayer.Time = t;

                if (_wasPlayingBeforeScrub)
                {
                    _mediaPlayer.Play();
                    PlayPauseButton.Content = "II";
                }
            }
        }

        private void ProgressSlider_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isUpdatingFromPlayer && _mediaPlayer != null)
            {
                if (_mediaPlayer.State == LibVLCSharp.Shared.VLCState.Ended || _mediaPlayer.State == LibVLCSharp.Shared.VLCState.Stopped)
                {
                    if (_currentClip != null)
                    {
                        var m = new Media(_libVLC, new Uri(_currentClip.FullPath));
                        _mediaPlayer.Play(m);
                        PlayPauseButton.Content = "II";
                    }
                }
                long t = (long)Math.Max(0, Math.Min(_mediaPlayer.Length > 1000 ? _mediaPlayer.Length - 1000 : 0, (long)e.NewValue));
                if (!_isScrubbing)
                {
                    _mediaPlayer.Time = t;
                    if (!_mediaPlayer.IsPlaying)
                    {
                        TimeDisplay.Text = $"{TimeSpan.FromMilliseconds(t):mm\\:ss\\.ff} / {TimeSpan.FromMilliseconds(_mediaPlayer.Length):mm\\:ss}";
                    }
                }
                else
                {
                    if ((DateTime.Now - _lastScrubTime).TotalMilliseconds > 40) // Limit seeks to ~25fps to prevent VLC choke
                    {
                        _lastScrubTime = DateTime.Now;
                        Task.Run(() => {
                            try { if (_mediaPlayer != null) _mediaPlayer.Time = t; } catch { }
                        });
                    }
                    TimeDisplay.Text = $"{TimeSpan.FromMilliseconds(t):mm\\:ss\\.ff} / {TimeSpan.FromMilliseconds(_mediaPlayer.Length):mm\\:ss}";
                }
            }
        }
        private void VolumeSlider_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
                _mediaPlayer.Volume = (int)e.NewValue;

            try
            {
                _volume = (int)e.NewValue;
                SaveSettings();
            }
            catch { }
        }
        private void SpeedSlider_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.SetRate((float)e.NewValue);
            }
            if (SpeedText != null)
            {
                SpeedText.Text = $"{e.NewValue:0.00}x";
            }
        }
        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null)
                return;

            if (_mediaPlayer.State == LibVLCSharp.Shared.VLCState.Ended || _mediaPlayer.State == LibVLCSharp.Shared.VLCState.Stopped || _mediaPlayer.State == LibVLCSharp.Shared.VLCState.Error)
            {
                if (_currentClip != null)
                {
                    _mediaPlayer.Play(new Media(_libVLC, new Uri(_currentClip.FullPath)));
                    PlayPauseButton.Content = "II";
                }
                return;
            }

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                PlayPauseButton.Content = "▶️";
            }
            else
            {
                if (_mediaPlayer.Length > 0 && Math.Abs(_mediaPlayer.Length - _mediaPlayer.Time) <= 500)
                {
                    _mediaPlayer.Time = 0;
                }
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
            ChkOptimize.Content = $"{GetString("TxtOptimize")} {_targetSizeMb}MB";
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
            System.Windows.Clipboard.SetFileDropList(f);
        }
        private void MenuCut_Click(object sender, RoutedEventArgs e)
        {
            if (ClipList.SelectedItems.Count == 0)
                return;
            var f = new System.Collections.Specialized.StringCollection();
            foreach (VideoClip c in ClipList.SelectedItems) f.Add(c.FullPath);
            System.Windows.DataObject d = new System.Windows.DataObject();
            d.SetFileDropList(f);
            d.SetData("Preferred DropEffect",
                      new MemoryStream(new byte[] { 2, 0, 0, 0 }));
            System.Windows.Clipboard.SetDataObject(d);
        }
        private void MenuClipPaste_Click(object sender, RoutedEventArgs e)
        {
            if (GameFolderList.SelectedItem is GameFolder f &&
                System.Windows.Clipboard.ContainsFileDropList())
            {
                foreach (string file in System.Windows.Clipboard.GetFileDropList())
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
                System.Windows.Clipboard.ContainsFileDropList())
            {
                foreach (string file in System.Windows.Clipboard.GetFileDropList())
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
            bool optimize = ChkOptimize.IsChecked == true;
            bool normalizeAudio = ChkNormalizeAudio.IsChecked == true;
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
                string ffmpegPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "ffmpeg");
                Directory.CreateDirectory(ffmpegPath);
                FFmpeg.SetExecutablesPath(ffmpegPath);
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegPath);
                string inp = _currentClip.FullPath, dir = Path.GetDirectoryName(inp),
                       name = Path.GetFileNameWithoutExtension(inp);

                string outDir = string.IsNullOrEmpty(_outPath) ? dir : _outPath;
                string tmp = Path.Combine(outDir, name + "_temp.mp4");
                string nfp = Path.Combine(outDir, $"Clipless_{name}.mp4");

                int c = 1;
                while (File.Exists(nfp))
                    nfp = Path.Combine(outDir, $"Clipless_{c++}_{name}.mp4");
                var mediaInfo = await FFmpeg.GetMediaInfo(inp);
                var conv = FFmpeg.Conversions.New()
                               .AddStream(mediaInfo.Streams)
                               .SetSeek(TimeSpan.FromMilliseconds(_trimStartMs))
                               .SetOutputTime(
                                   TimeSpan.FromMilliseconds(_trimEndMs - _trimStartMs));

                if (optimize)
                {
                    double durationSec = (_trimEndMs - _trimStartMs) / 1000.0;
                    if (durationSec <= 0) durationSec = 1;
                    double targetTbps = (_targetSizeMb * 8000.0 * 0.975) / durationSec; 
                    int vBitrate = (int)Math.Max(100, targetTbps - 128);

                    string encoder = _renderEngine == "GPU" ? "h264_nvenc" : "libx264";
                    string audioFilter = normalizeAudio ? " -af loudnorm" : "";
                    conv.AddParameter($"-c:v {encoder} -preset fast -b:v {vBitrate}k -maxrate {vBitrate}k -bufsize {vBitrate*2}k -c:a aac -b:a 128k{audioFilter}");
                }
                else
                {
                    if (normalizeAudio)
                    {
                        conv.AddParameter("-c:v copy -c:a aac -b:a 128k -af loudnorm");
                    }
                    else
                    {
                        conv.AddParameter("-c copy");
                    }
                }
                conv.SetOutput(tmp);

                conv.OnProgress += (s, a) => Dispatcher.BeginInvoke(new Action(() => {
                    ExportProgressBar.Value = a.Percent;
                    ExportProgressText.Text = $"{a.Percent}%";
                }));
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

        private Task<ImageSource> GetThumbnailSTAAsync(string path)
        {
            var tcs = new TaskCompletionSource<ImageSource>();
            _staJobs.Add(() =>
            {
                try
                {
                    using var sf = ShellFile.FromFilePath(path);
                    var b = sf.Thumbnail.BitmapSource;
                    b.Freeze();
                    tcs.SetResult(b);
                }
                catch
                {
                    tcs.SetResult(null);
                }
            });
            return tcs.Task;
        }

        private async void LoadThumbnailAsync(VideoClip clip)
        {
            if (_thumbnailCache.TryGetValue(clip.FullPath, out var t))
            {
                clip.DefaultThumbnail = t;
                clip.Thumbnail = t;
                EnqueueHoverThumbnailGeneration(clip);
                return;
            }
            var bt = await GetThumbnailSTAAsync(clip.FullPath);
            if (bt != null)
            {
                _thumbnailCache[clip.FullPath] = bt;
                clip.DefaultThumbnail = bt;
                if (clip.Thumbnail == null) clip.Thumbnail = bt;
            }
            EnqueueHoverThumbnailGeneration(clip);
        }

        private Queue<VideoClip> _thumbnailQueue = new Queue<VideoClip>();
        private bool _isProcessingThumbnails = false;

        private void EnqueueHoverThumbnailGeneration(VideoClip clip)
        {
            if (!_thumbnailQueue.Contains(clip))
            {
                _thumbnailQueue.Enqueue(clip);
            }
            if (!_isProcessingThumbnails)
            {
                ProcessThumbnailQueueAsync();
            }
        }

        private async Task DownloadFFmpegIfNeeded()
        {
            string ffmpegPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "ffmpeg");
            Directory.CreateDirectory(ffmpegPath);
            FFmpeg.SetExecutablesPath(ffmpegPath);
            if (!File.Exists(Path.Combine(ffmpegPath, "ffmpeg.exe")))
            {
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegPath);
            }
        }

        private async void ProcessThumbnailQueueAsync()
        {
            _isProcessingThumbnails = true;
            try { await DownloadFFmpegIfNeeded(); } catch { } 

            while (_thumbnailQueue.Count > 0)
            {
                var clip = _thumbnailQueue.Dequeue();
                await GenerateHoverFramesAsync(clip);
            }
            _isProcessingThumbnails = false;
        }

        private string GetCacheFolderName(VideoClip clip)
        {
            string input = clip.FullPath + clip.ActualCreationTime.Ticks;
            using var sha = System.Security.Cryptography.SHA256.Create();
            return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input)).Select(b => b.ToString("x2")));
        }

        private async Task GenerateHoverFramesAsync(VideoClip clip)
        {
            string cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "ThumbCache");
            string hash = GetCacheFolderName(clip);
            string clipCacheDir = Path.Combine(cachePath, hash);

            bool fullyCached = true;
            if (Directory.Exists(clipCacheDir))
            {
                for (int i = 1; i <= 10; i++)
                {
                    if (!File.Exists(Path.Combine(clipCacheDir, $"frame_{i}.jpg"))) 
                    {
                        fullyCached = false;
                        break;
                    }
                }
            }
            else
            {
                fullyCached = false;
            }

            if (!fullyCached)
            {
                Directory.CreateDirectory(clipCacheDir);
                try
                {
                    var md = await FFmpeg.GetMediaInfo(clip.FullPath);
                    double duration = md.Duration.TotalSeconds;
                    if (duration > 0)
                    {
                        double fps = 10.0 / duration;
                        string fPattern = Path.Combine(clipCacheDir, "frame_%d.jpg");
                        var conv = FFmpeg.Conversions.New().AddParameter($"-y -threads 1 -i \"{clip.FullPath}\" -vf \"fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)},scale=320:-1\" -vframes 10 -q:v 5 \"{fPattern}\"");
                        await conv.Start();
                    }
                }
                catch { }
            }

            if (File.Exists(Path.Combine(clipCacheDir, "frame_1.jpg")))
            {
                Dispatcher.BeginInvoke(() => {
                    if (clip.Thumbnail == clip.DefaultThumbnail || clip.DefaultThumbnail == null)
                    {
                        try
                        {
                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.UriSource = new Uri(Path.Combine(clipCacheDir, "frame_1.jpg"));
                            bmp.EndInit();
                            bmp.Freeze();
                            clip.DefaultThumbnail = bmp;
                            if (_hoveredClip != clip) clip.Thumbnail = bmp;
                        } catch { }
                    }
                });
            }
        }

        private VideoClip _hoveredClip;

        private void Clip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is VideoClip clip)
            {
                _hoveredClip = clip;
                _hoverFrameIndex = 1;
                if (_hoverTimer != null)
                {
                    _hoverTimer.Start();
                    UpdateHoverFrame();
                }
            }
        }

        private void HoverTimer_Tick(object sender, EventArgs e)
        {
            if (_hoveredClip != null)
            {
                _hoverFrameIndex++;
                if (_hoverFrameIndex > 10) _hoverFrameIndex = 1;
                UpdateHoverFrame();
            }
        }

        private void UpdateHoverFrame()
        {
            if (_hoveredClip == null) return;
            var clip = _hoveredClip;

            string cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "ThumbCache");
            string hash = GetCacheFolderName(clip);
            string clipCacheDir = Path.Combine(cachePath, hash);

            int attemptIndex = _hoverFrameIndex;
            string framePath = Path.Combine(clipCacheDir, $"frame_{attemptIndex}.jpg");
            if (!File.Exists(framePath))
            {
                for(int k = attemptIndex - 1; k >= 1; k--)
                {
                    string altPath = Path.Combine(clipCacheDir, $"frame_{k}.jpg");
                    if (File.Exists(altPath)) { framePath = altPath; attemptIndex = k; break; }
                }
            }

            if (File.Exists(framePath))
            {
                if (clip.HoverFrames == null) clip.HoverFrames = new ImageSource[11];
                if (clip.HoverFrames[attemptIndex] == null)
                {
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(framePath);
                        bmp.EndInit();
                        bmp.Freeze();
                        clip.HoverFrames[attemptIndex] = bmp;
                    }
                    catch { return; }
                }
                clip.Thumbnail = clip.HoverFrames[attemptIndex];
            }
        }

        private void Clip_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is VideoClip clip)
            {
                if (_hoveredClip == clip)
                {
                    _hoveredClip = null;
                    if (_hoverTimer != null) _hoverTimer.Stop();
                }
                if (clip.DefaultThumbnail != null)
                {
                    clip.Thumbnail = clip.DefaultThumbnail;
                }
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
            SaveSettings();

            if (_autoDeleteEnabled)
            {
                RunAutoDelete();
            }

            if (_recordEnabled && _isLoaded)
            {
                StartBackgroundRecorder();
            }

            SettingsOverlay.Visibility = Visibility.Collapsed;
            if (PlayerOverlay.Visibility == Visibility.Visible)
                VideoPlayer.Visibility = Visibility.Visible;
        }

        private void TargetSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtTargetSizeValue != null)
            {
                _targetSizeMb = (int)e.NewValue;
                TxtTargetSizeValue.Text = $"{_targetSizeMb} MB";
            }
        }

        private void RenderEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RenderEngineComboBox != null)
            {
                _renderEngine = RenderEngineComboBox.SelectedIndex == 1 ? "GPU" : "CPU";
            }
        }

        private void FramerateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FramerateComboBox != null)
            {
                _captureFramerate = FramerateComboBox.SelectedIndex == 1 ? "30" : "60";
            }
        }

        private void LoadAudioDevices()
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var outDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                var inDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

                var outList = new List<AudioDeviceItem> { new AudioDeviceItem { Id = "", Name = GetString("TxtNone") } };
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    outList.Insert(1, new AudioDeviceItem { Id = "Default", Name = $"{GetString("TxtDefault")} ({enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName})" });
                outList.AddRange(outDevices.Select(d => new AudioDeviceItem { Id = d.ID, Name = d.FriendlyName }));
                if (AudioOutputComboBox != null)
                {
                    AudioOutputComboBox.ItemsSource = outList;
                    var foundOut = outList.FirstOrDefault(x => x.Id == _audioOutputId);
                    AudioOutputComboBox.SelectedItem = foundOut ?? outList.FirstOrDefault(x => x.Id == "Default") ?? outList[0];
                }

                var inList = new List<AudioDeviceItem> { new AudioDeviceItem { Id = "", Name = GetString("TxtNone") } };
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
                    inList.Insert(1, new AudioDeviceItem { Id = "Default", Name = $"{GetString("TxtDefault")} ({enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).FriendlyName})" });
                inList.AddRange(inDevices.Select(d => new AudioDeviceItem { Id = d.ID, Name = d.FriendlyName }));
                if (AudioInputComboBox != null)
                {
                    AudioInputComboBox.ItemsSource = inList;
                    var foundIn = inList.FirstOrDefault(x => x.Id == _audioInputId);
                    AudioInputComboBox.SelectedItem = foundIn ?? inList.FirstOrDefault(x => x.Id == "Default") ?? inList[0];
                }
            }
            catch { }
        }

        private void AudioOutputComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AudioOutputComboBox.SelectedItem is AudioDeviceItem item)
            {
                _audioOutputId = item.Id;
            }
        }

        private async void LoadCameraDevices()
        {
            try
            {
                var outList = new List<CameraDeviceItem> { new CameraDeviceItem { Id = "", Name = GetString("TxtNone") } };

                await DownloadFFmpegIfNeeded();
                string ffmpegPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipless", "ffmpeg", "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                {
                    var p = new System.Diagnostics.Process();
                    p.StartInfo.FileName = ffmpegPath;
                    p.StartInfo.Arguments = "-hide_banner -list_devices true -f dshow -i dummy";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;

                    p.Start();
                    string output = await p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();

                    bool inVideo = true; // some ffmpeg versions don't print "DirectShow video devices"
                    foreach(var line in output.Split('\n'))
                    {
                        if (line.Contains("DirectShow video devices")) { inVideo = true; continue; }
                        if (line.Contains("DirectShow audio devices")) { inVideo = false; break; }

                        if ((inVideo || line.Contains("(video)")) && line.Contains("\"") && !line.Contains("Alternative name") && !line.Contains("(audio)"))
                        {
                            var parts = line.Split('"');
                            if (parts.Length >= 3)
                            {
                                outList.Add(new CameraDeviceItem { Id = parts[1], Name = parts[1] });
                            }
                        }
                    }
                }

                Dispatcher.BeginInvoke(() => {
                    if (CameraComboBox != null)
                    {
                        CameraComboBox.ItemsSource = outList;
                        var found = outList.FirstOrDefault(x => x.Id == _cameraId);
                        CameraComboBox.SelectedItem = found ?? outList[0];
                    }
                    if (ChkCameraOverlay != null)
                    {
                        ChkCameraOverlay.IsChecked = _cameraOverlayEnabled;
                        ChkCameraOverlay.IsEnabled = outList.Count > 1;
                    }
                });
            }
            catch { }
        }

        private void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CameraComboBox.SelectedItem is CameraDeviceItem item)
            {
                _cameraId = item.Id;
                if (_isLoaded && _cameraOverlayEnabled && ChkRecordEnabled?.IsChecked == true)
                {
                    StartBackgroundRecorder();
                }
            }
        }

        private void ChkCameraOverlay_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkCameraOverlay != null)
            {
                _cameraOverlayEnabled = ChkCameraOverlay.IsChecked == true;
            }
            if (_isLoaded && ChkRecordEnabled?.IsChecked == true)
            {
                StartBackgroundRecorder();
            }
        }

        private void AudioInputComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AudioInputComboBox.SelectedItem is AudioDeviceItem item)
            {
                _audioInputId = item.Id;
            }
        }

        private void ResolutionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResolutionComboBox != null)
            {
                _captureResolution = ResolutionComboBox.SelectedIndex == 1 ? "1080p" : (ResolutionComboBox.SelectedIndex == 2 ? "720p" : "Native");
            }
        }

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkAutoStart != null && _isLoaded)
            {
                _autoStartEnabled = ChkAutoStart.IsChecked == true;
                try 
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (key != null)
                        {
                            if (_autoStartEnabled)
                            {
                                key.SetValue("Clipless", $"\"{Environment.ProcessPath}\" -autostart");
                            }
                            else
                            {
                                key.DeleteValue("Clipless", false);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void ChkAutoDelete_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkAutoDelete != null)
            {
                _autoDeleteEnabled = ChkAutoDelete.IsChecked == true;
            }
        }

        private void AutoDeleteSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtAutoDeleteValue != null)
            {
                _autoDeleteWeeks = (int)e.NewValue;
                TxtAutoDeleteValue.Text = $"{_autoDeleteWeeks} {GetString("TxtWeeks")}";
            }
        }

        private void ChkRecordEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkRecordEnabled != null)
            {
                _recordEnabled = ChkRecordEnabled.IsChecked == true;

                if (BufferMinutesSlider != null) BufferMinutesSlider.IsEnabled = !_recordEnabled;
                if (RenderEngineComboBox != null) RenderEngineComboBox.IsEnabled = !_recordEnabled;
                if (ResolutionComboBox != null) ResolutionComboBox.IsEnabled = !_recordEnabled;
                if (FramerateComboBox != null) FramerateComboBox.IsEnabled = !_recordEnabled;
                if (AudioOutputComboBox != null) AudioOutputComboBox.IsEnabled = !_recordEnabled;
                if (AudioInputComboBox != null) AudioInputComboBox.IsEnabled = !_recordEnabled;

                if (_isLoaded)
                {
                    if (_recordEnabled) StartBackgroundRecorder();
                    else StopBackgroundRecorder();
                }
            }
        }

        private async void BufferMinutesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBufferMinutesValue != null)
            {
                _bufferMinutes = (int)e.NewValue;
                TxtBufferMinutesValue.Text = $"{_bufferMinutes} {GetString("TxtMin")}";
            }
        }

        private void RunAutoDelete()
        {
            if (!_autoDeleteEnabled || string.IsNullOrEmpty(_basePath) || !Directory.Exists(_basePath))
                return;

            Task.Run(() => {
                try
                {
                    DateTime cutoff = DateTime.Now.AddDays(-(_autoDeleteWeeks * 7));
                    foreach (string path in Directory.GetDirectories(_basePath))
                    {
                        foreach (string file in Directory.GetFiles(path, "*.mp4"))
                        {
                            try
                            {
                                if (File.GetCreationTime(file) < cutoff && !_favorites.Contains(file))
                                {
                                    File.Delete(file);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            });
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
        public string FileSize { get; set; }
        public long ActualFileSize { get; set; }
        public DateTime ActualCreationTime { get; set; }
        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged();
                }
            }
        }
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
        public ImageSource DefaultThumbnail { get; set; }
        public ImageSource[] HoverFrames { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public class ClipTag : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Color { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public class CameraDeviceItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class AudioDeviceItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class MonitorItem
    {
        public int Index { get; set; }
        public string Name { get; set; }
    }
}