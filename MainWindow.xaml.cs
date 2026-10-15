using System.Windows;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using SignaLinkControlCenter.Services;

namespace SignaLinkControlCenter;

public partial class MainWindow : Window
{
    private readonly AudioService audio = new();
    private MMDevice? rx;
    private MMDevice? tx;
    private bool loadingDevices;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        Closing += (_, _) => { audio.Dispose(); };
        audio.InputLevelChanged += Audio_InputLevelChanged;
        audio.OutputStereoLevelChanged += Audio_OutputStereoLevelChanged;
        audio.MonitorError += Audio_MonitorError;
    }

    private void DevicePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (loadingDevices || !IsLoaded) return;
        audio.StopMonitor();
        audio.StopTestTone();
        audio.StopOutputMeter();
        audio.SelectedInputId = (RxDevicePicker.SelectedItem as MMDevice)?.ID;
        audio.SelectedOutputId = (TxDevicePicker.SelectedItem as MMDevice)?.ID;
        rx = audio.FindSignaLinkInput();
        tx = audio.FindSignaLinkOutput();
        if (tx != null) audio.EnsureOutputMeter(tx);
        RxText.Text = rx?.FriendlyName ?? "Not selected";
        TxText.Text = tx?.FriendlyName ?? "Not selected";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        try
        {
            string[] ports = System.IO.Ports.SerialPort.GetPortNames();
            Array.Sort(ports, StringComparer.OrdinalIgnoreCase);
            ComPortsList.ItemsSource = ports.Length == 0 ? new[] { "No COM ports detected" } : ports;
            var inputs = audio.GetInputs();
            var outputs = audio.GetOutputs();
            InputsList.ItemsSource = inputs.Select(x => $"{(x.IsSignaLinkCandidate ? "● SIGNALINK  " : "    ")}{x.Name}   {x.SampleFormat}   {(x.IsDefault ? "[DEFAULT]" : "")}");
            OutputsList.ItemsSource = outputs.Select(x => $"{(x.IsSignaLinkCandidate ? "● SIGNALINK  " : "    ")}{x.Name}   {x.SampleFormat}   {(x.IsDefault ? "[DEFAULT]" : "")}");

            loadingDevices = true;
            using (var enumerator = new MMDeviceEnumerator())
            {
                var inputDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                var outputDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                RxDevicePicker.ItemsSource = inputDevices;
                TxDevicePicker.ItemsSource = outputDevices;
                RxDevicePicker.SelectedItem = inputDevices.FirstOrDefault(d => d.ID == audio.SelectedInputId)
                    ?? inputDevices.FirstOrDefault(d => d.FriendlyName.Contains("USB Audio CODEC", StringComparison.OrdinalIgnoreCase));
                TxDevicePicker.SelectedItem = outputDevices.FirstOrDefault(d => d.ID == audio.SelectedOutputId)
                    ?? outputDevices.FirstOrDefault(d => d.FriendlyName.Contains("USB Audio CODEC", StringComparison.OrdinalIgnoreCase));
                audio.SelectedInputId = (RxDevicePicker.SelectedItem as MMDevice)?.ID;
                audio.SelectedOutputId = (TxDevicePicker.SelectedItem as MMDevice)?.ID;
            }
            loadingDevices = false;
            rx = audio.FindSignaLinkInput();
            tx = audio.FindSignaLinkOutput();
            if (tx != null)
                audio.EnsureOutputMeter(tx);
            else
                audio.StopOutputMeter();
            bool found = rx != null && tx != null;
            DeviceStatus.Text = found ? "● USB Audio CODEC candidate detected" : "✗ USB Audio CODEC not found";
            DeviceStatus.Foreground = found ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("BadBrush");
            PttDeviceText.Text = "Auto-PTT: audio activated (no HID/COM control)";

            RxText.Text = rx == null ? "Not found" : rx.FriendlyName;
            TxText.Text = tx == null ? "Not found" : tx.FriendlyName;
            EnhText.Text = (rx != null && GetEnhancementState(rx)) && (tx != null && GetEnhancementState(tx)) ? "✓ Disabled" : "⚠ Check / fix";
            EnhText.Foreground = EnhText.Text.StartsWith("✓") ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("WarnBrush");
            AgcText.Text = "Driver control — use Custom tab";
            AgcText.Foreground = (Brush)FindResource("WarnBrush");
            if (rx != null) InputLevelSlider.Value = audio.GetInputVolume() * 100.0;
            if (tx != null) OutputLevelSlider.Value = audio.GetOutputVolume() * 100.0;
            AudioMessage.Text = found ? "SignaLink audio endpoints are ready. Set RX input and TX output levels below. The meter shows live RX audio." : "Connect the SignaLink Lite and press Refresh.";
        }
        catch (Exception ex)
        {
            AudioMessage.Text = "Refresh error: " + ex.Message;
        }
    }

    private bool GetEnhancementState(MMDevice d)
    {
        // Re-read by comparing the endpoint's property through the public service data.
        return audio.GetInputs().Concat(audio.GetOutputs()).FirstOrDefault(x => x.Id == d.ID)?.EnhancementsDisabled ?? false;
    }

    private void FixEnhancements_Click(object sender, RoutedEventArgs e)
    {
        int ok = 0;
        if (rx != null && audio.TryDisableEnhancements(rx, out var rmsg)) ok++;
        if (tx != null && audio.TryDisableEnhancements(tx, out var tmsg)) ok++;
        AudioMessage.Text = ok > 0
            ? "Enhancement/system-effects disable request applied. Refresh to verify. AGC is driver-specific and is handled from the native Custom tab."
            : "Windows did not expose the endpoint effects control. Use the native Windows audio properties to disable enhancements.";
        Refresh();
    }

    private void OpenProperties_Click(object sender, RoutedEventArgs e) => audio.OpenLegacySoundProperties();

    private void FixAll_Click(object sender, RoutedEventArgs e)
    {
        FixEnhancements_Click(sender, e);
        audio.OpenLegacySoundProperties();
    }

    private void RenameSignaLink_Click(object sender, RoutedEventArgs e)
    {
        if (audio.TryRenameSignaLinkDevices(out var message))
        {
            AudioMessage.Text = message + " Refreshing device list…";
        }
        else
        {
            AudioMessage.Text = message;
        }
        Refresh();
    }

    private void InputLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // During InitializeComponent(), WPF can raise ValueChanged before all
        // x:Name fields have been assigned. Do not touch the value label until
        // the XAML object tree is fully constructed.
        if (InputLevelValue != null)
            InputLevelValue.Text = $"{e.NewValue:0}%";

        if (IsLoaded && rx != null && Math.Abs(e.NewValue - audio.GetInputVolume() * 100) > 0.5)
            audio.TrySetInputVolume((float)(e.NewValue / 100.0), out _);
    }

    private void OutputLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // During InitializeComponent(), WPF can raise ValueChanged before all
        // x:Name fields have been assigned. Do not touch the value label until
        // the XAML object tree is fully constructed.
        if (OutputLevelValue != null)
            OutputLevelValue.Text = $"{e.NewValue:0}%";

        if (IsLoaded && tx != null && Math.Abs(e.NewValue - audio.GetOutputVolume() * 100) > 0.5)
            audio.TrySetOutputVolume((float)(e.NewValue / 100.0), out _);
    }

    private void Audio_InputLevelChanged(float level)
    {
        Dispatcher.BeginInvoke(() => InputMeter.Value = level);
    }

    private void Audio_OutputStereoLevelChanged(float left, float right)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (OutputLeftMeter != null) OutputLeftMeter.Value = left;
            if (OutputRightMeter != null) OutputRightMeter.Value = right;
            if (TxAudioStatus != null)
                TxAudioStatus.Text = left < 0.5 && right < 0.5
                    ? "TX audio: idle"
                    : $"TX audio: LEFT {left:0}%  |  RIGHT {right:0}%";

            if (VoxStatus != null)
            {
                VoxStatus.Text = right >= 5.0f
                    ? "● TX audio present — SignaLink Auto-PTT may activate"
                    : "TX audio idle";
                VoxStatus.Foreground = right >= 5.0f
                    ? (Brush)FindResource("BadBrush")
                    : (Brush)FindResource("MutedBrush");
            }
        });
    }

    private void Audio_MonitorError(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MonitorStatus.Text = "Monitor error: " + message;
            MonitorStatus.Foreground = (Brush)FindResource("BadBrush");
        });
    }

    private void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (rx == null) { MonitorStatus.Text = "SignaLink RX not found."; return; }
        try
        {
            using var deviceEnumerator = new MMDeviceEnumerator();
            var defaultOut = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            audio.StartMonitor(rx, defaultOut, tx);
            MonitorStatus.Text = tx != null
                ? $"● Monitoring {rx.FriendlyName} → {defaultOut.FriendlyName}  |  TX meter: {tx.FriendlyName}"
                : $"● Monitoring {rx.FriendlyName} → {defaultOut.FriendlyName}  |  SignaLink Out not found";
            MonitorStatus.Foreground = (Brush)FindResource("GoodBrush");
        }
        catch (Exception ex) { MonitorStatus.Text = "Monitor error: " + ex.Message; }
    }

    private void StopMonitor_Click(object sender, RoutedEventArgs e)
    {
        audio.StopMonitor();
        MonitorStatus.Text = "Stopped";
        MonitorStatus.Foreground = (Brush)FindResource("MutedBrush");
    }

    private void TestTone_Click(object sender, RoutedEventArgs e)
    {
        if (tx == null)
        {
            TestToneStatus.Text = "SignaLink output not found.";
            TestToneStatus.Foreground = (Brush)FindResource("BadBrush");
            return;
        }

        try
        {
            if (audio.IsTestToneRunning)
            {
                audio.StopTestTone();
                TestToneButton.Content = "TEST TONE";
                TestToneStatus.Text = "Test tone off";
                TestToneStatus.Foreground = (Brush)FindResource("MutedBrush");
            }
            else
            {
                audio.StartTestTone(tx);
                TestToneButton.Content = "STOP TONE";
                TestToneStatus.Text = "● 1 kHz test tone active — may key radio";
                TestToneStatus.Foreground = (Brush)FindResource("GoodBrush");
            }
        }
        catch (Exception ex)
        {
            TestToneStatus.Text = "Test tone error: " + ex.Message;
            TestToneStatus.Foreground = (Brush)FindResource("BadBrush");
        }
    }

    private async void TestPtt_Click(object sender, RoutedEventArgs e)
    {
        if (tx == null) { PttStatus.Text = "Select the SignaLink output device first."; return; }
        if (!double.TryParse(PttSeconds.Text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double seconds)) seconds = 3;
        seconds = Math.Clamp(seconds, 0.2, 10);
        bool alreadyPlaying = audio.IsTestToneRunning;
        TestPttButton.IsEnabled = false;
        try
        {
            if (!alreadyPlaying) audio.StartTestTone(tx);
            PttStatus.Text = "Audio transmitting — verify the SignaLink PTT LED";
            PttStatus.Foreground = (Brush)FindResource("WarnBrush");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            PttStatus.Text = "Test audio ended; PTT release depends on hardware DLY knob";
            PttStatus.Foreground = (Brush)FindResource("GoodBrush");
        }
        catch (Exception ex) { PttStatus.Text = "Audio PTT test error: " + ex.Message; }
        finally { if (!alreadyPlaying) audio.StopTestTone(); TestPttButton.IsEnabled = true; }
    }
}
