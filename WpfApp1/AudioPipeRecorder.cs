using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace ClipManager
{
    public class AudioPipeRecorder : IDisposable
    {
        public string PipeName { get; private set; }
        private WasapiCapture _capture;
        private WasapiOut _silenceOut;
        private NamedPipeServerStream _pipe;
        private volatile bool _isRecording;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public string FFmpegFormat { get; private set; }

        public AudioPipeRecorder(MMDevice device, bool isLoopback)
        {
            PipeName = "clipless_audio_" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);

            if (isLoopback)
            {
                _capture = new WasapiLoopbackCapture(device);
                try
                {
                    _silenceOut = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
                    _silenceOut.Init(new SilenceProvider(_capture.WaveFormat));
                    _silenceOut.Play();
                }
                catch { } // Might fail if device is exclusive or unsupported, but loopback usually works
            }
            else
            {
                _capture = new WasapiCapture(device);
            }

            var wf = _capture.WaveFormat;
            SampleRate = wf.SampleRate;
            Channels = wf.Channels;

            if (wf.Encoding == WaveFormatEncoding.IeeeFloat)
                FFmpegFormat = "f32le";
            else if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 16)
                FFmpegFormat = "s16le";
            else if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 32)
                FFmpegFormat = "s32le";
            else
                FFmpegFormat = "f32le"; // fallback assumption

            _capture.DataAvailable += OnDataAvailable;
        }

        public Task StartAsync()
        {
            _isRecording = true;
            try { _capture.StartRecording(); } catch { }

            // Wait for ffmpeg to connect to the pipe in background without blocking capture start
            return Task.Run(async () => {
                try {
                    await _pipe.WaitForConnectionAsync().ConfigureAwait(false);
                } catch { }
            });
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (!_isRecording || !_pipe.IsConnected) return;

            try
            {
                _pipe.Write(e.Buffer, 0, e.BytesRecorded);
                _pipe.Flush();
            }
            catch { }
        }

        public void Dispose()
        {
            _isRecording = false;
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            try { _silenceOut?.Stop(); } catch { }
            try { _silenceOut?.Dispose(); } catch { }
            if (_pipe != null)
            {
                try { _pipe.Disconnect(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }
    }
}
