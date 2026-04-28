param(
    [int]$Fps = 10,
    [int]$MaxSeconds = 100,
    [string]$TargetBossLevel = "",
    [switch]$SteamParityProfile,
    [switch]$UseLegacyClientLoadoutIds,
    [int]$LanLatencyMs = 0,
    [int]$LanJitterMs = 0,
    [double]$LanUnreliableDropPercent = 0,
    [string]$HostRoot = "",
    [string]$ClientRoot = "",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($HostRoot)) {
    $HostRoot = $env:CUPHEAD_LAN_HOST_ROOT
}
if ([string]::IsNullOrWhiteSpace($ClientRoot)) {
    $ClientRoot = $env:CUPHEAD_LAN_CLIENT_ROOT
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo "visual-test\continuous"
}
if ([string]::IsNullOrWhiteSpace($HostRoot) -or [string]::IsNullOrWhiteSpace($ClientRoot)) {
    throw "Provide -HostRoot and -ClientRoot, or set CUPHEAD_LAN_HOST_ROOT and CUPHEAD_LAN_CLIENT_ROOT."
}

$hostRoot = (Resolve-Path $HostRoot).Path
$clientRoot = (Resolve-Path $ClientRoot).Path
if ($SteamParityProfile) {
    $UseLegacyClientLoadoutIds = $true
}

function Resolve-OptionalToolPath([string]$commandName, [string[]]$candidatePaths) {
    $command = Get-Command $commandName -ErrorAction SilentlyContinue
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source)) {
        return $command.Source
    }

    foreach ($path in $candidatePaths) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            return $path
        }
    }

    return $null
}

$ffmpeg = Resolve-OptionalToolPath "ffmpeg" @(
    (Join-Path $repo "tools\ffmpeg\ffmpeg.exe"),
    (Join-Path $env:USERPROFILE "Tools\ffmpeg\bin\ffmpeg.exe")
)
if (!$ffmpeg) {
    throw "ffmpeg.exe is missing. Install ffmpeg or place it in tools\ffmpeg."
}

function Reset-TestSaves {
    $template = Join-Path $hostRoot "BepInEx\CupHeads\TutorialSaves\cuphead_player_data_v1_slot_1.sav"
    foreach ($root in @($hostRoot, $clientRoot)) {
        $fresh = Join-Path $root "BepInEx\CupHeads\FreshSyncSaves"
        New-Item -ItemType Directory -Path $fresh -Force | Out-Null
        $data = Get-Content -LiteralPath $template -Raw | ConvertFrom-Json
        $data._isTutorialCompleted = $true
        $data._isFlyingTutorialCompleted = $true

        $data.loadouts.playerOne.primaryWeapon = 1456773641
        $data.loadouts.playerOne.secondaryWeapon = 2147483647
        $data.loadouts.playerOne.super = 2147483647
        $data.loadouts.playerOne.charm = 2147483647
        $data.loadouts.playerTwo.primaryWeapon = 1456773641
        $data.loadouts.playerTwo.secondaryWeapon = 2147483647
        $data.loadouts.playerTwo.super = 2147483647
        $data.loadouts.playerTwo.charm = 2147483647
        if ($UseLegacyClientLoadoutIds -and $root -ieq $clientRoot) {
            $data.loadouts.playerTwo.primaryWeapon = 9
            $data.loadouts.playerTwo.secondaryWeapon = 255
            $data.loadouts.playerTwo.super = 255
            $data.loadouts.playerTwo.charm = 255
        }

        $data.mapDataManager.currentMap = 3
        $data.mapDataManager.mapData = @([pscustomobject]@{
            mapId = 3
            sessionStarted = $true
            hasVisitedDieHouse = $false
            hasKingDiceDisappeared = $false
            playerOnePosition = [pscustomobject]@{ x = 0.30; y = 2.28; z = 0.0 }
            playerTwoPosition = [pscustomobject]@{ x = 0.75; y = 2.24; z = 0.0 }
        })

        foreach ($level in $data.levelDataManager.levelObjects) {
            $level.completed = $false
            $level.completedAsChaliceP1 = $false
            $level.completedAsChaliceP2 = $false
            $level.played = $false
            $level.grade = 0
            $level.difficultyBeaten = 0
            $level.curseCharmP1 = $false
            $level.curseCharmP2 = $false
            $level.bgmPlayListCurrent = 0
        }
        $data.statictics.playerOne.numDeaths = 0
        $data.statictics.playerOne.numParriesInRow = 0
        $data.statictics.playerTwo.numDeaths = 0
        $data.statictics.playerTwo.numParriesInRow = 0

        $json = $data | ConvertTo-Json -Depth 100 -Compress
        [System.IO.File]::WriteAllText((Join-Path $fresh "cuphead_player_data_v1_slot_0.sav"), $json, (New-Object System.Text.UTF8Encoding($false)))
        Copy-Item -LiteralPath $template -Destination (Join-Path $fresh "cuphead_player_data_v1_slot_1.sav") -Force
        Copy-Item -LiteralPath $template -Destination (Join-Path $fresh "cuphead_player_data_v1_slot_2.sav") -Force
        Remove-Item -LiteralPath (Join-Path $root "BepInEx\LogOutput.log") -Force -ErrorAction SilentlyContinue
    }
}

