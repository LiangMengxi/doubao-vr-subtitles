using DoubaoVR.Desktop;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static class BackgroundChecks
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "DoubaoVR-ImageChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(root);
            var source = await folder.CreateFileAsync("original.png");
            using (var output = await source.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                var pixels = new byte[3200 * 1600 * 4];
                for (int offset = 0; offset < pixels.Length; offset += 4) { pixels[offset] = 180; pixels[offset + 1] = 80; pixels[offset + 2] = 40; pixels[offset + 3] = 255; }
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 3200, 1600, 96, 96, pixels);
                await encoder.FlushAsync();
            }
            byte[] original = await File.ReadAllBytesAsync(source.Path);
            string name = await BackgroundImageService.ImportAsync(source.Path, root);
            if (!BackgroundFiles.IsOwnedName(name)) throw new Exception("Image import did not use an app-owned name.");
            var imported = await StorageFile.GetFileFromPathAsync(BackgroundFiles.Resolve(root, name));
            using (var input = await imported.OpenReadAsync())
            {
                var decoder = await BitmapDecoder.CreateAsync(input);
                if (decoder.PixelWidth != 1600 || decoder.PixelHeight != 800) throw new Exception("Actual image decoding did not obey the memory bound.");
            }
            if (!original.SequenceEqual(await File.ReadAllBytesAsync(source.Path))) throw new Exception("The selected original image was modified.");
            string invalid = Path.Combine(root, "invalid.png");
            await File.WriteAllTextAsync(invalid, "not a bitmap");
            bool rejected = false;
            try { await BackgroundImageService.ImportAsync(invalid, root); }
            catch (Exception) { rejected = true; }
            if (!rejected || Directory.EnumerateFiles(Path.Combine(root, "backgrounds"), "*.pending").Any()) throw new Exception("Invalid image cleanup failed.");
            Console.WriteLine("PASS: actual PNG import, bounded decode/re-encode, unchanged original and corrupt image rejection.");
        }
        finally
        {
            string images = Path.Combine(root, "backgrounds");
            if (Directory.Exists(images)) { foreach (string path in Directory.EnumerateFiles(images)) File.Delete(path); Directory.Delete(images); }
            foreach (string path in Directory.EnumerateFiles(root)) File.Delete(path);
            Directory.Delete(root);
        }
    }
}
