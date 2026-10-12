using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.Xml;
using XiaoK.Core;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace XiaoK.Adapters.Windows;

internal static class OpenXmlPresentationGenerator
{
    private const int SlideWidth = 12_192_000;
    private const int SlideHeight = 6_858_000;

    public static bool TryCreate(string content, CancellationToken cancellationToken, out byte[] presentationBytes)
    {
        presentationBytes = [];
        if (!LocalPptxCreatePolicy.TryParseContent(content, out var title, out var slides)) return false;

        using var buffer = new MemoryStream();
        try
        {
            using (var document = PresentationDocument.Create(buffer, PresentationDocumentType.Presentation))
            {
                var presentationPart = document.AddPresentationPart();
                var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
                var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
                var themePart = masterPart.AddNewPart<ThemePart>();

                layoutPart.SlideLayout = new P.SlideLayout(
                    new P.CommonSlideData(CreateEmptyShapeTree()),
                    new P.ColorMapOverride(new D.MasterColorMapping()))
                {
                    Type = P.SlideLayoutValues.Blank,
                    Preserve = true
                };

                masterPart.SlideMaster = new P.SlideMaster(
                    new P.CommonSlideData(CreateEmptyShapeTree()),
                    CreateColorMap(),
                    new P.SlideLayoutIdList(new P.SlideLayoutId
                    {
                        Id = 2_147_483_649U,
                        RelationshipId = masterPart.GetIdOfPart(layoutPart)
                    }),
                    new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));

                themePart.Theme = CreateTheme();

                var slideIdList = new P.SlideIdList();
                var presentation = new P.Presentation(
                    new P.SlideMasterIdList(new P.SlideMasterId
                    {
                        Id = 2_147_483_648U,
                        RelationshipId = presentationPart.GetIdOfPart(masterPart)
                    }),
                    slideIdList,
                    new P.SlideSize { Cx = SlideWidth, Cy = SlideHeight, Type = P.SlideSizeValues.Screen16x9 },
                    new P.NotesSize { Cx = SlideHeight, Cy = SlideWidth },
                    new P.DefaultTextStyle());

                uint nextSlideId = 256;
                foreach (var slideContent in slides)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var slidePart = presentationPart.AddNewPart<SlidePart>();
                    slidePart.AddPart(layoutPart);
                    slidePart.Slide = CreateSlide(title, slideContent);
                    slidePart.Slide.Save();
                    slideIdList.Append(new P.SlideId
                    {
                        Id = nextSlideId++,
                        RelationshipId = presentationPart.GetIdOfPart(slidePart)
                    });
                }

                presentationPart.Presentation = presentation;
                presentationPart.Presentation.Save();
                cancellationToken.ThrowIfCancellationRequested();
                if (new OpenXmlValidator().Validate(document).Any()) return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            presentationBytes = buffer.ToArray();
            return presentationBytes.Length > 0;
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or ArgumentException or InvalidOperationException
            or IOException or XmlException)
        {
            presentationBytes = [];
            return false;
        }
    }

    private static P.Slide CreateSlide(string presentationTitle, LocalPptxCreatePolicy.SlideContent slideContent)
    {
        var tree = CreateEmptyShapeTree();
        tree.Append(CreateTextBox(2, "标题", [slideContent.Title], 32_00, 600_000, 460_000, 10_992_000, 1_200_000, bold: true));
        tree.Append(CreateTextBox(3, "要点", slideContent.Bullets.Select(bullet => "• " + bullet).ToArray(),
            20_00, 900_000, 2_000_000, 10_392_000, 4_450_000, bold: false));
        tree.Append(CreateTextBox(4, "演示文稿标题", [presentationTitle], 11_00, 600_000, 6_520_000, 10_992_000, 240_000, bold: false));
        return new P.Slide(
            new P.CommonSlideData(tree),
            new P.ColorMapOverride(new D.MasterColorMapping()));
    }

    private static P.ShapeTree CreateEmptyShapeTree() => new(
        new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new D.TransformGroup()));

    private static P.Shape CreateTextBox(uint id, string name, IReadOnlyList<string> lines, int fontSize,
        int x, int y, int width, int height, bool bold)
    {
        var textBody = new P.TextBody(
            new D.BodyProperties { Wrap = D.TextWrappingValues.Square, Anchor = D.TextAnchoringTypeValues.Top },
            new D.ListStyle());
        foreach (var line in lines)
            textBody.Append(new D.Paragraph(
            new D.Run(
                new D.RunProperties { Language = "zh-CN", FontSize = fontSize, Bold = bold },
                new D.Text(line)),
            new D.EndParagraphRunProperties { Language = "zh-CN" }));
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new D.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = width, Cy = height }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle }),
            textBody);
    }

    private static P.ColorMap CreateColorMap() => new()
    {
        Background1 = D.ColorSchemeIndexValues.Light1,
        Text1 = D.ColorSchemeIndexValues.Dark1,
        Background2 = D.ColorSchemeIndexValues.Light2,
        Text2 = D.ColorSchemeIndexValues.Dark2,
        Accent1 = D.ColorSchemeIndexValues.Accent1,
        Accent2 = D.ColorSchemeIndexValues.Accent2,
        Accent3 = D.ColorSchemeIndexValues.Accent3,
        Accent4 = D.ColorSchemeIndexValues.Accent4,
        Accent5 = D.ColorSchemeIndexValues.Accent5,
        Accent6 = D.ColorSchemeIndexValues.Accent6,
        Hyperlink = D.ColorSchemeIndexValues.Hyperlink,
        FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink
    };

    private static D.Theme CreateTheme()
    {
        var colorScheme = new D.ColorScheme(
            new D.Dark1Color(new D.RgbColorModelHex { Val = "1F1F1F" }),
            new D.Light1Color(new D.RgbColorModelHex { Val = "FFFFFF" }),
            new D.Dark2Color(new D.RgbColorModelHex { Val = "414A5A" }),
            new D.Light2Color(new D.RgbColorModelHex { Val = "F2F5F9" }),
            new D.Accent1Color(new D.RgbColorModelHex { Val = "6B8FD6" }),
            new D.Accent2Color(new D.RgbColorModelHex { Val = "63A89A" }),
            new D.Accent3Color(new D.RgbColorModelHex { Val = "F0B35A" }),
            new D.Accent4Color(new D.RgbColorModelHex { Val = "8B78BE" }),
            new D.Accent5Color(new D.RgbColorModelHex { Val = "79AFC1" }),
            new D.Accent6Color(new D.RgbColorModelHex { Val = "D77F83" }),
            new D.Hyperlink(new D.RgbColorModelHex { Val = "0563C1" }),
            new D.FollowedHyperlinkColor(new D.RgbColorModelHex { Val = "954F72" })) { Name = "XiaoK" };

        var fontScheme = new D.FontScheme(
            new D.MajorFont(new D.LatinFont { Typeface = "Aptos Display" },
                new D.EastAsianFont { Typeface = "Microsoft YaHei" }, new D.ComplexScriptFont { Typeface = "Arial" }),
            new D.MinorFont(new D.LatinFont { Typeface = "Aptos" },
                new D.EastAsianFont { Typeface = "Microsoft YaHei" }, new D.ComplexScriptFont { Typeface = "Arial" }))
        { Name = "XiaoK" };

        var formatScheme = new D.FormatScheme(
            new D.FillStyleList(SolidFill("Accent1"), SolidFill("Accent2"), SolidFill("Accent3")),
            new D.LineStyleList(CreateOutline(), CreateOutline(), CreateOutline()),
            new D.EffectStyleList(new D.EffectStyle(new D.EffectList()),
                new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList())),
            new D.BackgroundFillStyleList(SolidFill("Light1"), SolidFill("Light2"), SolidFill("Dark1")))
        { Name = "XiaoK" };

        return new D.Theme
        {
            Name = "XiaoK",
            ThemeElements = new D.ThemeElements(colorScheme, fontScheme, formatScheme),
            ObjectDefaults = new D.ObjectDefaults(),
            ExtraColorSchemeList = new D.ExtraColorSchemeList()
        };
    }

    private static D.SolidFill SolidFill(string schemeColor) => new(new D.SchemeColor
    {
        Val = schemeColor switch
        {
            "Accent1" => D.SchemeColorValues.Accent1,
            "Accent2" => D.SchemeColorValues.Accent2,
            "Accent3" => D.SchemeColorValues.Accent3,
            "Light1" => D.SchemeColorValues.Light1,
            "Light2" => D.SchemeColorValues.Light2,
            "Dark1" => D.SchemeColorValues.Dark1,
            _ => throw new ArgumentOutOfRangeException(nameof(schemeColor))
        }
    });

    private static D.Outline CreateOutline() => new(
        new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
        new D.PresetDash { Val = D.PresetLineDashValues.Solid })
    {
        Width = 9_525,
        CapType = D.LineCapValues.Flat,
        CompoundLineType = D.CompoundLineValues.Single,
        Alignment = D.PenAlignmentValues.Center
    };
}