function Set-BepInExConfigValue([string]$path, [string]$section, [string]$key, [string]$value) {
    $lines = New-Object 'System.Collections.Generic.List[string]'
    if (Test-Path -LiteralPath $path) {
        foreach ($line in [System.IO.File]::ReadAllLines($path)) {
            $lines.Add($line)
        }
    }

    $sectionHeader = "[$section]"
    $sectionIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -ieq $sectionHeader) {
            $sectionIndex = $i
            break
        }
    }

    if ($sectionIndex -lt 0) {
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Trim().Length -gt 0) {
            $lines.Add("")
        }
        $sectionIndex = $lines.Count
        $lines.Add($sectionHeader)
    }

    $insertAt = $lines.Count
    for ($i = $sectionIndex + 1; $i -lt $lines.Count; $i++) {
        $trimmed = $lines[$i].Trim()
        if ($trimmed.StartsWith("[") -and $trimmed.EndsWith("]")) {
            $insertAt = $i
            break
        }

        if ($trimmed -match ("^" + [regex]::Escape($key) + "\s*=")) {
            $lines[$i] = "$key = $value"
            [System.IO.File]::WriteAllLines($path, $lines.ToArray(), [System.Text.UTF8Encoding]::new($false))
            return
        }
    }

    if ($insertAt -gt $sectionIndex + 1 -and $lines[$insertAt - 1].Trim().Length -gt 0) {
        $lines.Insert($insertAt, "")
        $insertAt++
    }
    $lines.Insert($insertAt, "$key = $value")
    [System.IO.File]::WriteAllLines($path, $lines.ToArray(), [System.Text.UTF8Encoding]::new($false))
}

