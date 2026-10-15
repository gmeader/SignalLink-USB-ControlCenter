using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using SignaLinkControlCenter.Models;

namespace SignaLinkControlCenter.Services;

public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private WasapiCapture? capture;
    private WasapiOut? output;
    private BufferedWaveProvider? buffer;
    private MediaFoundationResampler? resampler;
    private WasapiLoopbackCapture? outputMonitor;
    private TestToneWaveProvider? testToneProvider;

    public event Action<float>? InputLevelChanged;
    public event Action<float>? OutputLevelChanged;
    public event Action<float, float>? OutputStereoLevelChanged;
    public event Action<string>? MonitorError;

    public void OpenLegacySoundProperties()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "control.exe",
            Arguments = "mmsys.cpl",
            UseShellExecute = true
        });
    }

    public IReadOnlyList<AudioEndpointInfo> GetInputs() => GetEndpoints(DataFlow.Capture);
    public IReadOnlyList<AudioEndpointInfo> GetOutputs() => GetEndpoints(DataFlow.Render);

    public MMDevice? FindSignaLinkInput()
        => FindSignaLink(DataFlow.Capture);

    public MMDevice? FindSignaLinkOutput()
        => FindSignaLink(DataFlow.Render);

    public bool TrySetInputVolume(float level, out string message) => TrySetVolume(FindSignaLinkInput(), level, out message);

    public bool TrySetOutputVolume(float level, out string message) => TrySetVolume(FindSignaLinkOutput(), level, out message);

    private static bool TrySetVolume(MMDevice? device, float level, out string message)
    {
        if (device == null)
        {
            message = "SignaLink audio device was not found.";
            return false;
        }

        try
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            message = "Level set.";
            return true;
        }
        catch (Exception ex)
        {
            message = "Could not set audio level: " + ex.Message;
            return false;
        }
    }

    public float GetInputVolume() => GetVolume(FindSignaLinkInput());
    public float GetOutputVolume() => GetVolume(FindSignaLinkOutput());

    private static float GetVolume(MMDevice? device)
    {
        try { return device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
        catch { return 0f; }
    }

    public string? SelectedInputId { get; set; }
    public string? SelectedOutputId { get; set; }

    private MMDevice? FindSignaLink(DataFlow flow)
    {
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        string? selected = flow == DataFlow.Capture ? SelectedInputId : SelectedOutputId;
        return selected != null ? devices.FirstOrDefault(d => d.ID == selected) : null;
    }

    private IReadOnlyList<AudioEndpointInfo> GetEndpoints(DataFlow flow)
    {
        var defaults = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)?.ID;
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioEndpointInfo
            {
                Name = d.FriendlyName,
                Id = d.ID,
                IsInput = flow == DataFlow.Capture,
                IsSignaLinkCandidate = IsSignaLink(d),
                IsDefault = d.ID == defaults,
                RoleText = d.ID == defaults ? "Default" : "",
                Volume = d.AudioEndpointVolume.MasterVolumeLevelScalar,
                Muted = d.AudioEndpointVolume.Mute,
                EnhancementsDisabled = ReadEnhancementsDisabled(d),
                AgcKnown = false,
                AgcDisabled = false,
                SampleFormat = TryGetFormat(d)
            }).ToList();
    }

    private static string TryGetFormat(MMDevice d)
    {
        try
        {
            var f = d.AudioClient.MixFormat;
            return $"{f.SampleRate:N0} Hz / {f.BitsPerSample} bit / {f.Channels} ch";
        }
        catch { return "Unknown"; }
    }

    private static bool IsSignaLink(MMDevice d)
    {
        var n = d.FriendlyName.ToLowerInvariant();
        return n.Contains("usb audio codec") || n.Contains("signalink");
    }

    private static bool ReadEnhancementsDisabled(MMDevice d)
    {
        return WindowsAudioPropertyService.TryGetSystemEffectsDisabled(d.ID, out var disabled) && disabled;
    }

    public bool TryDisableEnhancements(MMDevice device, out string message)
    {
        return WindowsAudioPropertyService.TrySetSystemEffectsDisabled(device.ID, true, out message);
    }

    public bool TryRenameSignaLinkDevices(out string message)
    {
        message = "Automatic renaming is disabled because USB Audio CODEC is not a unique SignaLink identifier.";
        return false;
    }

    public void StartMonitor(MMDevice input, MMDevice speakerOutput, MMDevice? signalinkOutput)
    {
        StopMonitor();

        // The SignaLink input is 16-bit PCM, mono, 48 kHz on this system.
        // We keep that format all the way through capture and only convert at
        // the Windows audio-engine boundary for the selected speaker device.
        capture = new WasapiCapture(input);
        buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };

        capture.DataAvailable += (_, e) =>
        {
            try
            {
                buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                InputLevelChanged?.Invoke(CalculateRmsPercent(e.Buffer, e.BytesRecorded, capture.WaveFormat));
            }
            catch (Exception ex)
            {
                MonitorError?.Invoke("RX capture error: " + ex.Message);
            }
        };

        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                MonitorError?.Invoke("RX capture stopped: " + e.Exception.Message);
        };

        // Windows speakers may expose a different bit depth/channel layout.
        // Convert explicitly to the speaker endpoint's actual mix format.
        // This avoids relying on Windows 'Listen to this device' and avoids
        // assuming that the SignaLink and speaker formats are identical.
        var speakerFormat = speakerOutput.AudioClient.MixFormat;
        resampler = new MediaFoundationResampler(buffer, speakerFormat)
        {
            ResamplerQuality = 60
        };

        output = new WasapiOut(speakerOutput, AudioClientShareMode.Shared, true, 100);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null)
                MonitorError?.Invoke("Speaker playback stopped: " + e.Exception.Message);
        };

        output.Init(resampler);
        output.Play();

        // Also monitor the actual SignaLink render endpoint. This meter is useful
        // for confirming that a software modem (or another application) is
        // really sending audio to SignaLink Out. It does not generate any audio.
        if (signalinkOutput != null)
        {
            outputMonitor = new WasapiLoopbackCapture(signalinkOutput);
            outputMonitor.DataAvailable += (_, e) =>
            {
                try
                {
                    var levels = CalculateStereoRmsPercent(e.Buffer, e.BytesRecorded, outputMonitor.WaveFormat);
                    OutputLevelChanged?.Invoke(Math.Max(levels.left, levels.right));
                    OutputStereoLevelChanged?.Invoke(levels.left, levels.right);
                }
                catch (Exception ex)
                {
                    MonitorError?.Invoke("TX monitor error: " + ex.Message);
                }
            };
            outputMonitor.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                    MonitorError?.Invoke("TX monitor stopped: " + e.Exception.Message);
            };
            outputMonitor.StartRecording();
        }

        capture.StartRecording();
    }

    private static (float left, float right) CalculateStereoRmsPercent(byte[] data, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0)
            return (0f, 0f);

        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = channels * bytesPerSample;
        if (bytesPerSample <= 0 || frameSize <= 0)
            return (0f, 0f);

        double leftSum = 0, rightSum = 0;
        int frames = bytesRecorded / frameSize;
        int rightSamples = channels > 1 ? frames : 0;

        for (int frame = 0; frame < frames; frame++)
        {
            double left = ReadNormalizedSample(data, frame * frameSize, format);
            leftSum += left * left;

            if (channels > 1)
            {
                double right = ReadNormalizedSample(data, frame * frameSize + bytesPerSample, format);
                rightSum += right * right;
            }
        }

        return (RmsToPercent(leftSum, frames), RmsToPercent(rightSum, rightSamples));
    }

    private static double ReadNormalizedSample(byte[] data, int pos, WaveFormat format)
    {
        if (format.BitsPerSample == 16)
            return BitConverter.ToInt16(data, pos) / 32768.0;

        if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            return Math.Clamp(BitConverter.ToSingle(data, pos), -1.0f, 1.0f);

        if (format.BitsPerSample == 24)
        {
            int value = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16);
            if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
            return value / 8388608.0;
        }

        if (format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible))
            return BitConverter.ToInt32(data, pos) / 2147483648.0;

        return 0.0;
    }

    private static float RmsToPercent(double sum, int samples)
    {
        if (samples <= 0) return 0f;
        double rms = Math.Sqrt(sum / samples);
        double db = rms > 0 ? 20.0 * Math.Log10(rms) : -60.0;
        return (float)Math.Clamp((db + 60.0) / 60.0 * 100.0, 0.0, 100.0);
    }

    private static float CalculateRmsPercent(byte[] data, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0)
            return 0f;

        double sum = 0;
        int samples = 0;
        if (format.BitsPerSample == 16)
        {
            samples = bytesRecorded / 2;
            for (int i = 0; i < samples; i++)
            {
                short sample = BitConverter.ToInt16(data, i * 2);
                double normalized = sample / 32768.0;
                sum += normalized * normalized;
            }
        }
        else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            samples = bytesRecorded / 4;
            for (int i = 0; i < samples; i++)
            {
                double normalized = Math.Clamp(BitConverter.ToSingle(data, i * 4), -1.0f, 1.0f);
                sum += normalized * normalized;
            }
        }
        else
        {
            return 0f;
        }

        double rms = Math.Sqrt(sum / Math.Max(1, samples));
        // Match the useful visual range of a Windows-style level meter:
        // silence is 0%, full-scale is 100%, with -60 dBFS at the bottom.
        double db = rms > 0 ? 20.0 * Math.Log10(rms) : -60.0;
        return (float)Math.Clamp((db + 60.0) / 60.0 * 100.0, 0.0, 100.0);
    }

    public bool IsTestToneRunning => testToneWasapi != null;

    public void StartTestTone(MMDevice signalinkOutput)
    {
        StopTestTone();

        // Audio activates the SignaLink's internal Auto-PTT circuit.
        // Use the exact selected Windows endpoint rather than an ambiguous WaveOut name.
        var format = new WaveFormat(48000, 16, 2);
        testToneProvider = new TestToneWaveProvider(format, 1000.0, 0.20);
        StartOutputMeterIfNeeded(signalinkOutput);
        // WaveOutEvent uses legacy device numbers; use WASAPI shared mode instead.
        testToneWasapi = new WasapiOut(signalinkOutput, AudioClientShareMode.Shared, true, 100);
        testToneWasapi.Init(testToneProvider);
        testToneWasapi.Play();
    }

    private WasapiOut? testToneWasapi;

    public void StopTestTone()
    {
        try { testToneWasapi?.Stop(); } catch { }
        testToneWasapi?.Dispose();
        testToneWasapi = null;
        testToneProvider = null;
    }

    private void StartOutputMeterIfNeeded(MMDevice signalinkOutput)
    {
        if (outputMonitor != null)
            return;

        outputMonitor = new WasapiLoopbackCapture(signalinkOutput);
        outputMonitor.DataAvailable += (_, e) =>
        {
            try
            {
                var levels = CalculateStereoRmsPercent(e.Buffer, e.BytesRecorded, outputMonitor.WaveFormat);
                    OutputLevelChanged?.Invoke(Math.Max(levels.left, levels.right));
                    OutputStereoLevelChanged?.Invoke(levels.left, levels.right);
            }
            catch (Exception ex)
            {
                MonitorError?.Invoke("TX monitor error: " + ex.Message);
            }
        };
        outputMonitor.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                MonitorError?.Invoke("TX monitor stopped: " + e.Exception.Message);
        };
        outputMonitor.StartRecording();
    }

    public void StopMonitor()
    {
        // This method controls INPUT monitoring only. The SignaLink OUT loopback
        // meter is intentionally independent so TX activity remains visible
        // even when the input-monitor buttons are stopped.
        try { capture?.StopRecording(); } catch { }
        capture?.Dispose(); capture = null;
        output?.Stop(); output?.Dispose(); output = null;
        resampler?.Dispose(); resampler = null;
        buffer = null;
        InputLevelChanged?.Invoke(0f);
    }

    public void StopOutputMeter()
    {
        try { outputMonitor?.StopRecording(); } catch { }
        outputMonitor?.Dispose(); outputMonitor = null;
        OutputLevelChanged?.Invoke(0f);
        OutputStereoLevelChanged?.Invoke(0f, 0f);
    }

    public void EnsureOutputMeter(MMDevice signalinkOutput)
    {
        StartOutputMeterIfNeeded(signalinkOutput);
    }

    public void Dispose()
    {
        StopMonitor();
        StopOutputMeter();
        StopTestTone();
        enumerator.Dispose();
    }

    private sealed class TestToneWaveProvider : IWaveProvider
    {
        private readonly WaveFormat format;
        private readonly double frequency;
        private readonly double amplitude;
        private double phase;

        public TestToneWaveProvider(WaveFormat format, double frequency, double amplitude)
        {
            this.format = format;
            this.frequency = frequency;
            this.amplitude = amplitude;
        }

        public WaveFormat WaveFormat => format;

        public int Read(byte[] buffer, int offset, int count)
        {
            int bytesPerSample = format.BitsPerSample / 8;
            if (bytesPerSample <= 0 || format.Channels <= 0) return 0;

            int frameSize = bytesPerSample * format.Channels;
            int frames = count / frameSize;
            double phaseStep = 2.0 * Math.PI * frequency / format.SampleRate;

            for (int frame = 0; frame < frames; frame++)
            {
                double sample = Math.Sin(phase) * amplitude;
                phase += phaseStep;
                if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;

                for (int ch = 0; ch < format.Channels; ch++)
                {
                    int pos = offset + frame * frameSize + ch * bytesPerSample;
                    // Left channel (channel 0) carries the test tone.
                    // Right channel (channel 1) is intentionally silent because
                    // SignaLink Lite can use right-channel audio as its VOX PTT trigger.
                    WriteSample(buffer, pos, format, sample);
                }
            }

            return frames * frameSize;
        }

        private static void WriteSample(byte[] buffer, int pos, WaveFormat format, double sample)
        {
            sample = Math.Clamp(sample, -1.0, 1.0);

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(pos, 4), (float)sample);
                return;
            }

            if ((format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible) && format.BitsPerSample == 16)
            {
                short value = (short)Math.Round(sample * short.MaxValue);
                BitConverter.TryWriteBytes(buffer.AsSpan(pos, 2), value);
                return;
            }

            if ((format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible) && format.BitsPerSample == 24)
            {
                int value = (int)Math.Round(sample * 8388607.0);
                buffer[pos] = (byte)(value & 0xff);
                buffer[pos + 1] = (byte)((value >> 8) & 0xff);
                buffer[pos + 2] = (byte)((value >> 16) & 0xff);
                return;
            }

            if ((format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible) && format.BitsPerSample == 32)
            {
                int value = (int)Math.Round(sample * 2147483647.0);
                BitConverter.TryWriteBytes(buffer.AsSpan(pos, 4), value);
            }
        }
    }

}
