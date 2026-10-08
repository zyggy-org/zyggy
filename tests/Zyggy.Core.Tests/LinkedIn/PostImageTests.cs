using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// The image loader (plan 36b D2, D3): a regular file inside <c>image.dir</c>, no symlink, within <c>image.max_bytes</c>, a PNG, JPEG or GIF
/// with readable dimensions under LinkedIn's pixel limit, whose SHA-256 is the approved one — read once, the same bytes returned.
/// </summary>
public sealed class PostImageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public PostImageTests()
    {
        Directory.CreateDirectory(MediaDir);
    }

    private string MediaDir => Path.Combine(_root, "media");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("png", "image/png", 1200, 1200)]
    [InlineData("gif", "image/gif", 640, 480)]
    [InlineData("jpeg", "image/jpeg", 1920, 1080)]
    public void Load_SupportedFormat_ReturnsSameBytesTypeAndDimensions(string kind, string mediaType, int width, int height)
    {
        // Arrange
        var bytes = Bytes(kind, width, height);
        var path = Write("picture." + kind, bytes);

        // Act
        var load = PostImage.Load(path, ImageBytes.Sha256(bytes), MediaDir, 10 * 1024 * 1024);

        // Assert
        load.Refusal.Should().BeNull();
        load.Image!.Bytes.ToArray().Should().Equal(bytes);
        load.Image.MediaType.Should().Be(mediaType);
        load.Image.Width.Should().Be(width);
        load.Image.Height.Should().Be(height);
        load.Image.Sha256.Should().Be(ImageBytes.Sha256(bytes));
    }

    [Fact]
    public void Load_OutsideMediaDir_Refused()
    {
        // Arrange
        var bytes = ImageBytes.Png(10, 10);
        var outside = Path.Combine(_root, "elsewhere.png");
        File.WriteAllBytes(outside, bytes);

        // Act + Assert
        PostImage.Load(outside, ImageBytes.Sha256(bytes), MediaDir, 1024).Refusal.Should().Be("image outside image.dir");
        PostImage.Load(Path.Combine(MediaDir, "..", "elsewhere.png"), ImageBytes.Sha256(bytes), MediaDir, 1024).Refusal.Should().Be("image outside image.dir");
        PostImage.Load("relative.png", ImageBytes.Sha256(bytes), MediaDir, 1024).Refusal.Should().Be("image outside image.dir");
        PostImage.Load(MediaDir + "-sibling" + Path.DirectorySeparatorChar + "x.png", ImageBytes.Sha256(bytes), MediaDir, 1024)
            .Refusal.Should().Be("image outside image.dir");
    }

    [Fact]
    public void Load_DirectoryOrMissing_NotARegularFile()
    {
        // Arrange
        var directory = Path.Combine(MediaDir, "folder.png");
        Directory.CreateDirectory(directory);

        // Act + Assert
        PostImage.Load(directory, new string('a', 64), MediaDir, 1024).Refusal.Should().Be("image not a regular file");
        PostImage.Load(Path.Combine(MediaDir, "missing.png"), new string('a', 64), MediaDir, 1024).Refusal.Should().Be("image not a regular file");
    }

    [Fact]
    public void Load_Symlink_NotARegularFile()
    {
        // Arrange
        var bytes = ImageBytes.Png(10, 10);
        var target = Write("target.png", bytes);
        var link = Path.Combine(MediaDir, "link.png");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links need a privilege on this machine");
        }

        // Act + Assert
        PostImage.Load(link, ImageBytes.Sha256(bytes), MediaDir, 1024).Refusal.Should().Be("image not a regular file");
    }

    [Fact]
    public void Load_TooLarge_RefusedWithSizes()
    {
        // Arrange
        var bytes = ImageBytes.Png(10, 10, pad: 2000);
        var path = Write("big.png", bytes);

        // Act
        var load = PostImage.Load(path, ImageBytes.Sha256(bytes), MediaDir, 1024);

        // Assert
        load.Refusal.Should().Be($"image too large ({bytes.Length} > 1024)");
    }

    [Fact]
    public void Load_TextFileNamedPng_NotPngJpegOrGif()
    {
        // Arrange
        var bytes = "just some text, not an image"u8.ToArray();
        var path = Write("fake.png", bytes);

        // Act + Assert
        PostImage.Load(path, ImageBytes.Sha256(bytes), MediaDir, 1024).Refusal.Should().Be("image not PNG, JPEG or GIF");
    }

    [Fact]
    public void Load_TruncatedJpeg_DimensionsUnreadable()
    {
        // Arrange
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A];
        var path = Write("cut.jpg", bytes);

        // Act + Assert
        PostImage.Load(path, ImageBytes.Sha256(bytes), MediaDir, 1024).Refusal.Should().Be("image dimensions unreadable");
    }

    [Fact]
    public void Load_TooManyPixels_Refused()
    {
        // Arrange: 7000 × 7000 = 49,000,000 ≥ 36,152,320
        var bytes = ImageBytes.Png(7000, 7000);
        var path = Write("huge.png", bytes);

        // Act + Assert
        PostImage.Load(path, ImageBytes.Sha256(bytes), MediaDir, 1024).Refusal.Should().Be("image too many pixels (49000000 ≥ 36152320)");
    }

    [Fact]
    public void Load_HashMismatch_Refused()
    {
        // Arrange
        var bytes = ImageBytes.Png(10, 10);
        var path = Write("shown.png", bytes);

        // Act + Assert
        PostImage.Load(path, new string('0', 64), MediaDir, 1024).Refusal.Should().Be("image hash mismatch");
    }

    private static byte[] Bytes(string kind, int width, int height) => kind switch
    {
        "png" => ImageBytes.Png(width, height),
        "gif" => ImageBytes.Gif(width, height),
        _ => ImageBytes.Jpeg(width, height),
    };

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(MediaDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
