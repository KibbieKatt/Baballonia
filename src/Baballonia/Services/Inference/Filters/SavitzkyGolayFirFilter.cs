namespace Baballonia.Services.Inference.Filters;

public sealed class SavitzkyGolayFirFilter : IFilter
{
    private static readonly float[] Coefficients = [31f / 35f, 9f / 35f, -3f / 35f, -5f / 35f, 3f / 35f];
    private readonly float[][] _history;
    private readonly int[] _heads;
    private readonly int[] _counts;

    public SavitzkyGolayFirFilter(float[] initial)
    {
        _history = new float[initial.Length][];
        _heads = new int[initial.Length];
        _counts = new int[initial.Length];

        for (var i = 0; i < initial.Length; i++)
        {
            _history[i] = new float[Coefficients.Length];
            _history[i][0] = initial[i];
            _heads[i] = 1 % Coefficients.Length;
            _counts[i] = 1;
        }
    }

    public float[] Filter(float[] input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var history = _history[i];
            var head = _heads[i];
            history[head] = input[i];
            head = (head + 1) % Coefficients.Length;
            _heads[i] = head;
            if (_counts[i] < Coefficients.Length)
                _counts[i]++;

            float sum = 0f;
            float weight = 0f;
            for (var j = 0; j < _counts[i]; j++)
            {
                var coeff = Coefficients[j];
                var index = (head - 1 - j + Coefficients.Length) % Coefficients.Length;
                sum += history[index] * coeff;
                weight += coeff;
            }

            var newestIndex = (head - 1 + Coefficients.Length) % Coefficients.Length;
            input[i] = weight != 0f ? sum / weight : history[newestIndex];
        }

        return input;
    }
}
