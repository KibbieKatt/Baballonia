using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Runtime.InteropServices;

namespace Baballonia.Services.Inference;

public class MatToFloatTensorConverter : IImageConverter
{
    public void Convert(Mat input, DenseTensor<float> outTensor)
    {
        Mat? floatMat = null;
        Mat? resizedMat = null;
        Mat? continuousMat = null;

        try
        {
            var targetHeight = outTensor.Dimensions[2];
            var targetWidth = outTensor.Dimensions[3];

            Mat resultMat = input;
            if (input.Type() != MatType.CV_32FC(input.Channels()))
            {
                floatMat = new Mat();
                input.ConvertTo(floatMat, MatType.CV_32FC(input.Channels()), 1f / 255f);
                resultMat = floatMat;
            }

            if (resultMat.Rows != targetHeight || resultMat.Cols != targetWidth)
            {
                resizedMat = new Mat();
                Cv2.Resize(resultMat, resizedMat, new Size(targetWidth, targetHeight));
                resultMat = resizedMat;
            }

            if (!resultMat.IsContinuous())
            {
                continuousMat = resultMat.Clone();
                resultMat = continuousMat;
            }

            int height = resultMat.Rows;
            int width = resultMat.Cols;
            int channels = resultMat.Channels();

            IntPtr matPtr = resultMat.Data;

            int totalElements = height * width * channels;

            float[] buffer = new float[totalElements];
            Marshal.Copy(matPtr, buffer, 0, totalElements);

            // Convert HWC to NCHW
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int pixelIndex = (y * width + x) * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        outTensor[0, c, y, x] = buffer[pixelIndex + c];
                    }
                }
            }
        }
        finally
        {
            continuousMat?.Dispose();
            resizedMat?.Dispose();
            floatMat?.Dispose();
        }
    }
}
