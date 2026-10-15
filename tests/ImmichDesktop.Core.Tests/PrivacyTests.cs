using ImmichDesktop.Core.Privacy;
using SkiaSharp;

namespace ImmichDesktop.Core.Tests;

/// <summary>The on-device checks for private pictures: the text rules, and the models on drawn pictures.</summary>
public class SensitiveTextTests
{
    [Theory]
    [InlineData(SensitiveKind.PaymentCard, "VISA", "4111 1111 1111 1111", "VALID THRU 12/28", "ALEX MORGAN")]
    [InlineData(SensitiveKind.PaymentCard, "5555-5555-5555-4444")]
    [InlineData(SensitiveKind.PaymentCard, "MASTERCARD", "MEMBER SINCE 2019", "VALID THRU 04/29")]
    [InlineData(SensitiveKind.IdDocument, "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<", "L898902C36UTO7408122F1204159ZE184226B<<<<<10")]
    [InlineData(SensitiveKind.IdDocument, "DRIVER LICENSE", "DOB 01/02/1990", "EXP 01/02/2030")]
    [InlineData(SensitiveKind.IdDocument, "SOCIAL SECURITY", "123-45-6789")]
    [InlineData(SensitiveKind.Financial, "IBAN DE89 3704 0044 0532 0130 00")]
    [InlineData(SensitiveKind.Financial, "GB29 NWBK 6016 1331 9268 19")]
    [InlineData(SensitiveKind.Financial, "Account number 12345678", "Sort code 12-34-56")]
    [InlineData(SensitiveKind.Credentials, "Your recovery codes", "abcd-efgh", "ijkl-mnop")]
    [InlineData(SensitiveKind.Credentials, "Wi-Fi password: hunter22")]
    [InlineData(SensitiveKind.Medical, "PRESCRIPTION", "Amoxicillin 500 mg")]
    public void Finds_private_text(SensitiveKind expected, params string[] lines) =>
        Assert.True(SensitiveText.Find(lines).HasFlag(expected), $"expected {expected} in: {string.Join(" | ", lines)}");

    [Theory]
    [InlineData("Happy birthday Sam!", "Love, Alex")]
    [InlineData("Welcome to Lisbon", "Est. 1755")]
    [InlineData("VISA DEBIT", "************1234", "TOTAL 12.50", "TAX 1.20", "BALANCE DUE 0.00")] // a receipt
    [InlineData("Order 1234 5678 9012 3456")] // 16 digits, but not a valid card number
    [InlineData("EXIT", "SEX", "NO PARKING")]
    [InlineData("Login", "Forgot password?")]
    public void Ordinary_text_is_not_flagged(params string[] lines) =>
        Assert.Equal(SensitiveKind.None, SensitiveText.Find(lines));

    [Fact]
    public void Card_checksum() =>
        Assert.Equal([true, false], new[] { SensitiveText.Luhn("4111111111111111"), SensitiveText.Luhn("4111111111111112") });
}

/// <summary>The bundled models really run here and give sensible answers (no GPU on test machines: CPU).</summary>
public class OnDeviceModelTests
{
    static readonly string? Font = new[]
    {
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        @"C:\Windows\Fonts\arialbd.ttf",
        @"C:\Windows\Fonts\segoeuib.ttf",
    }.FirstOrDefault(File.Exists);

    static BgraImage ToImage(SKBitmap bitmap) => new(bitmap.Width, bitmap.Height, bitmap.Bytes);

    static SKBitmap Draw(int width, int height, Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        draw(canvas);
        return bitmap;
    }

    /// <summary>A landscape like the demo pictures: sky, sun, hills.</summary>
    static SKBitmap Landscape(uint sky, uint hill) => Draw(800, 600, c =>
    {
        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateLinearGradient(new(0, 0), new(0, 600), [new SKColor(sky), SKColors.White], SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, 800, 600, paint);
        paint.Shader = null;
        paint.Color = new SKColor(0xFFFFF4C2);
        c.DrawCircle(560, 180, 70, paint);
        paint.Color = new SKColor(hill);
        using var path = new SKPath();
        path.MoveTo(0, 430); path.LineTo(260, 300); path.LineTo(520, 420); path.LineTo(800, 330); path.LineTo(800, 600); path.LineTo(0, 600); path.Close();
        c.DrawPath(path, paint);
    });

    [Fact]
    public void Ordinary_photos_are_not_explicit()
    {
        using var detector = new NudeNetDetector(useGpu: false);
        foreach (var (sky, hill) in new[] { (0xFF7AB8E8u, 0xFF2E7D32u), (0xFFF2A65Au, 0xFF6D4C41u), (0xFF9FA8DAu, 0xFF37474Fu) })
        {
            using var photo = Landscape(sky, hill);
            var scores = detector.Detect(ToImage(photo));
            Assert.True(scores.Explicit < 0.2f, $"explicit {scores.Explicit:0.00}");
            Assert.True(scores.Revealing < 0.2f, $"revealing {scores.Revealing:0.00}");
        }
    }

