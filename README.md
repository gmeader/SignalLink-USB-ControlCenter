# SignalLink-USB-ControlCenter

A Windows desktop application for configuring, monitoring, and testing the **Tigertronics SignaLink USB** sound card interface used in amateur radio digital communications.

SignaLink USB Control Center provides a single graphical interface for managing Windows audio settings, monitoring receive and transmit audio levels, testing audio output, and verifying audio-activated Push-to-Talk (PTT) operation.

The application is written in **C# using WPF, .NET 8, and NAudio**, and supports Windows 10 and Windows 11.

## Features

### Audio Device Management

- Automatically detects available Windows recording and playback devices.
- Provides dropdown selectors for the SignaLink RX input and TX output devices.
- Displays available recording and playback devices.
- Provides a Refresh button to update device information.
- Opens Windows Sound settings for additional configuration.
- Displays Windows audio device status and configuration information.

### Receive Audio (RX)

- Live receive audio level meter.
- Adjustable Windows recording input level.
- Routes received audio from the SignaLink to the computer's speakers.
- Start and stop input audio monitoring independently.

These features help verify that received radio audio is reaching the computer at an appropriate level.

### Transmit Audio (TX)

- Live stereo transmit audio level meters.
- Separate left and right channel level displays.
- Adjustable Windows playback output level.
- Real-time monitoring of audio sent to the SignaLink.

The transmit meters help identify excessive or insufficient audio levels before transmission.

### Test Tone

Includes a built-in **1 kHz test tone generator** for checking the SignaLink transmit audio path.

The test tone can be used to:

- Verify that audio reaches the SignaLink.
- Check Windows playback device selection.
- Adjust the SignaLink TX level.
- Test audio-activated PTT operation.
- Verify that audio is transmitted by the connected radio.

**Warning:** Generating transmit audio may activate the SignaLink's PTT circuit and key the radio. Use a dummy load or an authorized frequency when testing.

### PTT Testing

The SignaLink USB uses an internal audio-activated PTT circuit rather than a serial-port or USB HID PTT interface.

The application provides a timed PTT test that sends audio to activate the SignaLink's internal PTT circuitry.

PTT test features include:

- User-selectable test duration.
- Audio-triggered PTT activation.
- No COM port required.
- Independent test controls for checking transmit operation.

The SignaLink's physical **TX**, **RX**, and **DLY** controls remain hardware-operated.

The application cannot directly read the PTT relay state or determine whether the radio has actually keyed.

### COM Port Display

Although the SignaLink USB does not require a serial port, the application displays available Windows COM ports. This is useful when working with radios that have separate CAT control or programming interfaces. COM ports are displayed for informational purposes only. They are not used for SignaLink audio or PTT control.

## Hardware Requirements

- Tigertronics SignaLink USB
- Compatible amateur radio transceiver
- Appropriate SignaLink radio interface cable and jumper configuration
- Available USB port
- Windows 10 or Windows 11 (64-bit)

The SignaLink USB contains its own USB sound card and normally uses standard Windows USB audio drivers.

No special SignaLink serial-port driver is required.

## How It Works

### Receive Audio Path

```text
Radio Receiver
      |
      v
SignaLink USB
      |
      v
Windows Recording Device
      |
      +----> RX Audio Level Meter
      |
      +----> Optional Speaker Monitor
```

### Transmit Audio Path

```text
Windows Application
      |
      v
Windows Playback Device
      |
      +----> TX Audio Level Meters
      |
      v
SignaLink USB
      |
      +----> Audio-Activated PTT
      |
      v
Radio Transmitter
```


## Installation

### Prebuilt Windows Release

1. Download the latest Windows release ZIP from the repository's **Releases** page, if available.
2. Extract the ZIP into a folder, such as `C:\SignaLinkControlCenter`.
3. Connect the SignaLink USB to the computer.
4. Run `SignaLinkControlCenter.exe`.
5. Select the appropriate SignaLink recording and playback devices.
6. Verify the RX and TX audio levels.

The self-contained Windows release includes the required .NET runtime, so a separate .NET installation is not necessary.

Keep all files from the published release together unless using a verified single-file build.

## Building from Source

### Prerequisites

- Windows 10 or Windows 11
- .NET 8 SDK with Windows desktop development support
- Visual Studio (optional)

### Build

Open PowerShell in the project directory containing `SignaLinkControlCenter.csproj`.

```powershell
dotnet restore
dotnet build -c Release
```

### Run

```powershell
dotnet run -c Release
```

### Publish for Distribution

To create a self-contained Windows x64 application:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

The published application will be located in:

```text
bin\Release\net8.0-windows\win-x64\publish\
```

Distribute the entire contents of the `publish` directory.

### Optional Single-File EXE

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Check the resulting publish directory for any additional required files before distributing.

## Technology

| Component | Technology |
|---|---|
| Language | C# |
| User Interface | WPF |
| Framework | .NET 8 |
| Audio | NAudio |
| Platform | Windows 10/11 |
| Audio Interfaces | Windows Core Audio / WASAPI |
| PTT | SignaLink hardware audio-activated PTT |

## Operating Notes

**Windows audio levels and SignaLink hardware controls are separate.** Adjusting the application playback or recording level does not physically move the SignaLink's TX or RX knobs.

The SignaLink DLY control determines how long its PTT circuit remains active after transmit audio stops. This delay is controlled by hardware, not the application.

For reliable operation, select the correct USB audio endpoints and confirm the Windows audio levels before testing transmission.

When using multiple USB sound devices, verify the selected device carefully to avoid sending test audio to the wrong interface.

## Project Status

SignaLink USB Control Center is an experimental amateur radio utility.

The application is intended to simplify audio configuration and troubleshooting for SignaLink USB installations.

Operation with every SignaLink hardware revision, radio model, and Windows audio configuration has not been verified.

## Disclaimer

This project is an independent utility and is not affiliated with or endorsed by Tigertronics.

Users are responsible for proper radio configuration, legal transmission, and compliance with applicable amateur radio regulations.

## License

Add the appropriate open-source license for this repository. See the `LICENSE` file when one is provided.
