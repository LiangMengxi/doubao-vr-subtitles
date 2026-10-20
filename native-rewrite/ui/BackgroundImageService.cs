using Windows.Graphics.Imaging;
using Windows.Storage;

namespace DoubaoVR.Desktop;

internal static class BackgroundImageService
{
    public static async Task<string> ImportAsync(string sourcePath, string dataDirectory)
    {
        var source = new FileInfo(sourcePath);
        if (source.Extension.ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp")) throw new InvalidDataException("请选择 PNG、JPG 或 BMP 图片。");
        if (source.Length > BackgroundFiles.MaximumFileBytes) throw new InvalidDataException("图片不能超过 20 MB。");
        var file = await StorageFile.GetFileFromPathAsync(source.FullName);
        using var input = await file.OpenReadAsync();
        if (input.Size > (ulong)BackgroundFiles.MaximumFileBytes) throw new InvalidDataException("图片不能超过 20 MB。");
        var decoder = await BitmapDecoder.CreateAsync(input);
        var size = BackgroundFiles.Size(decoder.PixelWidth, decoder.PixelHeight, (long)input.Size);
        var transform = new BitmapTransform { ScaledWidth = size.Width, ScaledHeight = size.Height, InterpolationMode = BitmapInterpolationMode.Fant };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        string name = Guid.NewGuid().ToString("N") + ".png";
        string destination = BackgroundFiles.Resolve(dataDirectory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".pending";
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
            var outputFile = await folder.CreateFileAsync(Path.GetFileName(temporary), CreationCollisionOption.FailIfExists);
            using (var output = await outputFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            File.Move(temporary, destination);
            return name;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