function Configure-TestCopies {
    $hostConfig = Join-Path $hostRoot "BepInEx\config\com.cupheadonline.mod.cfg"
    $clientConfig = Join-Path $clientRoot "BepInEx\config\com.cupheadonline.mod.cfg"
    $hostSavePath = Join-Path $hostRoot "BepInEx\CupHeads\FreshSyncSaves"
    $clientSavePath = Join-Path $clientRoot "BepInEx\CupHeads\FreshSyncSaves"

    Set-BepInExConfigValue $hostConfig "Networking" "TransportMode" "LanHost"
    Set-BepInExConfigValue $clientConfig "Networking" "TransportMode" "LanClient"
    Set-BepInExConfigValue $hostConfig "Networking" "LanHostAddress" "127.0.0.1"
    Set-BepInExConfigValue $clientConfig "Networking" "LanHostAddress" "127.0.0.1"
    Set-BepInExConfigValue $hostConfig "Networking" "AutoStartLanTransport" "true"
    Set-BepInExConfigValue $clientConfig "Networking" "AutoStartLanTransport" "true"

    foreach ($config in @($hostConfig, $clientConfig)) {
        Set-BepInExConfigValue $config "Debug" "AutoRunLanSteamE2E" "true"
        Set-BepInExConfigValue $config "Debug" "AutoRunLanSteamE2ETarget" $TargetBossLevel
        Set-BepInExConfigValue $config "Debug" "UseSeparateSavePath" "true"
        Set-BepInExConfigValue $config "Debug" "LanArtificialLatencyMs" $LanLatencyMs
        Set-BepInExConfigValue $config "Debug" "LanArtificialJitterMs" $LanJitterMs
        Set-BepInExConfigValue $config "Debug" "LanUnreliableDropPercent" $LanUnreliableDropPercent
        Set-BepInExConfigValue $config "StartupSplash" "EnableStartupSplash" "false"
    }

    Set-BepInExConfigValue $hostConfig "Debug" "SeparateSavePath" $hostSavePath
    Set-BepInExConfigValue $clientConfig "Debug" "SeparateSavePath" $clientSavePath
}

