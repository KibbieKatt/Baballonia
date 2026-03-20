using Baballonia.SDK;
using Microsoft.Extensions.Logging;

namespace Baballonia.NamedPipeCapture;

/// <summary>
/// Factory that creates <see cref="NamedPipeCapture"/> instances.
/// Handles addresses of the form <c>pipe://&lt;pipeName&gt;</c>.
/// </summary>
public sealed class NamedPipeCaptureFactory(ILogger<NamedPipeCapture> logger) : ICaptureFactory
{
    private const string Scheme = "pipe://";

    public Capture Create(string address)
    {
        if (!CanConnect(address))
            throw new ArgumentException($"Address '{address}' is not a valid pipe:// URI.", nameof(address));

        string pipeName = address[Scheme.Length..];
        return new NamedPipeCapture(pipeName, logger);
    }

    public bool CanConnect(string address) =>
        !string.IsNullOrWhiteSpace(address) &&
        address.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) &&
        address.Length > Scheme.Length;

    public string GetProviderName() => "NamedPipe";
}