#Requires -Version 7.0
<#
.SYNOPSIS
    Saves or loads complete monitor profiles through the Windows CCD API.

.DESCRIPTION
    Uses QueryDisplayConfig/SetDisplayConfig to switch the whole display topology
    (active displays, resolution, refresh rate, position, main display) in a
    single step. Adapter LUIDs and target IDs are remapped by device path on load,
    so profiles stay valid after a restart or a reconnect (e.g. spacedesk).

.EXAMPLE
    .\DisplayProfile.ps1 -Action Save -Name Rig
    .\DisplayProfile.ps1 -Action Apply -Name Desk
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Save', 'Apply')]
    [string]$Action,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,32}$')]
    [string]$Name
)

$ErrorActionPreference = 'Stop'

$profileFile = Join-Path $PSScriptRoot "$Name.display"
$logFile = Join-Path $PSScriptRoot 'DisplayProfile.log'

function Write-Log {
    param([string]$Message)
    $line = '{0:yyyy-MM-dd HH:mm:ss} [{1}/{2}] {3}' -f (Get-Date), $Action, $Name, $Message
    Add-Content -Path $logFile -Value $line -Encoding utf8
    Write-Output $line
}

$source = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DisplayProfileNative
{
    const uint QDC_ALL_PATHS = 0x1;
    const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    const uint SDC_APPLY = 0x80;
    const uint SDC_SAVE_TO_DATABASE = 0x200;
    const uint SDC_ALLOW_CHANGES = 0x400;
    const uint INVALID_IDX = 0xFFFFFFFF;
    const uint MODE_TYPE_TARGET = 2;

    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] public struct RATIONAL { public uint Num; public uint Den; }
    [StructLayout(LayoutKind.Sequential)] public struct SRC { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct TGT { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling; public RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct PATH { public SRC sourceInfo; public TGT targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct MODE
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public ulong pad0;
        [FieldOffset(24)] public ulong pad1;
        [FieldOffset(32)] public ulong pad2;
        [FieldOffset(40)] public ulong pad3;
        [FieldOffset(48)] public ulong pad4;
        [FieldOffset(56)] public uint pad5;
        [FieldOffset(60)] public uint pad6;
    }

    [StructLayout(LayoutKind.Sequential)] public struct HDR { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TGTNAME
    {
        public HDR header; public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ADAPTERNAME
    {
        public HDR header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH[] paths, ref uint numModes, [Out] MODE[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, [In] PATH[] paths, uint numModes, [In] MODE[] modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TGTNAME request);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ADAPTERNAME request);

    static void Query(uint flags, out PATH[] paths, out MODE[] modes)
    {
        uint np, nm;
        int err = GetDisplayConfigBufferSizes(flags, out np, out nm);
        if (err != 0) throw new InvalidOperationException("GetDisplayConfigBufferSizes error " + err);
        paths = new PATH[np];
        modes = new MODE[nm];
        err = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
        if (err != 0) throw new InvalidOperationException("QueryDisplayConfig error " + err);
        Array.Resize(ref paths, (int)np);
        Array.Resize(ref modes, (int)nm);
    }

    static string AdapterPath(LUID luid)
    {
        var req = new ADAPTERNAME();
        req.header.type = 4;
        req.header.size = (uint)Marshal.SizeOf(typeof(ADAPTERNAME));
        req.header.adapterId = luid;
        return DisplayConfigGetDeviceInfo(ref req) == 0 ? req.devicePath : "";
    }

    static TGTNAME TargetName(LUID luid, uint targetId)
    {
        var req = new TGTNAME();
        req.header.type = 2;
        req.header.size = (uint)Marshal.SizeOf(typeof(TGTNAME));
        req.header.adapterId = luid;
        req.header.id = targetId;
        if (DisplayConfigGetDeviceInfo(ref req) != 0) { req.devicePath = ""; req.friendlyName = ""; }
        return req;
    }

    static byte[] ToBytes<T>(T value) where T : struct
    {
        var bytes = new byte[Marshal.SizeOf(typeof(T))];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false); }
        finally { handle.Free(); }
        return bytes;
    }

    static T FromBytes<T>(byte[] bytes) where T : struct
    {
        if (bytes.Length != Marshal.SizeOf(typeof(T))) throw new InvalidOperationException("Profile file is corrupt");
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T)); }
        finally { handle.Free(); }
    }

    static string Clean(string value) { return (value ?? "").Replace("|", "").Trim(); }

    public static string Save()
    {
        PATH[] paths; MODE[] modes;
        Query(QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
        if (paths.Length == 0) throw new InvalidOperationException("No active displays found");

        var sb = new StringBuilder();
        foreach (var p in paths)
        {
            var tn = TargetName(p.targetInfo.adapterId, p.targetInfo.id);
            sb.AppendLine(string.Join("|", new[] {
                "P", Convert.ToBase64String(ToBytes(p)),
                Clean(AdapterPath(p.sourceInfo.adapterId)), Clean(AdapterPath(p.targetInfo.adapterId)),
                Clean(tn.devicePath), Clean(tn.friendlyName) }));
        }
        foreach (var m in modes)
        {
            string targetPath = m.infoType == MODE_TYPE_TARGET ? Clean(TargetName(m.adapterId, m.id).devicePath) : "";
            sb.AppendLine(string.Join("|", new[] { "M", Convert.ToBase64String(ToBytes(m)), Clean(AdapterPath(m.adapterId)), targetPath }));
        }
        return sb.ToString();
    }

    static uint MapMode(uint oldIdx, List<MODE> savedModes, List<string[]> savedModeInfo, Dictionary<string, LUID> adapters,
                        Dictionary<uint, uint> indexMap, List<MODE> outModes, bool isTarget, uint newTargetId)
    {
        if (oldIdx == INVALID_IDX || oldIdx >= savedModes.Count) return INVALID_IDX;
        uint newIdx;
        if (indexMap.TryGetValue(oldIdx, out newIdx)) return newIdx;
        LUID luid;
        if (!adapters.TryGetValue(savedModeInfo[(int)oldIdx][2], out luid)) return INVALID_IDX;
        var mode = savedModes[(int)oldIdx];
        mode.adapterId = luid;
        if (isTarget) mode.id = newTargetId;
        outModes.Add(mode);
        newIdx = (uint)(outModes.Count - 1);
        indexMap[oldIdx] = newIdx;
        return newIdx;
    }

    public static string Apply(string profileText)
    {
        var log = new StringBuilder();

        PATH[] allPaths; MODE[] allModes;
        Query(QDC_ALL_PATHS, out allPaths, out allModes);

        var adapters = new Dictionary<string, LUID>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in allPaths)
        {
            string sa = Clean(AdapterPath(p.sourceInfo.adapterId));
            string ta = Clean(AdapterPath(p.targetInfo.adapterId));
            if (sa.Length > 0 && !adapters.ContainsKey(sa)) adapters[sa] = p.sourceInfo.adapterId;
            if (ta.Length > 0 && !adapters.ContainsKey(ta)) adapters[ta] = p.targetInfo.adapterId;
            if (p.targetInfo.targetAvailable != 0)
            {
                string tp = Clean(TargetName(p.targetInfo.adapterId, p.targetInfo.id).devicePath);
                if (tp.Length > 0) targets[ta + "|" + tp] = p.targetInfo.id;
            }
        }

        var savedPaths = new List<PATH>(); var savedPathInfo = new List<string[]>();
        var savedModes = new List<MODE>(); var savedModeInfo = new List<string[]>();
        foreach (var line in profileText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|');
            if (f[0] == "P" && f.Length >= 6) { savedPaths.Add(FromBytes<PATH>(Convert.FromBase64String(f[1]))); savedPathInfo.Add(f); }
            else if (f[0] == "M" && f.Length >= 4) { savedModes.Add(FromBytes<MODE>(Convert.FromBase64String(f[1]))); savedModeInfo.Add(f); }
        }
        if (savedPaths.Count == 0) throw new InvalidOperationException("Profile contains no displays");

        var outPaths = new List<PATH>(); var outModes = new List<MODE>(); var indexMap = new Dictionary<uint, uint>();
        for (int i = 0; i < savedPaths.Count; i++)
        {
            var p = savedPaths[i]; var info = savedPathInfo[i];
            LUID srcLuid, tgtLuid; uint targetId;
            if (!adapters.TryGetValue(info[2], out srcLuid) || !adapters.TryGetValue(info[3], out tgtLuid) || !targets.TryGetValue(info[3] + "|" + info[4], out targetId))
            {
                log.AppendLine("Not available, skipped: " + info[5]);
                continue;
            }
            p.sourceInfo.adapterId = srcLuid;
            p.targetInfo.adapterId = tgtLuid;
            p.targetInfo.id = targetId;
            p.flags = 1;
            p.sourceInfo.modeInfoIdx = MapMode(p.sourceInfo.modeInfoIdx, savedModes, savedModeInfo, adapters, indexMap, outModes, false, 0);
            p.targetInfo.modeInfoIdx = MapMode(p.targetInfo.modeInfoIdx, savedModes, savedModeInfo, adapters, indexMap, outModes, true, targetId);
            outPaths.Add(p);
            log.AppendLine("Enabling: " + info[5]);
        }
        if (outPaths.Count == 0) throw new InvalidOperationException("None of the profile's displays is available");

        uint flags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES;
        var pathArray = outPaths.ToArray();
        int result = SetDisplayConfig((uint)pathArray.Length, pathArray, (uint)outModes.Count, outModes.ToArray(), flags);
        log.AppendLine("SetDisplayConfig with modes: " + result);

        if (result != 0)
        {
            for (int i = 0; i < pathArray.Length; i++) { pathArray[i].sourceInfo.modeInfoIdx = INVALID_IDX; pathArray[i].targetInfo.modeInfoIdx = INVALID_IDX; }
            result = SetDisplayConfig((uint)pathArray.Length, pathArray, 0, null, flags);
            log.AppendLine("SetDisplayConfig without modes: " + result);
        }
        if (result != 0) throw new InvalidOperationException(log.ToString().Trim() + " | error code " + result);
        return log.ToString();
    }
}
'@