    [Fact]
    public void Bare_skin_alone_is_not_nudity()
    {
        // A close-up of skin (the old whole-picture model called this nudity): no exposed body part.
        using var detector = new NudeNetDetector(useGpu: false);
        using var skin = Draw(800, 600, c =>
        {
            c.Clear(new SKColor(0xFFE0AC90));
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0xFFC98E73) };
            c.DrawOval(new SKRect(120, 180, 700, 420), paint);
        });
        Assert.True(detector.Detect(ToImage(skin)).Explicit < 0.2f);
    }

    [Fact]
    public void Thorough_check_head_matches_LAIONs_model()
    {
        // Reference values computed from LAION's original weights (tools/models/laion_nsfw_head.py).
        var head = NsfwHead.Load(OnDeviceModels.PathOf(ClipNsfwClassifier.HeadFile));
        var sin = Enumerable.Range(0, 512).Select(i => (float)Math.Sin(i * 0.37)).ToArray();
        Assert.Equal(0.14689847f, head.Score(sin), 4);
        var scaled = sin.Select(v => v * 7).ToArray();
        Assert.Equal(head.Score(sin), head.Score(scaled), 5); // embeddings are normalised first
    }

    [Fact]
    public void Thorough_check_runs_when_its_model_is_downloaded()
    {
        // CLIP is a 352 MB download, so this only runs where it's been fetched (set IMMICH_TEST_CLIP_MODEL to the file).
        var model = Environment.GetEnvironmentVariable("IMMICH_TEST_CLIP_MODEL");
        Assert.SkipWhen(model is null || !File.Exists(model), "CLIP model not downloaded");
        using var classifier = new ClipNsfwClassifier(model!, useGpu: false);
        foreach (var (sky, hill) in new[] { (0xFF7AB8E8u, 0xFF2E7D32u), (0xFFF2A65Au, 0xFF6D4C41u) })
        {
            using var photo = Landscape(sky, hill);
            var score = classifier.Classify(ToImage(photo));
            Assert.True(score < 0.3f, $"landscape scored {score:0.00}");
        }
    }

    [Fact]
    public async Task A_download_that_doesnt_match_is_never_used()
    {
        using var temp = new TempDir();
        byte[] published = [1, 2, 3, 4];
        var server = new BytesHandler(published);
        var target = Path.Combine(temp.Path, "models", "model.onnx");
        var wrongHash = new ModelDownload(new Uri("https://example.com/model.onnx"), target, 4, new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => wrongHash.DownloadAsync(null, TestContext.Current.CancellationToken, server));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));

        var right = wrongHash with { Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(published)) };
        await right.DownloadAsync(null, TestContext.Current.CancellationToken, server);
        Assert.True(right.IsDownloaded);
        Assert.Equal(published, File.ReadAllBytes(target));
    }

    sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public void Reads_a_payment_card()
    {
        Assert.SkipWhen(Font is null, "No font to draw the test card with");
        using var typeface = SKTypeface.FromFile(Font);
        using var card = Draw(1000, 630, c =>
        {
            c.Clear(new SKColor(0xFF1E3A8A));
            using var text = new SKPaint { IsAntialias = true, Color = SKColors.White, Typeface = typeface };
            text.TextSize = 54; c.DrawText("VISA", 790, 110, text);
            text.TextSize = 64; c.DrawText("4111 1111 1111 1111", 80, 360, text);
            text.TextSize = 34; c.DrawText("VALID THRU 12/28", 80, 450, text);
            text.TextSize = 40; c.DrawText("ALEX MORGAN", 80, 540, text);
        });
        using var reader = new PhotoTextReader(useGpu: false);

        var lines = reader.Read(ToImage(card));
        var all = string.Join(" | ", lines.Select(l => l.Text));

        Assert.Contains("4111", all);
        Assert.True(SensitiveText.Find(lines.Select(l => l.Text)).HasFlag(SensitiveKind.PaymentCard), all);
    }

    [Fact]
    public void Source_link_points_at_the_commit_this_was_built_from()
    {
        var url = AppInfo.SourceUrl("src/ImmichDesktop.Core/Privacy");
        Assert.StartsWith("https://github.com/TypicalZedF/immich-splash/tree/", url);
        Assert.Matches("/tree/([0-9a-f]{7,40}|beta)/src/ImmichDesktop.Core/Privacy$", url);
    }

    [Fact]
    public void Telemetry_is_off_and_model_fingerprints_are_known()
    {
        Assert.True(OnDeviceModels.TelemetryDisabled);
        var files = OnDeviceModels.Files();
        Assert.True(files.Count >= 4);
        Assert.All(files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        // NudeNet's published 320n.onnx (v3.4-weights release), unchanged.
        Assert.Equal("c15d8273adad2d0a92f014cc69ab2d6c311a06777a55545f2c4eb46f51911f0f", files.Single(f => f.File == NudeNetDetector.ModelFile).Sha256);
        // tools/models/laion_nsfw_head.py run on LAION's clip_autokeras_nsfw_b32.zip produces exactly this file.
        Assert.Equal("4a975cf764e8629defa1ce0929678c1a8abc0e32a3a03c7c6473b62080407b71", files.Single(f => f.File == ClipNsfwClassifier.HeadFile).Sha256);
    }

    [Fact]
    public void A_landscape_has_no_text()
    {
        using var photo = Landscape(0xFF7AB8E8, 0xFF2E7D32);
        using var reader = new PhotoTextReader(useGpu: false);
        Assert.Equal(SensitiveKind.None, SensitiveText.Find(reader.Read(ToImage(photo)).Select(l => l.Text)));
    }
}
