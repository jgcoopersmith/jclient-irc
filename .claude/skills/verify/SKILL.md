# jclient irc Verifier

## Build
```
cd D:\Claude\jclient-irc
dotnet build -c Debug
```

## Launch
```powershell
Start-Process "bin\Debug\net10.0-windows\win-x64\jclient.exe"
```
Process name is `jclient`. If "Connect on startup" is enabled in the user's
settings (%AppData%\IRCClient\settings.json), it auto-connects to the last-used
saved connection; joins take ~15-20 s.

## Drive via UI Automation
Use `System.Windows.Automation` — find the top-level window by name
"jclient irc for Windows".
- Buttons: FindFirst by NameProperty ("Connect", "New", "Edit", "Delete", "Disconnect") + InvokePattern
- Input box: the single ControlType.Edit element (its accessible Name is NOT
  empty — it inherits the window header label text, so match by control type,
  not name). ValuePattern to set text; for Enter, physically click into the box
  first (SetFocus alone is unreliable when the window isn't foreground), then
  SendKeys.SendWait("{ENTER}").
- Tabs: ControlType.TabItem elements expose per-tab bounding rects for clicks.
- Context menus (tab strip / split panes) are only shown from MouseDown, so use
  real mouse input (see scratchpad uiclick helper pattern: SetCursorPos +
  mouse_event P/Invoke), and do right-click + menu-item click in ONE script —
  menus dismiss between tool calls.

## Screenshot
```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bmp = New-Object System.Drawing.Bitmap ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width), ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([System.Drawing.Point]::Empty, [System.Drawing.Point]::Empty, $bmp.Size)
$bmp.Save("C:\path\shot.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

## Key flows to drive
- Select the saved connection in the library list, click Connect → status bar
  "Connected to … as <nick>"; saved channels auto-join as background tabs
  (orange = unread; focus must NOT switch automatically)
- Window headers read "<name>     <nick> @ <server>     <topic>"
- `/topic #chan text` (needs +o; the op-bot on irc.prison.net ops after ~1 min)
  → header updates with topic
- `/quit` forces a server-side drop → with "Reconnect on disconnect" enabled,
  reconnects and rejoins in ~5-20 s
- File > Disconnect → status "Disconnected"; File > Exit → process ends
- Ctrl+click tabs → right-click → Stack Vertical/Horizontal → split view;
  right-click pane → Unstack

## Gotchas
- The user's saved connection targets irc.prison.net (EFNET); their own mIRC
  session on the same desktop independently shows joins/quits/topics — useful
  as a second witness.
- After committing, ALWAYS rebuild/republish: the pre-commit hook bumps
  <Version> at commit time, so pre-commit binaries carry the old version.
- PING/PONG handled automatically; no manual action needed.