$audioSource = @'
using System;
using System.Runtime.InteropServices;

public static class DisplayProfileAudio
{
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    // Roles: 0 = console, 1 = multimedia, 2 = communications
    public static void SetDefault(string deviceId)
    {
        var client = new PolicyConfigClient();
        try
        {
            var policy = (IPolicyConfig)client;
            for (int role = 0; role < 3; role++)
            {
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }
}
'@

$configFile = Join-Path $PSScriptRoot 'DisplayProfiles.json'
$renderKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'

function Set-ProfileAudio {
    if (-not (Test-Path -LiteralPath $configFile)) { return }

    $config = Get-Content -LiteralPath $configFile -Raw -Encoding utf8 | ConvertFrom-Json
    $audio = $config.Profiles.$Name.Audio
    if (-not $audio -or -not $audio.DeviceId) { return }

    if ($audio.DeviceId -notmatch '^\{0\.0\.0\.00000000\}\.\{[0-9a-fA-F-]{36}\}$') {
        throw "Invalid audio device ID in $configFile"
    }

    $endpointGuid = $audio.DeviceId.Substring($audio.DeviceId.IndexOf('}.') + 2)
    $state = (Get-ItemProperty -LiteralPath (Join-Path $renderKey $endpointGuid) -ErrorAction SilentlyContinue).DeviceState
    if ($null -eq $state -or ($state -band 0xF) -ne 1) {
        Write-Log "Audio: $($audio.DeviceName) not available, default device unchanged"
        return
    }

    if (-not ('DisplayProfileAudio' -as [type])) {
        Add-Type -TypeDefinition $audioSource -Language CSharp
    }
    [DisplayProfileAudio]::SetDefault($audio.DeviceId)
    Write-Log "Audio: default device set to $($audio.DeviceName)"
}

$exitCode = 0

try {
    if (-not ('DisplayProfileNative' -as [type])) {
        Add-Type -TypeDefinition $source -Language CSharp
    }

    switch ($Action) {
        'Save' {
            $data = [DisplayProfileNative]::Save()
            Set-Content -Path $profileFile -Value $data -Encoding utf8
            Write-Log "Profile saved: $profileFile"
        }
        'Apply' {
            if (-not (Test-Path -LiteralPath $profileFile)) {
                throw "Profile not found: $profileFile"
            }
            $profileText = Get-Content -LiteralPath $profileFile -Raw -Encoding utf8
            $maxAttempts = 5
            $retryDelaySeconds = 3
            for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
                try {
                    # Apply queries adapters and displays again on every attempt (e.g. the G9 only shows up after waking)
                    $result = [DisplayProfileNative]::Apply($profileText)
                    foreach ($line in ($result -split "`r?`n" | Where-Object { $_ })) {
                        Write-Log $line
                    }
                    if ($attempt -gt 1) {
                        Write-Log "Display applied on attempt $attempt of $maxAttempts"
                    }
                    break
                }
                catch {
                    if ($attempt -ge $maxAttempts) { throw }
                    Write-Log "Attempt $attempt of $maxAttempts failed, retrying in $retryDelaySeconds s: $($_.Exception.Message -replace '\r?\n', ' / ')"
                    Start-Sleep -Seconds $retryDelaySeconds
                }
            }
        }
    }
}
catch {
    Write-Log "ERROR display: $($_.Exception.Message)"
    $exitCode = 1
}

if ($Action -eq 'Apply') {
    try {
        Set-ProfileAudio
    }
    catch {
        Write-Log "ERROR audio: $($_.Exception.Message)"
        $exitCode = 1
    }
}

exit $exitCode
