# IRCClient Verifier

## Build
```
cd code\IRCClient
dotnet build -c Debug
```

## Launch
```powershell
Start-Process "bin\Debug\net10.0-windows\IRCClient.exe"
```

## Drive via UI Automation
Use `System.Windows.Automation` — find window by name "IRC Client (RFC 1459 / RFC 2812)".
- Buttons: FindFirst by NameProperty ("Connect", "Send", "Disconnect") + InvokePattern
- Input box: the single Edit control with Name="" (not the server/nick fields) + ValuePattern to set text, then SendKeys.SendWait("{ENTER}")

## Screenshot
```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bmp = New-Object System.Drawing.Bitmap ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width), ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([System.Drawing.Point]::Empty, [System.Drawing.Point]::Empty, $bmp.Size)
$bmp.Save("C:\path\shot.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

## Key flows to drive
- Connect to irc.libera.chat:6667 → status bar should update to "Connected to … as <nick>"
- Server NOTICE messages appear in (server) tab in gold
- `/raw WHOIS <nick>` → 451 numeric reply appears in (server) tab
- Disconnect → status bar reverts to "Disconnected"

## Gotchas
- Libera Chat drops unregistered nicks within ~30 s; short-lived connection is expected
- PING/PONG handled automatically; no manual action needed
