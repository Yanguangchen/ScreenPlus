// Generates src/ScreenPlus/Assets/AppIcon.ico from the shared artwork in Resources/AppIcon.png.
// Usage (from the windows folder):  dotnet run scripts/build-icon.cs
#:package SkiaSharp@4.152.1
#:package SkiaSharp.NativeAssets.Linux.NoDependencies@4.152.1

using SkiaSharp;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "..", ".."));
var source = Path.Combine(root, "Resources", "AppIcon.png");
var output = Path.Combine(root, "windows", "src", "ScreenPlus", "Assets", "AppIcon.ico");

using var artwork = SKImage.FromEncodedData(source) ?? throw new FileNotFoundException(source);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var images = sizes.Select(size =>
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))!;
    surface.Canvas.Clear(SKColors.Transparent);
    surface.Canvas.DrawImage(artwork, new SKRect(0, 0, size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));
    using var snapshot = surface.Snapshot();
    return snapshot.Encode(SKEncodedImageFormat.Png, 100).ToArray();
}).ToArray();

// ICO: header, one directory entry per image, then the PNG data (supported since Windows Vista).
using var stream = File.Create(output);
using var writer = new BinaryWriter(stream);
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)sizes.Length);
var offset = 6 + 16 * sizes.Length;
for (var i = 0; i < sizes.Length; i++)
{
    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
    writer.Write((byte)0);  // palette
    writer.Write((byte)0);
    writer.Write((ushort)1);  // planes
    writer.Write((ushort)32);  // bits per pixel
    writer.Write(images[i].Length);
    writer.Write(offset);
    offset += images[i].Length;
}
foreach (var image in images) writer.Write(image);
Console.WriteLine($"Wrote {output} ({stream.Length / 1024} KB)");
