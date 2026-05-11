namespace Baballonia.Services.Inference.Filters;

public static class FilterFactory
{
    public static IFilter? Create(string mode, int expressionCount, float oneEuroMinCutoff, float oneEuroSpeedCutoff)
    {
        var normalized = EyeSmoothingModes.Normalize(mode);
        var initial = new float[expressionCount];

        return normalized switch
        {
            nameof(EyeSmoothingModes.Off) => null,
            _ when normalized == EyeSmoothingModes.Off => null,
            _ when normalized == EyeSmoothingModes.OneEuroCurrent => new OneEuroFilter(initial, oneEuroMinCutoff, oneEuroSpeedCutoff),
            _ when normalized == EyeSmoothingModes.OneEuroAuto => new OneEuroFilter(initial, 2.0f, 1.0f),
            _ when normalized == EyeSmoothingModes.AlphaBeta => new AlphaBetaFilter(initial),
            _ when normalized == EyeSmoothingModes.KalmanCv => new ConstantVelocityKalmanFilter(initial),
            _ when normalized == EyeSmoothingModes.AdaptiveKalman => new AdaptiveKalmanFilter(initial),
            _ when normalized == EyeSmoothingModes.StateAdaptiveFir => new StateAdaptiveFirFilter(initial),
            _ when normalized == EyeSmoothingModes.SavitzkyGolayFir => new SavitzkyGolayFirFilter(initial),
            _ when normalized == EyeSmoothingModes.NEuroPredictor => new NEuroPredictorFilter(initial),
            _ => new OneEuroFilter(initial, oneEuroMinCutoff, oneEuroSpeedCutoff)
        };
    }
}
