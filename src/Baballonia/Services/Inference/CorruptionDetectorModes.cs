using System.Collections.Generic;

namespace Baballonia.Services.Inference;

public static class CorruptionDetectorModes
{
    public const string StockRow = "Stock Row";
    public const string HistogramFlash = "Histogram Flash";
    public const string HybridRuntimeV2 = "Hybrid Runtime V2";

    public static IReadOnlyList<string> All { get; } =
    [
        StockRow,
        HistogramFlash,
        HybridRuntimeV2
    ];

    public static string Normalize(string? value)
    {
        return value switch
        {
            StockRow or "stock_row" => StockRow,
            HistogramFlash or "histogram_flash" => HistogramFlash,
            HybridRuntimeV2 or "hybrid_runtime_v2" => HybridRuntimeV2,
            _ => StockRow
        };
    }
}
