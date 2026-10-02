$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CursorProbe {
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr LoadCursorFromFileW(string path);
    [DllImport("user32.dll")]
    public static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO pIconInfo);
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
}
"@
$h = [CursorProbe]::LoadCursorFromFileW("$PWD\cursors\puppy\Arrow.cur")
$ii = New-Object CursorProbe+ICONINFO
[CursorProbe]::GetIconInfo($h, [ref]$ii) | Out-Null
Write-Output ("加载后实际热点: x=" + $ii.xHotspot + " y=" + $ii.yHotspot)
