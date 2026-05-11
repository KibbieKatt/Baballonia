using Baballonia.Services.events;
using Baballonia.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Services;

public sealed class EyeTraceRecorderService : IHostedService, IDisposable
{
    private const string EnabledSettingKey = "AppSettings_RecordEyeTrace";
    private const string TracesDirectoryName = "EyeTraceCapture";

    private readonly IEyePipelineEventBus _eyePipelineEventBus;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<EyeTraceRecorderService> _logger;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOptions = new();
    private readonly ConcurrentQueue<EyeTraceRow> _pendingRows = new();
    private readonly SemaphoreSlim _pendingSignal = new(0);

    private StreamWriter? _writer;
    private string? _sessionDirectory;
    private bool _recordingEnabled;
    private long _nextSettingsRefreshTick;
    private CancellationTokenSource? _writerCancellationTokenSource;
    private Task? _writerTask;

    public EyeTraceRecorderService(
        IEyePipelineEventBus eyePipelineEventBus,
        ILocalSettingsService localSettingsService,
        ILogger<EyeTraceRecorderService> logger)
    {
        _eyePipelineEventBus = eyePipelineEventBus;
        _localSettingsService = localSettingsService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _eyePipelineEventBus.Subscribe<EyePipelineEvents.TraceResultEvent>(OnTraceResult);
        _writerCancellationTokenSource = new CancellationTokenSource();
        _writerTask = Task.Run(() => WriterLoopAsync(_writerCancellationTokenSource.Token), cancellationToken);
        RefreshRecordingState(force: true);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _eyePipelineEventBus.Unsubscribe<EyePipelineEvents.TraceResultEvent>(OnTraceResult);
        if (_writerCancellationTokenSource != null)
        {
            _writerCancellationTokenSource.Cancel();
            _pendingSignal.Release();
        }

        if (_writerTask != null)
        {
            try
            {
                await _writerTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
        }

        lock (_lock)
        {
            CloseActiveSession();
        }
    }

    private void OnTraceResult(EyePipelineEvents.TraceResultEvent trace)
    {
        RefreshRecordingState();
        if (!_recordingEnabled)
            return;

        lock (_lock)
        {
            EnsureActiveSession(trace);
            if (_sessionDirectory == null)
                return;

            _pendingRows.Enqueue(new EyeTraceRow(
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                trace.frameSequence,
                trace.frameCapturedTick,
                trace.pipelineCompletedTick,
                trace.sourceDescription,
                trace.filterMode,
                trace.rawResult,
                trace.filteredResult,
                trace.processedResult));
        }

        _pendingSignal.Release();
    }

    private void RefreshRecordingState(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now < Interlocked.Read(ref _nextSettingsRefreshTick))
            return;

        Interlocked.Exchange(ref _nextSettingsRefreshTick, now + 500);
        var enabled = _localSettingsService.ReadSetting<bool>(EnabledSettingKey, false);

        lock (_lock)
        {
            if (enabled == _recordingEnabled)
                return;

            _recordingEnabled = enabled;
            if (!_recordingEnabled)
            {
                CloseActiveSession();
                return;
            }

            _logger.LogInformation("Eye trace recording enabled");
        }
    }

    private void EnsureActiveSession(EyePipelineEvents.TraceResultEvent trace)
    {
        if (_writer != null)
            return;

        var tracesRoot = Path.Combine(Utils.UserAccessibleDataDirectory, TracesDirectoryName);
        Directory.CreateDirectory(tracesRoot);

        var sessionName = $"session-{DateTime.Now:yyyyMMdd-HHmmss}";
        _sessionDirectory = Path.Combine(tracesRoot, sessionName);
        Directory.CreateDirectory(_sessionDirectory);

        var metadata = new EyeTraceMetadata(
            CreatedAtUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            StopwatchFrequency: Stopwatch.Frequency,
            FilterMode: trace.filterMode,
            BigeyeBackend: ResolveActiveBackend(trace.sourceDescription),
            VideoSource: trace.sourceDescription,
            CorruptionDetector: _localSettingsService.ReadSetting<string>("AppSettings_CorruptionDetector", "Stock Row") ?? "Stock Row",
            StabilizeEyes: _localSettingsService.ReadSetting<bool>("AppSettings_StabilizeEyes", true),
            UseGpu: _localSettingsService.ReadSetting<bool>("AppSettings_UseGPU", true),
            Notes: "Self-supervised N-Euro training trace. rawResult is pre-filter model output; filteredResult is post-filter pre-process; processedResult is the expression vector sent onward.");

        File.WriteAllText(
            Path.Combine(_sessionDirectory, "metadata.json"),
            JsonSerializer.Serialize(metadata, _jsonOptions));

        _writer = new StreamWriter(Path.Combine(_sessionDirectory, "samples.jsonl"), append: false);
        _logger.LogInformation("Recording eye trace to {Path}", _sessionDirectory);
    }

    private void CloseActiveSession()
    {
        if (_writer == null)
            return;

        try
        {
            _writer.Flush();
            _writer.Dispose();
            _logger.LogInformation("Closed eye trace session {Path}", _sessionDirectory);
        }
        finally
        {
            _writer = null;
            _sessionDirectory = null;
        }
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _pendingSignal.WaitAsync(cancellationToken);
                FlushPendingRows();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            FlushPendingRows();
        }
    }

    private void FlushPendingRows()
    {
        lock (_lock)
        {
            if (_writer == null)
            {
                while (_pendingRows.TryDequeue(out _))
                {
                }

                return;
            }

            while (_pendingRows.TryDequeue(out var row))
            {
                _writer.WriteLine(JsonSerializer.Serialize(row, _jsonOptions));
            }

            _writer.Flush();
        }
    }

    public void Dispose()
    {
        _writerCancellationTokenSource?.Cancel();
        _pendingSignal.Dispose();
        _writerCancellationTokenSource?.Dispose();
        lock (_lock)
        {
            CloseActiveSession();
        }
    }

    private sealed record EyeTraceMetadata(
        string CreatedAtUtc,
        long StopwatchFrequency,
        string FilterMode,
        string BigeyeBackend,
        string VideoSource,
        string CorruptionDetector,
        bool StabilizeEyes,
        bool UseGpu,
        string Notes);

    private sealed record EyeTraceRow(
        string Utc,
        long FrameSequence,
        long FrameCapturedTick,
        long PipelineCompletedTick,
        string SourceDescription,
        string FilterMode,
        float[] RawResult,
        float[] FilteredResult,
        float[] ProcessedResult);

    private string ResolveActiveBackend(string sourceDescription)
    {
        if (sourceDescription.Contains("dshow:", StringComparison.OrdinalIgnoreCase))
            return "Low Latency";

        if (sourceDescription.Contains("msmf:", StringComparison.OrdinalIgnoreCase))
            return "Stable";

        return _localSettingsService.ReadSetting<string>("AppSettings_BigeyeWindowsBackend", "Stable") ?? "Stable";
    }
}
