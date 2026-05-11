using Baballonia.Contracts;
using Baballonia.SDK;
using Baballonia.Services.Inference.Platforms;
using Baballonia.Services.Inference.VideoSources;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Services.Inference;

public class SingleCameraSourceFactory
{
    private readonly ILogger<SingleCameraSourceFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDeviceEnumerator _deviceEnumerator;
    private readonly IPlatformConnector _platformConnector;
    private readonly ILocalSettingsService _localSettingsService;
    private const string BigeyeBackendEnvVar = "BABALLONIA_BIGEYE_BACKEND";
    private const string BigeyeProviderEnvVar = "BABALLONIA_BIGEYE_PROVIDER";
    private const string BigeyeBackendSettingKey = "AppSettings_BigeyeWindowsBackend";

    public SingleCameraSourceFactory(ILogger<SingleCameraSourceFactory> logger, ILoggerFactory loggerFactory, IDeviceEnumerator deviceEnumerator, IPlatformConnector platformConnector, ILocalSettingsService localSettingsService)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _deviceEnumerator = deviceEnumerator;
        _platformConnector = platformConnector;
        _localSettingsService = localSettingsService;
    }

    public SingleCameraSource? Create(string address, string providerName)
    {
        ICaptureFactory? provider;
        if (!string.IsNullOrEmpty(providerName))
        {
            provider = _platformConnector.GetCaptureFactories()
                .FirstOrDefault(factory => factory.GetProviderName() == providerName && factory.CanConnect(address));
            if(provider == null)
                throw new ArgumentNullException($"No provider \"{provider}\" is not compatible with \"{address}\"");

        }
        else
        {
            provider = _platformConnector.GetCaptureFactories().First(factory => factory.CanConnect(address));
            if(provider == null)
                throw new ArgumentNullException($"No suitable provider for {address} found");
        }

        var capture = provider.Create(address);

        return new SingleCameraSource(_loggerFactory.CreateLogger<SingleCameraSource>(), capture, address);
    }

    public Task<SingleCameraSource?> CreateStart(string address)
    {
        var camera = address;
        _deviceEnumerator.Cameras ??= _deviceEnumerator.UpdateCameras();
        if (_deviceEnumerator.Cameras.TryGetValue(camera, out var mappedAddress))
        {
            camera = mappedAddress;
        }

        var provider = ResolvePreferredProvider(address, camera) ??
                       _platformConnector.GetCaptureFactories().FirstOrDefault(factory => factory.CanConnect(camera));
        if (provider == null)
            throw new ArgumentNullException($"No provider for {address} not found");

        _logger.LogInformation("Using capture provider {ProviderName} for {CameraAddress}", provider.GetProviderName(), address);
        return CreateStart(address, provider.GetProviderName());
    }

    private ICaptureFactory? ResolvePreferredProvider(string address, string mappedCamera)
    {
        if (!OperatingSystem.IsWindows() ||
            !address.Contains("Bigeye", StringComparison.OrdinalIgnoreCase))
            return null;

        var forcedProvider = Environment.GetEnvironmentVariable(BigeyeProviderEnvVar)?.Trim();
        if (string.IsNullOrWhiteSpace(forcedProvider))
            return null;

        var provider = _platformConnector.GetCaptureFactories()
            .FirstOrDefault(factory =>
                string.Equals(factory.GetProviderName(), forcedProvider, StringComparison.OrdinalIgnoreCase) &&
                (factory.CanConnect(address) || factory.CanConnect(mappedCamera)));

        if (provider != null)
            _logger.LogInformation("Applying Bigeye provider override: {ProviderName}", provider.GetProviderName());

        return provider;
    }

    public Task<SingleCameraSource?> CreateStart(string address, string providerName)
    {
        var camera = address;

        _deviceEnumerator.Cameras ??= _deviceEnumerator.UpdateCameras();
        if (_deviceEnumerator.Cameras.TryGetValue(camera, out var mappedAddress))
        {
            camera = mappedAddress;
        }

        if (OperatingSystem.IsWindows() &&
            providerName == "OpenCvCapture" &&
            int.TryParse(camera, out _) &&
            address.Contains("Bigeye", StringComparison.OrdinalIgnoreCase))
        {
            var selectedBackend = GetBigeyeBackendPreference();
            camera = selectedBackend switch
            {
                "dshow" => $"dshow:{camera}",
                _ => $"msmf:{camera}"
            };
            _logger.LogInformation("Applying Bigeye Windows capture hint: {HintedCamera}", camera);
        }

        return Task.Run<SingleCameraSource?>(() =>
        {
            var cameraSource = Create(camera, providerName);
            if (cameraSource == null)
                return null;

            if (!cameraSource.Start())
            {
                _logger.LogError("Could not initialize {}", address);
                return null;
            }

            Stopwatch sw = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(13);
            while (sw.Elapsed < timeout)
            {
                using var testFrame = cameraSource.GetFrame();
                if (testFrame != null)
                    return cameraSource;

                Thread.Sleep(10);
            }

            _logger.LogError("No data was received from {}, with {}, closing... Maybe the camera is opened somewhere else?", address, providerName);
            cameraSource.Dispose();
            return null;
        });
    }

    private string GetBigeyeBackendPreference()
    {
        var selectedBackend = Environment.GetEnvironmentVariable(BigeyeBackendEnvVar)?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(selectedBackend))
            return selectedBackend;

        var savedMode = _localSettingsService.ReadSetting<string>(BigeyeBackendSettingKey, "Stable");
        return savedMode?.Trim().ToLowerInvariant() switch
        {
            "low latency" => "dshow",
            "dshow" => "dshow",
            _ => "msmf"
        };
    }
}
