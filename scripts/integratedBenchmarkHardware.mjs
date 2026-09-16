import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readdir, readFile, stat } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

export function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

export async function hashFile(filePath) {
  return sha256(await readFile(filePath));
}

export async function hashDirectory(directory) {
  const entries = [];
  async function visit(current) {
    for (const entry of (await readdir(current, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
      const absolute = path.join(current, entry.name);
      if (entry.isDirectory()) await visit(absolute);
      else if (entry.isFile()) entries.push({ path: path.relative(directory, absolute).replaceAll(path.sep, '/'), hash: await hashFile(absolute) });
    }
  }
  await visit(directory);
  return sha256(Buffer.from(entries.map((entry) => `${entry.path}\0${entry.hash}\n`).join('')));
}

function powershellInventory(exec = execFileSync) {
  const script = String.raw`
$cpu = try { Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1 Name,NumberOfCores,NumberOfLogicalProcessors } catch {
  $processor = Get-ItemProperty -Path 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -ErrorAction SilentlyContinue
  [pscustomobject]@{ Name = $processor.ProcessorNameString; NumberOfCores = $null; NumberOfLogicalProcessors = $env:NUMBER_OF_PROCESSORS }
}
$os = try { Get-CimInstance Win32_OperatingSystem -ErrorAction Stop | Select-Object -First 1 Caption,Version,BuildNumber,TotalVisibleMemorySize } catch {
  $windows = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
  [pscustomobject]@{ Caption = $windows.ProductName; Version = [Environment]::OSVersion.Version.ToString();
    BuildNumber = [string]$windows.CurrentBuild; TotalVisibleMemorySize = $null }
}
$gpus = try { @(Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,@{Name='InventorySource';Expression={'Win32_VideoController'}}) } catch {
  $source = @'
using System;
using System.Runtime.InteropServices;
public static class IntegratedDisplayMode {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)] public struct POINTL { public int x; public int y; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)] public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields; public POINTL dmPosition;
    public int dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet=CharSet.Ansi)] static extern bool EnumDisplaySettings(string name, int mode, ref DEVMODE value);
  public static int[] Current() { var value = new DEVMODE(); value.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
    return EnumDisplaySettings(null, -1, ref value) ? new [] { value.dmPelsWidth, value.dmPelsHeight, value.dmDisplayFrequency } : new [] { 0, 0, 0 }; }
}
'@
  Add-Type -TypeDefinition $source
  $mode = [IntegratedDisplayMode]::Current()
  $adapter = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Video\*\0000' -ErrorAction SilentlyContinue |
    Where-Object { $_.DriverDesc -and $_.DriverVersion } | Select-Object -First 1
  @([pscustomobject]@{ Name = $adapter.DriverDesc; DriverVersion = $adapter.DriverVersion;
    CurrentHorizontalResolution = $mode[0]; CurrentVerticalResolution = $mode[1]; CurrentRefreshRate = $mode[2];
    InventorySource = 'registry+EnumDisplaySettings' })
}
$power = try { (powercfg /getactivescheme | Out-String).Trim() } catch { $null }
$thermal = try { @(Get-CimInstance -Namespace root/wmi MSAcpi_ThermalZoneTemperature | Select-Object CurrentTemperature) } catch { @() }
[ordered]@{ cpu = $cpu; os = $os; gpus = $gpus; power = $power; thermal = $thermal } | ConvertTo-Json -Depth 5 -Compress
`;
  return JSON.parse(exec('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8', windowsHide: true, timeout: 30_000, stdio: ['ignore', 'pipe', 'pipe'],
  }));
}

function text(value) {
  return typeof value === 'string' && value.trim() ? value.trim() : 'unavailable';
}

function numeric(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

export function normalizeWindowsInventory(raw, hostname = os.hostname()) {
  const gpus = Array.isArray(raw?.gpus) ? raw.gpus : raw?.gpus ? [raw.gpus] : [];
  const displayGpu = gpus.find((gpu) => numeric(gpu.CurrentHorizontalResolution) && numeric(gpu.CurrentVerticalResolution)) ?? gpus[0];
  const thermalValues = (Array.isArray(raw?.thermal) ? raw.thermal : raw?.thermal ? [raw.thermal] : [])
    .map((entry) => numeric(entry.CurrentTemperature)).filter((value) => value !== null)
    .map((tenthsKelvin) => Math.round((tenthsKelvin / 10 - 273.15) * 10) / 10);
  const build = numeric(raw?.os?.BuildNumber);
  const reportedCaption = text(raw?.os?.Caption);
  const caption = build && build >= 22_000
    ? reportedCaption.replace(/Windows 10/i, 'Windows 11')
    : reportedCaption;
  return {
    id: hostname,
    cpu: text(raw?.cpu?.Name),
    cpuCores: numeric(raw?.cpu?.NumberOfCores),
    cpuLogicalProcessors: numeric(raw?.cpu?.NumberOfLogicalProcessors),
    ramBytes: numeric(raw?.os?.TotalVisibleMemorySize) ? numeric(raw.os.TotalVisibleMemorySize) * 1024 : null,
    gpu: gpus.length ? gpus.map((gpu) => text(gpu.Name)).join(' | ') : 'unavailable',
    driver: gpus.length ? gpus.map((gpu) => text(gpu.DriverVersion)).join(' | ') : 'unavailable',
    os: [caption, text(raw?.os?.Version), `build ${text(raw?.os?.BuildNumber)}`].join(' '),
    display: {
      width: numeric(displayGpu?.CurrentHorizontalResolution),
      height: numeric(displayGpu?.CurrentVerticalResolution),
      refreshHz: numeric(displayGpu?.CurrentRefreshRate),
      source: displayGpu ? text(displayGpu.InventorySource) : 'unavailable',
    },
    powerObservation: raw?.power ? { availability: 'available', value: String(raw.power) }
      : { availability: 'unavailable', value: null },
    thermalObservation: thermalValues.length ? { availability: 'available', celsius: thermalValues }
      : { availability: 'unavailable', celsius: [] },
  };
}

export function collectHardwareMetadata(options = {}) {
  if ((options.platform ?? process.platform) !== 'win32') {
    return {
      id: os.hostname(), cpu: os.cpus()[0]?.model ?? 'unavailable', cpuCores: os.cpus().length,
      cpuLogicalProcessors: os.cpus().length, ramBytes: os.totalmem(), gpu: 'unavailable', driver: 'unavailable',
      os: `${os.type()} ${os.release()}`, display: { width: null, height: null, refreshHz: null, source: 'unavailable' },
      powerObservation: { availability: 'unavailable', value: null },
      thermalObservation: { availability: 'unavailable', celsius: [] },
    };
  }
  try {
    const normalized = normalizeWindowsInventory(powershellInventory(options.execFileSyncImpl));
    normalized.cpuCores ??= Number(/(\d+)-Core/i.exec(normalized.cpu)?.[1]) || null;
    normalized.cpuLogicalProcessors ??= os.cpus().length;
    normalized.ramBytes ??= os.totalmem();
    return normalized;
  } catch (error) {
    return {
      id: os.hostname(), cpu: os.cpus()[0]?.model ?? 'unavailable', cpuCores: os.cpus().length,
      cpuLogicalProcessors: os.cpus().length, ramBytes: os.totalmem(), gpu: 'unavailable', driver: 'unavailable',
      os: `${os.version()} ${os.release()}`, display: { width: null, height: null, refreshHz: null, source: 'unavailable' },
      powerObservation: { availability: 'unavailable', value: null },
      thermalObservation: { availability: 'unavailable', celsius: [] },
      collectionError: error instanceof Error ? error.message : String(error),
    };
  }
}

export function qualificationInventoryErrors(hardware) {
  const errors = [];
  for (const field of ['cpu', 'gpu', 'driver', 'os']) {
    if (!hardware[field] || /unavailable|unknown/i.test(hardware[field])) errors.push(`Hardware ${field} is unavailable.`);
  }
  if (!hardware.cpuCores || !hardware.cpuLogicalProcessors) errors.push('Hardware CPU topology is unavailable.');
  if (!hardware.ramBytes) errors.push('Hardware RAM capacity is unavailable.');
  if (!hardware.display?.width || !hardware.display?.height || hardware.display.width < 1920 || hardware.display.height < 1080) {
    errors.push('A detected display of at least 1920x1080 is required.');
  }
  if (!hardware.display?.refreshHz || Math.abs(hardware.display.refreshHz - 60) > 1) {
    errors.push('A detected 60 Hz display is required for the common qualification lane.');
  }
  return errors;
}

export async function gitSourceIdentity(root, exec = execFileSync) {
  const run = (args) => exec('git', args, { cwd: root, encoding: 'utf8', windowsHide: true }).trim();
  return {
    commit: run(['rev-parse', 'HEAD']),
    workingTreeStatus: run(['status', '--porcelain']) ? 'dirty' : 'clean',
    lockfileHash: await hashFile(path.join(root, 'package-lock.json')),
  };
}

export async function assertRegularFile(filePath) {
  const info = await stat(filePath);
  if (!info.isFile()) throw new Error(`Expected a regular file: ${filePath}`);
  return filePath;
}
