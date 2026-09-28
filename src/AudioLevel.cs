using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TaskbarLyrics;

/// <summary>
/// Громкость того, что сейчас звучит в колонках/наушниках (WASAPI loopback — стандартный механизм Windows,
/// которым пользуются все визуализаторы). Включается только на время проигрышей.
/// Отдаёт нормализованный «уровень баса» 0…1 с автоподстройкой под громкость трека.
/// </summary>
public sealed class AudioLevel : IDisposable
{
    readonly object _lock = new();
    WasapiLoopbackCapture? _capture;
    float _lowPass, _alpha;
    float _raw, _peak = 0.02f;
    long _lastDataTicks;

    public bool IsRunning { get { lock (_lock) return _capture != null; } }

    public void Start()
    {
        lock (_lock)
        {
            if (_capture != null) return;
            try
            {
                var cap = new WasapiLoopbackCapture();
                _alpha = 1f - MathF.Exp(-2f * MathF.PI * 150f / cap.WaveFormat.SampleRate); // фильтр ~150 Гц: бас
                cap.DataAvailable += OnData;
                cap.RecordingStopped += (_, _) => { lock (_lock) { if (_capture == cap) _capture = null; } cap.Dispose(); };
                cap.StartRecording();
                _capture = cap;
            }
            catch (Exception ex) { App.Log("Audio capture: " + ex.Message); _capture = null; }
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? cap;
        lock (_lock) { cap = _capture; _capture = null; }
        try { cap?.StopRecording(); } catch { }
    }

    /// <summary>Уровень 0…1. Если звука нет (данные не приходят), плавно считается тишиной.</summary>
    public float Level
    {
        get
        {
            var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastDataTicks));
            if (idle > TimeSpan.FromMilliseconds(250)) return 0;
            return Math.Clamp(_raw / Math.Max(_peak, 0.02f), 0f, 1f);
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture cap || e.BytesRecorded == 0) return;
        var fmt = cap.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        double sum = 0;
        int frames = 0;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int frameBytes = 4 * channels;
            for (int i = 0; i + frameBytes <= e.BytesRecorded; i += frameBytes)
            {
                float mono = 0;
                for (int c = 0; c < channels; c++) mono += BitConverter.ToSingle(e.Buffer, i + 4 * c);
                mono /= channels;
                _lowPass += _alpha * (mono - _lowPass);
                sum += _lowPass * _lowPass;
                frames++;
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            int frameBytes = 2 * channels;
            for (int i = 0; i + frameBytes <= e.BytesRecorded; i += frameBytes)
            {
                float mono = 0;
                for (int c = 0; c < channels; c++) mono += BitConverter.ToInt16(e.Buffer, i + 2 * c) / 32768f;
                mono /= channels;
                _lowPass += _alpha * (mono - _lowPass);
                sum += _lowPass * _lowPass;
                frames++;
            }
        }
        if (frames == 0) return;

        var rms = (float)Math.Sqrt(sum / frames);
        _raw = rms;
        _peak = Math.Max(_peak * 0.997f, rms); // автоусиление: тихие треки тоже «дышат»
        Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
    }

    public void Dispose() => Stop();
}