Add-Type @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CupheadContinuousCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    public class CaptureInfo {
        public string Path;
        public int Width;
        public int Height;
        public int X;
        public int Y;
    }

    public class PairBitmaps {
        public Bitmap Host;
        public Bitmap Client;
    }

    public class Metrics {
        public int Frame;
        public double TimeSeconds;
        public int Width;
        public int Height;
        public int ExactMismatchPixels;
        public double ExactMismatchPct;
        public int PlayfieldExactMismatchPixels;
        public double PlayfieldExactMismatchPct;
        public double MeanAbsDiffPerChannel;
        public double PlayfieldMeanAbsDiffPerChannel;
        public double PixelsOverThresholdPct;
        public double TopHudMeanAbsDiffPerChannel;
        public double TopHudPixelsOverThresholdPct;
        public int MaxSinglePixelDelta;
        public int HostRedPixelsTop;
        public int ClientRedPixelsTop;
        public string HostRedBox;
        public string ClientRedBox;
        public bool BossBarVisible;
        public double BossRedDeltaPct;
        public bool BossBarBoxesMatch;
        public string FramePath;
    }

    public static Bitmap CaptureWindow(IntPtr hwnd, out CaptureInfo info) {
        RECT rect;
        GetClientRect(hwnd, out rect);
        POINT point = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref point);
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap)) {
            IntPtr hdc = graphics.GetHdc();
            bool captured = PrintWindow(hwnd, hdc, 0x3);
            graphics.ReleaseHdc(hdc);
            if (!captured) {
                graphics.CopyFromScreen(point.X, point.Y, 0, 0, new Size(width, height));
            }
        }
        info = new CaptureInfo { Width = width, Height = height, X = point.X, Y = point.Y };
        return bitmap;
    }

    public static PairBitmaps CapturePair(IntPtr hostHwnd, IntPtr clientHwnd, out CaptureInfo hostInfo, out CaptureInfo clientInfo) {
        Bitmap host = CaptureWindow(hostHwnd, out hostInfo);
        Bitmap client = CaptureWindow(clientHwnd, out clientInfo);
        return new PairBitmaps { Host = host, Client = client };
    }

    public static Metrics AnalyzeAndSave(
        Bitmap host,
        Bitmap client,
        string framePath,
        int frame,
        double timeSeconds) {
        int width = Math.Min(host.Width, client.Width);
        int height = Math.Min(host.Height, client.Height);
        int topHeight = Math.Max(1, (int)(height * 0.28));

        long sum = 0;
        long sumTop = 0;
        long sumPlayfield = 0;
        int over = 0;
        int overTop = 0;
        int exactMismatch = 0;
        int playfieldExactMismatch = 0;
        int maxSinglePixelDelta = 0;
        int playfieldPixels = 0;
        int hostRed = 0;
        int clientRed = 0;
        int hostMinX = 999999, hostMinY = 999999, hostMaxX = -1, hostMaxY = -1;
        int clientMinX = 999999, clientMinY = 999999, clientMaxX = -1, clientMaxY = -1;

        Rectangle bounds = new Rectangle(0, 0, width, height);
        BitmapData hostData = host.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        BitmapData clientData = client.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try {
            int hostStride = hostData.Stride;
            int clientStride = clientData.Stride;
            int bytes = Math.Abs(hostStride) * height;
            byte[] hostBytes = new byte[bytes];
            byte[] clientBytes = new byte[bytes];
            Marshal.Copy(hostData.Scan0, hostBytes, 0, bytes);
            Marshal.Copy(clientData.Scan0, clientBytes, 0, bytes);

            for (int y = 0; y < height; y++) {
                int hostRow = y * hostStride;
                int clientRow = y * clientStride;
                for (int x = 0; x < width; x++) {
                    int hi = hostRow + x * 3;
                    int ci = clientRow + x * 3;
                    int hb = hostBytes[hi], hg = hostBytes[hi + 1], hr = hostBytes[hi + 2];
                    int cb = clientBytes[ci], cg = clientBytes[ci + 1], cr = clientBytes[ci + 2];
                    int delta = Math.Abs(hr - cr) + Math.Abs(hg - cg) + Math.Abs(hb - cb);
                    sum += delta;
                    if (delta != 0) exactMismatch++;
                    if (delta > maxSinglePixelDelta) maxSinglePixelDelta = delta;
                    if (delta > 90) over++;

                    bool inPlayfield = y >= topHeight && y < (int)(height * 0.90);
                    if (inPlayfield) {
                        playfieldPixels++;
                        sumPlayfield += delta;
                        if (delta != 0) playfieldExactMismatch++;
                    }

                    if (y < topHeight) {
                        sumTop += delta;
                        if (delta > 90) overTop++;

                        if (hr > 135 && hg < 90 && hb < 90) {
                            hostRed++;
                            if (x < hostMinX) hostMinX = x;
                            if (x > hostMaxX) hostMaxX = x;
                            if (y < hostMinY) hostMinY = y;
                            if (y > hostMaxY) hostMaxY = y;
                        }
                        if (cr > 135 && cg < 90 && cb < 90) {
                            clientRed++;
                            if (x < clientMinX) clientMinX = x;
                            if (x > clientMaxX) clientMaxX = x;
                            if (y < clientMinY) clientMinY = y;
                            if (y > clientMaxY) clientMaxY = y;
                        }
                    }
                }
            }
        } finally {
            host.UnlockBits(hostData);
            client.UnlockBits(clientData);
        }

        using (Bitmap combined = new Bitmap(width * 2 + 8, height, PixelFormat.Format24bppRgb))
        using (Graphics graphics = Graphics.FromImage(combined))
        using (Brush brush = new SolidBrush(Color.Black))
        using (Font font = new Font("Arial", 12)) {
            graphics.FillRectangle(brush, 0, 0, combined.Width, combined.Height);
            graphics.DrawImage(host, 0, 0, width, height);
            graphics.DrawImage(client, width + 8, 0, width, height);
            graphics.DrawString("HOST", font, Brushes.White, 8, 8);
            graphics.DrawString("CLIENT", font, Brushes.White, width + 16, 8);
            combined.Save(framePath, ImageFormat.Jpeg);
        }

        double pixelCount = (double)width * height;
        double topPixelCount = (double)width * topHeight;
        double playfieldPixelCount = Math.Max(1.0, (double)playfieldPixels);
        string hostBox = hostRed > 0 ? hostMinX + "," + hostMinY + "-" + hostMaxX + "," + hostMaxY : "none";
        string clientBox = clientRed > 0 ? clientMinX + "," + clientMinY + "-" + clientMaxX + "," + clientMaxY : "none";
        int maxRed = Math.Max(hostRed, clientRed);
        double redDelta = maxRed > 0 ? Math.Abs(hostRed - clientRed) * 100.0 / maxRed : 0.0;
        bool bossVisible = hostRed > 500 && clientRed > 500 && (hostMaxX - hostMinX) > 120 && (clientMaxX - clientMinX) > 120;

        return new Metrics {
            Frame = frame,
            TimeSeconds = Math.Round(timeSeconds, 3),
            Width = width,
            Height = height,
            ExactMismatchPixels = exactMismatch,
            ExactMismatchPct = Math.Round(exactMismatch * 100.0 / pixelCount, 3),
            PlayfieldExactMismatchPixels = playfieldExactMismatch,
            PlayfieldExactMismatchPct = Math.Round(playfieldExactMismatch * 100.0 / playfieldPixelCount, 3),
            MeanAbsDiffPerChannel = Math.Round(sum / (pixelCount * 3.0), 3),
            PlayfieldMeanAbsDiffPerChannel = Math.Round(sumPlayfield / (playfieldPixelCount * 3.0), 3),
            PixelsOverThresholdPct = Math.Round(over * 100.0 / pixelCount, 3),
            TopHudMeanAbsDiffPerChannel = Math.Round(sumTop / (topPixelCount * 3.0), 3),
            TopHudPixelsOverThresholdPct = Math.Round(overTop * 100.0 / topPixelCount, 3),
            MaxSinglePixelDelta = maxSinglePixelDelta,
            HostRedPixelsTop = hostRed,
            ClientRedPixelsTop = clientRed,
            HostRedBox = hostBox,
            ClientRedBox = clientBox,
            BossBarVisible = bossVisible,
            BossRedDeltaPct = Math.Round(redDelta, 3),
            BossBarBoxesMatch = hostBox == clientBox,
            FramePath = framePath
        };
    }
}
"@ -ReferencedAssemblies System.Drawing

