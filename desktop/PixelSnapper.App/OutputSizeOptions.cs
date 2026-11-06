namespace PixelSnapper;

public sealed record OutputSizeOptions(int Width, int Height, bool KeepAspectRatio = true, bool UseWidthAsAnchor = true)
{
    public const int MaxDimension = 8192;
    public const long MaxPixels = 16_777_216;

    public void Validate()
    {
        ValidateDimensions(Width, Height, checkPixelCount: !KeepAspectRatio);
    }

    public (int Width, int Height) Resolve(int sourceWidth, int sourceHeight)
    {
        Validate();
        if (sourceWidth < 1 || sourceHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "원본 이미지의 가로·세로 크기를 확인할 수 없습니다.");

        var width = Width;
        var height = Height;
        if (KeepAspectRatio)
        {
            if (UseWidthAsAnchor)
                height = RoundDimension(Width * (double)sourceHeight / sourceWidth);
            else
                width = RoundDimension(Height * (double)sourceWidth / sourceHeight);
        }
        ValidateDimensions(width, height);
        return (width, height);
    }

    internal static void ValidateDimensions(int width, int height, bool checkPixelCount = true)
    {
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), $"출력 가로·세로는 각각 1~{MaxDimension:N0}px로 지정해 주세요.");
        if (checkPixelCount && (long)width * height > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "출력 크기는 총 16,777,216픽셀 이하로 지정해 주세요. (예: 4,096 × 4,096)");
    }

    private static int RoundDimension(double value)
    {
        var rounded = Math.Max(1, Math.Round(value, MidpointRounding.AwayFromZero));
        if (rounded > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(value), $"원본 비율을 유지하면 한 변이 {MaxDimension:N0}px를 넘습니다. 입력한 크기를 줄이거나 비율 잠금을 해제해 주세요.");
        return (int)rounded;
    }
}
