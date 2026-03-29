using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;
using System.Runtime.InteropServices;
using System;

namespace ClipManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static Mutex _mutex;
        private static EventWaitHandle _bringToFrontEvent;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "CliplessAppMutex_3b9a5e8f-7c1d-4f2a-8b1e-9a2b5h7f8c9d";
            const string eventName = "CliplessActivateEvent_3b9a5e8f-7c1d-4f2a-8b1e-9a2b5h7f8c9d";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // App is already running, exit this instance
                if (EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle ev))
                {
                    ev.Set();
                }
                Environment.Exit(0);
                return;
            }

            _bringToFrontEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            var t = new Thread(() =>
            {
                while (true)
                {
                    if (_bringToFrontEvent.WaitOne())
                    {
                        Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (Current.MainWindow != null)
                            {
                                Current.MainWindow.ShowInTaskbar = true;
                                Current.MainWindow.WindowState = WindowState.Normal;
                                Current.MainWindow.Visibility = Visibility.Visible;
                                Current.MainWindow.Activate();
                            }
                        }));
                    }
                }
            });
            t.IsBackground = true;
            t.Start();

            base.OnStartup(e);
        }
    }
}
