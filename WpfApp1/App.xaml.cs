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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "CliplessAppMutex_3b9a5e8f-7c1d-4f2a-8b1e-9a2b5h7f8c9d";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // App is already running, exit this instance
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }
}