function Wait-WindowHandle([System.Diagnostics.Process]$process) {
    for ($i = 0; $i -lt 160; $i++) {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $process.MainWindowHandle
        }
        Start-Sleep -Milliseconds 250
    }
    throw "No window handle for PID $($process.Id)"
}

function Read-Log([string]$path) {
    if (Test-Path $path) {
        return Get-Content -LiteralPath $path -Raw
    }
    return ""
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $OutputRoot $timestamp
$framesDir = Join-Path $runDir "frames"
New-Item -ItemType Directory -Path $framesDir -Force | Out-Null

Get-Process Cuphead -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Reset-TestSaves
Configure-TestCopies

[CupheadContinuousCapture]::SetProcessDPIAware() | Out-Null
$foregroundBeforeLaunch = [CupheadContinuousCapture]::GetForegroundWindow()
$unityArgs = "-screen-fullscreen 0 -screen-width 640 -screen-height 360"
$ahk = Resolve-OptionalToolPath "AutoHotkey" @(
    "C:\Program Files\AutoHotkey\v2\AutoHotkey.exe",
    "C:\Program Files\AutoHotkey\AutoHotkey.exe",
    (Join-Path $env:LOCALAPPDATA "Programs\AutoHotkey\v2\AutoHotkey.exe")
)
$ahkLauncher = Join-Path $repo "tools\launch-cuphead-sync.ahk"
if ($ahk -and (Test-Path -LiteralPath $ahkLauncher)) {
    $launchInfo = Join-Path $runDir "ahk-launch-info.txt"
    $LASTEXITCODE = $null
    & $ahk /ErrorStdOut $ahkLauncher $hostRoot $clientRoot $launchInfo $unityArgs 30 60 672 425 730 60 672 425 2000 30
    $ahkExitCode = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } elseif ($?) { 0 } else { 1 }
    if ($ahkExitCode -ne 0) {
        throw "AutoHotkey Cuphead launcher failed with exit code $ahkExitCode. See $launchInfo"
    }

    $launch = @{}
    $launchDeadline = (Get-Date).AddSeconds(40)
    do {
        if (Test-Path -LiteralPath $launchInfo) {
            $launch = @{}
            Get-Content -LiteralPath $launchInfo -ErrorAction SilentlyContinue | ForEach-Object {
                $idx = $_.IndexOf('=')
                if ($idx -gt 0) {
                    $launch[$_.Substring(0, $idx)] = $_.Substring($idx + 1)
                }
            }
            if ($launch.ContainsKey('error')) {
                throw "AutoHotkey Cuphead launcher failed: $($launch['error'])"
            }
            if (
                $launch.ContainsKey('hostPid') -and
                $launch.ContainsKey('clientPid') -and
                $launch.ContainsKey('hostHwnd') -and
                $launch.ContainsKey('clientHwnd')
            ) {
                break
            }
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $launchDeadline)

    if (
        -not $launch.ContainsKey('hostPid') -or
        -not $launch.ContainsKey('clientPid') -or
        -not $launch.ContainsKey('hostHwnd') -or
        -not $launch.ContainsKey('clientHwnd')
    ) {
        throw "AutoHotkey Cuphead launcher did not report both window handles. See $launchInfo"
    }

    $hostProcess = Get-Process -Id ([int]$launch['hostPid'])
    $clientProcess = Get-Process -Id ([int]$launch['clientPid'])
    $hostHandle = [IntPtr]([int64]$launch['hostHwnd'])
    $clientHandle = [IntPtr]([int64]$launch['clientHwnd'])
} else {
    $hostProcess = Start-Process -FilePath (Join-Path $hostRoot "Cuphead.exe") -WorkingDirectory $hostRoot -ArgumentList $unityArgs -PassThru
    Start-Sleep -Seconds 2
    $clientProcess = Start-Process -FilePath (Join-Path $clientRoot "Cuphead.exe") -WorkingDirectory $clientRoot -ArgumentList $unityArgs -PassThru

    $hostHandle = Wait-WindowHandle $hostProcess
    $clientHandle = Wait-WindowHandle $clientProcess
    [CupheadContinuousCapture]::ShowWindow($hostHandle, 9) | Out-Null
    [CupheadContinuousCapture]::ShowWindow($clientHandle, 9) | Out-Null
    [CupheadContinuousCapture]::MoveWindow($hostHandle, 30, 60, 672, 425, $true) | Out-Null
    [CupheadContinuousCapture]::MoveWindow($clientHandle, 730, 60, 672, 425, $true) | Out-Null
}
[CupheadContinuousCapture]::SetWindowPos($hostHandle, [IntPtr]1, 30, 60, 672, 425, 0x0010 -bor 0x0400) | Out-Null
[CupheadContinuousCapture]::SetWindowPos($clientHandle, [IntPtr]1, 730, 60, 672, 425, 0x0010 -bor 0x0400) | Out-Null
if ($foregroundBeforeLaunch -ne [IntPtr]::Zero) {
    [CupheadContinuousCapture]::SetForegroundWindow($foregroundBeforeLaunch) | Out-Null
}
Start-Sleep -Milliseconds 800

$hostLog = Join-Path $hostRoot "BepInEx\LogOutput.log"
$clientLog = Join-Path $clientRoot "BepInEx\LogOutput.log"
$frameDelay = [Math]::Max(0.01, 1.0 / [Math]::Max(1, $Fps))
$started = Get-Date
$deadline = $started.AddSeconds($MaxSeconds)
$metrics = New-Object System.Collections.Generic.List[object]
$hostPass = $false
$clientPass = $false
$failed = $false
$frame = 0

while ((Get-Date) -lt $deadline) {
    $loopStarted = Get-Date
    $frame++
    $hostInfo = $null
    $clientInfo = $null
    $pair = [CupheadContinuousCapture]::CapturePair($hostHandle, $clientHandle, [ref]$hostInfo, [ref]$clientInfo)
    try {
        $framePath = Join-Path $framesDir ("frame_{0:D6}.jpg" -f $frame)
        $elapsed = ((Get-Date) - $started).TotalSeconds
        $metrics.Add([CupheadContinuousCapture]::AnalyzeAndSave($pair.Host, $pair.Client, $framePath, $frame, $elapsed))
    } finally {
        if ($pair -ne $null) {
            if ($pair.Host -ne $null) { $pair.Host.Dispose() }
            if ($pair.Client -ne $null) { $pair.Client.Dispose() }
        }
    }

    $hostText = Read-Log $hostLog
    $clientText = Read-Log $clientLog
    if ($hostText -match "\[LanSteamE2E\] FAIL" -or $clientText -match "\[LanSteamE2E\] FAIL") {
        $failed = $true
        break
    }
    $hostPass = $hostText -match "HOST PASS"
    $clientPass = $clientText -match "CLIENT PASS"
    if ($hostPass -and $clientPass) {
        for ($i = 0; $i -lt $Fps; $i++) {
            $frame++
            $hostInfo = $null
            $clientInfo = $null
            $pair = [CupheadContinuousCapture]::CapturePair($hostHandle, $clientHandle, [ref]$hostInfo, [ref]$clientInfo)
            try {
                $framePath = Join-Path $framesDir ("frame_{0:D6}.jpg" -f $frame)
                $elapsed = ((Get-Date) - $started).TotalSeconds
                $metrics.Add([CupheadContinuousCapture]::AnalyzeAndSave($pair.Host, $pair.Client, $framePath, $frame, $elapsed))
            } finally {
                if ($pair -ne $null) {
                    if ($pair.Host -ne $null) { $pair.Host.Dispose() }
                    if ($pair.Client -ne $null) { $pair.Client.Dispose() }
                }
            }
            Start-Sleep -Milliseconds ([int](1000 / [Math]::Max(1, $Fps)))
        }
        break
    }

    $elapsedLoop = ((Get-Date) - $loopStarted).TotalSeconds
    $remaining = $frameDelay - $elapsedLoop
    if ($remaining -gt 0) {
        Start-Sleep -Milliseconds ([int]($remaining * 1000))
    }
}

$hostTextFinal = Read-Log $hostLog
$clientTextFinal = Read-Log $clientLog
$hostSummary = (($hostTextFinal -split "`r?`n") | Where-Object { $_ -match "Guest-only shooting smoke|Fight smoke complete|pause menu|resume|pause sync|PAUSE/RESUME|HOST PASS|FAIL" } | Select-Object -Last 16) -join "`n"
$clientSummary = (($clientTextFinal -split "`r?`n") | Where-Object { $_ -match "Client host-checkpoint pause/resume sync complete|Client pause/resume sync complete|pause menu|resume|CLIENT PASS|FAIL|Received host fight checkpoint|Sanitized Player 1 loadout|Simulation drift detected|Host snapshots stalled" } | Select-Object -Last 16) -join "`n"
$legacyLoadoutFixtureExercised = $false
$steamParityFailure = ""
$syncHealthFailure = ""
if ($UseLegacyClientLoadoutIds) {
    $legacyLoadoutFixtureExercised =
        ($clientTextFinal -match "W1=9->") -or
        ($clientTextFinal -match "W1=9\s") -or
        ($hostTextFinal -match "W1=9\s")

    if ($SteamParityProfile -and -not $legacyLoadoutFixtureExercised) {
        $failed = $true
        $steamParityFailure = "Steam parity profile did not exercise the legacy client loadout fixture."
    }
}

$syncHealthIssues = @()
if ($clientTextFinal -match "Simulation drift detected") {
    $syncHealthIssues += "Client reported simulation drift."
}
if ($clientTextFinal -match "Host snapshots stalled") {
    $syncHealthIssues += "Client reported stalled host snapshots."
}
if ($syncHealthIssues.Count -gt 0) {
    $failed = $true
    $syncHealthFailure = ($syncHealthIssues -join " ")
}

$bossFrames = @($metrics | Where-Object { $_.BossBarVisible })
$bossMismatchFrames = @($bossFrames | Where-Object { $_.BossRedDeltaPct -gt 1.0 -or -not $_.BossBarBoxesMatch })
$exactMatchFrames = @($metrics | Where-Object { $_.ExactMismatchPixels -eq 0 })
$fullMismatchFrames = @($metrics | Where-Object { $_.ExactMismatchPixels -gt 0 })
$maxOverall = ($metrics | Sort-Object MeanAbsDiffPerChannel -Descending | Select-Object -First 1)
$maxExactMismatch = ($metrics | Sort-Object ExactMismatchPct -Descending | Select-Object -First 1)
$maxPlayfieldMismatch = ($metrics | Sort-Object PlayfieldExactMismatchPct -Descending | Select-Object -First 1)
$maxTop = ($metrics | Sort-Object TopHudMeanAbsDiffPerChannel -Descending | Select-Object -First 1)
$maxBossDelta = ($bossFrames | Sort-Object BossRedDeltaPct -Descending | Select-Object -First 1)

$videoPath = Join-Path $runDir "cuphead-sync-side-by-side.mp4"
& $ffmpeg -y -hide_banner -loglevel error -framerate $Fps -i (Join-Path $framesDir "frame_%06d.jpg") -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2" -c:v libx264 -pix_fmt yuv420p -crf 18 $videoPath

$report = [pscustomobject]@{
    RunDirectory = $runDir
    FpsTarget = $Fps
    CaptureMode = "PrintWindow capture of each Cuphead client area, independent of foreground occlusion"
    FrameCount = $metrics.Count
    DurationSeconds = if ($metrics.Count -gt 0) { [Math]::Round($metrics[$metrics.Count - 1].TimeSeconds, 3) } else { 0 }
    HostPass = $hostPass
    ClientPass = $clientPass
    Failed = $failed
    SteamParityProfile = [bool]$SteamParityProfile
    LanLatencyMs = $LanLatencyMs
    LanJitterMs = $LanJitterMs
    LanUnreliableDropPercent = $LanUnreliableDropPercent
    LegacyClientLoadoutIds = [bool]$UseLegacyClientLoadoutIds
    LegacyLoadoutFixtureExercised = [bool]$legacyLoadoutFixtureExercised
    SteamParityFailure = $steamParityFailure
    SyncHealthFailure = $syncHealthFailure
    VideoPath = $videoPath
    FramesDirectory = $framesDir
    BossVisibleFrameCount = $bossFrames.Count
    BossMismatchFrameCount = $bossMismatchFrames.Count
    ExactFullFrameMatchCount = $exactMatchFrames.Count
    ExactFullFrameMismatchCount = $fullMismatchFrames.Count
    ExactFullFrameMatch = $fullMismatchFrames.Count -eq 0
    BossMismatchFrames = @($bossMismatchFrames | Select-Object -First 20 Frame,TimeSeconds,HostRedPixelsTop,ClientRedPixelsTop,HostRedBox,ClientRedBox,BossRedDeltaPct)
    MaxOverallDiffFrame = $maxOverall
    MaxExactMismatchFrame = $maxExactMismatch
    MaxPlayfieldMismatchFrame = $maxPlayfieldMismatch
    MaxTopHudDiffFrame = $maxTop
    MaxBossRedDeltaFrame = $maxBossDelta
    HostLogSummary = $hostSummary
    ClientLogSummary = $clientSummary
}

$reportPath = Join-Path $runDir "analysis-report.json"
$metricsPath = Join-Path $runDir "frame-metrics.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
$metrics | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metricsPath -Encoding UTF8
$report | ConvertTo-Json -Depth 8
