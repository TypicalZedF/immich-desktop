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
        using var classifier = new NsfwClassifier(useGpu: false);
        foreach (var (sky, hill) in new[] { (0xFF7AB8E8u, 0xFF2E7D32u), (0xFFF2A65Au, 0xFF6D4C41u), (0xFF9FA8DAu, 0xFF37474Fu) })
        {
            using var photo = Landscape(sky, hill);
            var scores = classifier.Classify(ToImage(photo));
            Assert.True(scores.Explicit < 0.3f, $"explicit {scores.Explicit:0.00}");
            Assert.InRange(scores.Drawing + scores.Hentai + scores.Neutral + scores.Porn + scores.Sexy, 0.99f, 1.01f);
        }
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
    public void Telemetry_is_off_and_model_fingerprints_are_known()
    {
        Assert.True(OnDeviceModels.TelemetryDisabled);
        var files = OnDeviceModels.Files();
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        // The NSFWJS conversion in this repo (tools/models/nsfwjs_to_onnx.py) produces exactly this file.
        Assert.Equal("6fa22cc593949f9f47757f0b91310113f068ea91a9e701df82c8155d8a8c7c94", files.Single(f => f.File == NsfwClassifier.ModelFile).Sha256);
    }

    [Fact]
    public void A_landscape_has_no_text()
    {
        using var photo = Landscape(0xFF7AB8E8, 0xFF2E7D32);
        using var reader = new PhotoTextReader(useGpu: false);
        Assert.Equal(SensitiveKind.None, SensitiveText.Find(reader.Read(ToImage(photo)).Select(l => l.Text)));
    }
}
