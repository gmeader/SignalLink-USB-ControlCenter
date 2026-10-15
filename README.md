# SignaLink USB Control Center

Windows 10/11 WPF .NET 8 dashboard for Tigertronics SignaLink USB. Adapted from DigiRig Control Center.

- Windows USB Audio CODEC RX/TX endpoints, levels, and audio meters
- RX monitor to PC speakers
- 1 kHz transmit test tone and timed audio Auto-PTT test
- Windows audio properties and enhancement controls

**Important:** SignaLink Auto-PTT is triggered by audio, not HID GPIO or a COM port. TEST TONE and PTT TEST can transmit RF. The hardware TX/RX/DLY knobs cannot be read or adjusted by software. TX meters measure PC audio, not RF power or actual PTT state. The DLY knob controls how long PTT remains asserted after audio stops.

SignaLink typically appears as `USB Audio CODEC`; other devices can share this name. Confirm the selected endpoints before transmitting.

Build: `dotnet build -c Release`
Run: `dotnet run -c Release`
Publish: `dotnet publish -c Release -r win-x64 --self-contained true`

Requires a suitable antenna/dummy load and radio wiring. **Not yet compiled or hardware tested.**
