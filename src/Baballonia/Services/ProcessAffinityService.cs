using Baballonia.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Services;

public sealed class ProcessAffinityService : BackgroundService
{
    private const long DefaultLastFourThreadsMask = 0xF000;
    private static readonly string[] VrcftProcessNames =
    [
        "VRCFaceTracking",
        "VRCFaceTracking.ModuleProcess"
    ];

    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<ProcessAffinityService> _logger;
    private long _lastAppliedSelfMask;
    private long _lastAppliedVrcftMask;

    public ProcessAffinityService(ILocalSettingsService localSettingsService, ILogger<ProcessAffinityService> logger)
    {
        _localSettingsService = localSettingsService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ApplyConfiguredAffinity();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Ignoring affinity update failure");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private void ApplyConfiguredAffinity()
    {
        var selfEnabled = _localSettingsService.ReadSetting("AppSettings_AffinityEnabled", false);
        var selfMask = NormalizeMask(_localSettingsService.ReadSetting<long>("AppSettings_AffinityMask", 0));
        if (selfEnabled && selfMask != 0)
        {
            using var currentProcess = Process.GetCurrentProcess();
            ApplyAffinity(currentProcess, selfMask, ref _lastAppliedSelfMask, "Baballonia", disposeProcess: false);
        }

        var vrcftEnabled = _localSettingsService.ReadSetting("AppSettings_VrcftAffinityEnabled", false);
        var vrcftMask = NormalizeMask(_localSettingsService.ReadSetting<long>("AppSettings_VrcftAffinityMask", 0));
        if (!vrcftEnabled || vrcftMask == 0)
            return;

        foreach (var processName in VrcftProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                ApplyAffinity(process, vrcftMask, ref _lastAppliedVrcftMask, process.ProcessName, disposeProcess: true);
            }
        }
    }

    private long NormalizeMask(long configuredMask)
    {
        if (configuredMask != 0)
            return configuredMask;

        var processorCount = Environment.ProcessorCount;
        if (processorCount == 16)
            return DefaultLastFourThreadsMask;

        if (processorCount >= 8)
        {
            var reservedThreads = Math.Min(4, processorCount / 2);
            var startBit = Math.Max(0, processorCount - reservedThreads);
            long mask = 0;
            for (var bit = startBit; bit < processorCount; bit++)
                mask |= 1L << bit;
            return mask;
        }

        return 0;
    }

    private void ApplyAffinity(Process process, long mask, ref long lastAppliedMask, string label, bool disposeProcess)
    {
        try
        {
            if (process.HasExited)
                return;

            if (lastAppliedMask == mask && (long)process.ProcessorAffinity == mask)
                return;

            process.ProcessorAffinity = (IntPtr)mask;
            lastAppliedMask = mask;
            _logger.LogInformation("Applied CPU affinity {MaskHex} to {Label} pid={Pid}", $"0x{mask:X}", label, process.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to apply CPU affinity {MaskHex} to {Label}", $"0x{mask:X}", label);
        }
        finally
        {
            if (disposeProcess)
                process.Dispose();
        }
    }
}
