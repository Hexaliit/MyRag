using OpenCvSharp;

namespace Mostlylucid.DocSummarizer.Images.Services.Preprocessing;

/// <summary>
///     Noise reduction methods for document images.
/// </summary>
public class NoiseReducer
{
    public enum DenoiseMethod
    {
        Gaussian,
        Bilateral,
        NonLocalMeans,
        Morphological
    }

    public Mat Denoise(Mat gray, DenoiseMethod method)
    {
        return method switch
        {
            DenoiseMethod.Gaussian => GaussianDenoise(gray),
            DenoiseMethod.Bilateral => BilateralDenoise(gray),
            DenoiseMethod.NonLocalMeans => NlmDenoise(gray),
            DenoiseMethod.Morphological => MorphologicalDenoise(gray),
            _ => gray.Clone()
        };
    }

    /// <summary>
    ///     Simple Gaussian blur for minor noise.
    /// </summary>
    private static Mat GaussianDenoise(Mat gray, int kernelSize = 3)
    {
        var result = new Mat();
        Cv2.GaussianBlur(gray, result, new Size(kernelSize, kernelSize), 0);
        return result;
    }

    /// <summary>
    ///     Bilateral filter - preserves edges while smoothing.
    /// </summary>
    private static Mat BilateralDenoise(Mat gray, int d = 9,
        double sigmaColor = 75, double sigmaSpace = 75)
    {
        var result = new Mat();
        Cv2.BilateralFilter(gray, result, d, sigmaColor, sigmaSpace);
        return result;
    }

    /// <summary>
    ///     Non-local means - best quality but slower.
    /// </summary>
    private static Mat NlmDenoise(Mat gray, float h = 10,
        int templateWindowSize = 7, int searchWindowSize = 21)
    {
        var result = new Mat();
        Cv2.FastNlMeansDenoising(gray, result, h,
            templateWindowSize, searchWindowSize);
        return result;
    }

    /// <summary>
    ///     Morphological denoising for binary images.
    /// </summary>
    private static Mat MorphologicalDenoise(Mat binary, int noiseSize = 2)
    {
        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect, new Size(noiseSize, noiseSize));

        var result = new Mat();
        Cv2.MorphologyEx(binary, result, MorphTypes.Open, kernel);
        Cv2.MorphologyEx(result, result, MorphTypes.Close, kernel);

        return result;
    }
}