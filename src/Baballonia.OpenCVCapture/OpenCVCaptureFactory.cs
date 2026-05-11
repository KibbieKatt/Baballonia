using Baballonia.SDK;
using Microsoft.Extensions.Logging;

namespace Baballonia.OpenCVCapture;

public class OpenCvCaptureFactory(ILoggerFactory loggerFactory) : ICaptureFactory
{
    public Capture Create(string address)
    {
        return new OpenCvCapture(address, loggerFactory.CreateLogger<OpenCvCapture>());
    }

    public bool CanConnect(string address)
    {
        if (address.StartsWith("msmf:", StringComparison.OrdinalIgnoreCase) ||
            address.StartsWith("dshow:", StringComparison.OrdinalIgnoreCase))
        {
            address = address[(address.IndexOf(':') + 1)..];
        }

        var lowered = address.ToLower();
        var serial = lowered.StartsWith("com") ||
                     lowered.StartsWith("/dev/tty") ||
                     lowered.StartsWith("/dev/cu") ||
                     lowered.StartsWith("/dev/ttyacm");;
        if (serial) return false;

        return lowered.StartsWith("/dev/video") ||
               lowered.EndsWith("appsink") ||
               address == "HTC Multimedia Camera" ||
               int.TryParse(address, out _) ||
               Uri.TryCreate(address, UriKind.Absolute, out _);
    }

    public string GetProviderName()
    {
        return nameof(OpenCvCapture);
    }
}
