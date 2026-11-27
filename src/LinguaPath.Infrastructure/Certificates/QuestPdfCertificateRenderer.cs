using LinguaPath.Application.Features.Progress;
using Microsoft.Extensions.Logging;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LinguaPath.Infrastructure.Certificates;

/// <summary>Certificate PDF (FR-L81): A4 landscape, template colours and images, QR code to the verification page.</summary>
internal sealed class QuestPdfCertificateRenderer(ILogger<QuestPdfCertificateRenderer> logger) : ICertificateRenderer
{
    static QuestPdfCertificateRenderer()
    {
        // Community licence: free for organisations under USD 1M annual revenue (see README).
        QuestPDF.Settings.License = LicenseType.Community;
        // Names in scripts the built-in font lacks render with a fallback glyph instead of failing the whole certificate.
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    public byte[] Render(CertificateDocument d)
    {
        var accent = d.AccentColor;
        var qr = QrPng(d.VerifyUrl);
        var logo = Usable(d.Logo, "logo");
        var signature = Usable(d.Signature, "signature");
        var background = Usable(d.Background, "background");

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(0);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontSize(12).FontColor("#1F2937"));

                if (background is not null) page.Background().Image(background).FitArea();

                page.Content().Padding(24).Border(3).BorderColor(accent).Padding(6).Border(1).BorderColor(accent).PaddingVertical(30).PaddingHorizontal(48)
                    .Column(col =>
                    {
                        col.Spacing(10);
                        if (logo is not null) col.Item().AlignCenter().Height(56).Image(logo).FitHeight();

                        col.Item().AlignCenter().Text(d.Heading).FontSize(34).Bold().FontColor(accent);
                        col.Item().AlignCenter().Text("is proudly presented to").FontSize(13).Italic().FontColor("#6B7280");
                        col.Item().AlignCenter().Text(d.LearnerName).FontSize(30).SemiBold();
                        col.Item().AlignCenter().Width(320).LineHorizontal(1).LineColor(accent);
                        col.Item().PaddingHorizontal(40).AlignCenter().Text(d.Body).FontSize(13).LineHeight(1.4f).AlignCenter();

                        col.Item().PaddingTop(8).AlignCenter().Row(row =>
                        {
                            row.AutoItem().PaddingHorizontal(18).Column(c => Fact(c, "Language", d.LanguageName));
                            row.AutoItem().PaddingHorizontal(18).Column(c => Fact(c, "Level", d.Level));
                            row.AutoItem().PaddingHorizontal(18).Column(c => Fact(c, "Grade", $"{d.Grade}% · {d.Band}"));
                            row.AutoItem().PaddingHorizontal(18).Column(c => Fact(c, "Completed", d.CompletedOn));
                        });

                        col.Item().PaddingTop(24).Row(row =>
                        {
                            row.RelativeItem().AlignBottom().Column(c =>
                            {
                                if (signature is not null) c.Item().Height(44).AlignLeft().Image(signature).FitHeight();
                                c.Item().Width(200).LineHorizontal(1).LineColor("#9CA3AF");
                                if (!string.IsNullOrWhiteSpace(d.SignatoryName)) c.Item().Text(d.SignatoryName).SemiBold();
                                if (!string.IsNullOrWhiteSpace(d.SignatoryTitle)) c.Item().Text(d.SignatoryTitle).FontSize(10).FontColor("#6B7280");
                            });
                            row.RelativeItem().AlignBottom().AlignRight().Column(c =>
                            {
                                c.Item().AlignRight().Text($"Certificate no. {d.Number}").FontSize(10).SemiBold();
                                c.Item().AlignRight().Text($"Verification code {d.VerificationCode}").FontSize(10).FontColor("#6B7280");
                                c.Item().AlignRight().Text("Scan to verify").FontSize(9).FontColor("#9CA3AF");
                            });
                            row.ConstantItem(96).PaddingLeft(12).AlignBottom().Image(qr);
                        });
                    });

                if (d.Revoked || d.Sample)
                {
                    page.Foreground().AlignCenter().AlignMiddle().Rotate(-25)
                        .Text(d.Revoked ? "REVOKED" : "SAMPLE").FontSize(110).Bold().FontColor(d.Revoked ? "#55DC2626" : "#449CA3AF");
                }
            });
        }).GeneratePdf();
    }

    private static void Fact(ColumnDescriptor c, string label, string value)
    {
        c.Item().AlignCenter().Text(label.ToUpperInvariant()).FontSize(9).FontColor("#6B7280").LetterSpacing(0.08f);
        c.Item().AlignCenter().Text(value).FontSize(14).SemiBold();
    }

    private static byte[] QrPng(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(10);
    }

    /// <summary>Uploaded images that QuestPDF can't decode are left out rather than breaking the PDF.</summary>
    private byte[]? Usable(byte[]? bytes, string what)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            Image.FromBinaryData(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Certificate {What} image is not a supported format; skipped", what);
            return null;
        }
    }
}
