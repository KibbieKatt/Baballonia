namespace Baballonia.Services.Inference.Filters;

public static class EyeSmoothingModes
{
    public const string Off = "Off";
    public const string OneEuroCurrent = "OneEuro Current";
    public const string OneEuroAuto = "OneEuro Auto";
    public const string AlphaBeta = "Alpha-Beta";
    public const string KalmanCv = "Kalman CV";
    public const string AdaptiveKalman = "Adaptive Kalman";
    public const string StateAdaptiveFir = "State-Adaptive FIR";
    public const string SavitzkyGolayFir = "Savitzky-Golay FIR";
    public const string NEuroPredictor = "N-Euro Predictor";

    public static readonly string[] All =
    [
        Off,
        OneEuroCurrent,
        OneEuroAuto,
        AlphaBeta,
        KalmanCv,
        AdaptiveKalman,
        StateAdaptiveFir,
        SavitzkyGolayFir,
        NEuroPredictor
    ];

    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => OneEuroCurrent,
            "off" => Off,
            "oneeuro current" or "one euro current" or "oneeurocurrent" or "one_euro_current" => OneEuroCurrent,
            "oneeuro auto" or "one euro auto" or "oneeuroauto" or "one_euro_auto" => OneEuroAuto,
            "alpha-beta" or "alphabeta" or "alpha_beta" => AlphaBeta,
            "kalman cv" or "kalmancv" or "kalman_cv" => KalmanCv,
            "adaptive kalman" or "adaptivekalman" or "adaptive_kalman" => AdaptiveKalman,
            "state-adaptive fir" or "state adaptive fir" or "stateadaptivefir" or "state_adaptive_fir" => StateAdaptiveFir,
            "savitzky-golay fir" or "savitzky golay fir" or "savitzkygolayfir" or "savitzky_golay_fir" or "sgolay" => SavitzkyGolayFir,
            "n-euro predictor" or "n euro predictor" or "n-euro" or "neuro predictor" or "n_euro_predictor" => NEuroPredictor,
            _ => OneEuroCurrent
        };
    }
}
