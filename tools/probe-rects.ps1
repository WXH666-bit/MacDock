# 诊断脚本：打印 MacDock 两个窗口的物理矩形与系统 DPI，用于核对 Dock 布局是否被裁剪。
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
}
"@

$proc = Get-Process MacDock -ErrorAction SilentlyContinue
if (-not $proc) { Write-Output 'MacDock 未运行'; exit }

$target = $proc.Id
$found = @()
$cb = [W+EnumProc]{
  param($h, $p)
  $pid2 = 0
  [W]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
  if ($pid2 -eq $target -and [W]::IsWindowVisible($h)) {
    $r = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $sb = New-Object System.Text.StringBuilder 256
    [W]::GetClassName($h, $sb, 256) | Out-Null
    $ex = [W]::GetWindowLongPtr($h, -20).ToInt64()
    if (($r.right - $r.left) -gt 40) {
      $script:found += [pscustomobject]@{
        Hwnd = $h; W = $r.right - $r.left; H = $r.bottom - $r.top
        L = $r.left; T = $r.top; R = $r.right; B = $r.bottom
        Transparent = [bool]($ex -band 0x20)
      }
    }
  }
  return $true
}
[W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

Add-Type -AssemblyName System.Windows.Forms
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output "屏幕物理: $($bounds.Width)x$($bounds.Height)  工作区底边(px): $($wa.Bottom)"
$found | Sort-Object T | Format-Table -AutoSize | Out-String | Write-Output
