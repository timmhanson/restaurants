using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// One thing placed on a card by the card designer: words, a fill-in line, a rule, a box, a picture or the QR code.
/// Positions and sizes are inches from the top-left corner of the finished (trimmed) 6 x 4 inch card.
/// </summary>
public class CardElement
{
    public string Id { get; set; } = "";
    /// <summary>text, fill (a label followed by a value on a line), line, box, image or qr.</summary>
    public string Type { get; set; } = CardElements.Text;
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    /// <summary>Height, for pictures, boxes and the QR code. Words take the height they need.</summary>
    public double H { get; set; }
    /// <summary>The words (or a fill-in line's label). {restaurant}, {offer} and the other fields are filled in for each card.</summary>
    public string? Text { get; set; }
    /// <summary>For a fill-in line: what is printed on the line, usually one field such as {offer}.</summary>
    public string? Value { get; set; }
    /// <summary>sans, geometric or narrow.</summary>
    public string Font { get; set; } = "sans";
    /// <summary>Points.</summary>
    public double Size { get; set; } = 9.5;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    /// <summary>For a fill-in line: the line runs under the label as well as under what is filled in.</summary>
    public bool LineUnderLabel { get; set; }
    /// <summary>left, center or right: how words line up, or where a picture sits inside its outline.</summary>
    public string Align { get; set; } = "left";
    /// <summary>A colour such as #2b4a8b, or "ink" for the card's ink colour.</summary>
    public string Color { get; set; } = CardElements.Ink;
    public double LineHeight { get; set; } = 1.2;
    /// <summary>Points, for rules, box edges and the line under a fill-in value.</summary>
    public double Thickness { get; set; } = 0.75;
    /// <summary>For a picture: which uploaded picture it shows (backlogo, footer, logo, photo, picture, or one of its own such as el-ab12cd).</summary>
    public string? Image { get; set; }
}

/// <summary>What is filled in on each card, by the name used in braces in the designer.</summary>
public record CardMerge(Dictionary<string, string> Values)
{
    public static readonly (string Key, string Label)[] Fields =
    [
        ("restaurant", "Restaurant name"), ("restaurantState", "Restaurant name and state"), ("street", "Restaurant street"),
        ("cityStateZip", "Restaurant city, state and zip"), ("website", "Web address"), ("offer", "The offer"), ("dateIssued", "Date issued"),
        ("goodUntil", "Good until"), ("months", "How long it is good for"), ("authorizedBy", "Authorized by"), ("guest", "Guest name"),
        ("legal", "Small print"), ("note", "Note from the restaurant")
    ];

    public static CardMerge For(GuestComment c, Restaurant r, int validityMonths, CardLook look)
    {
        static string Day(DateOnly d) => d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        var state = (r.State ?? "").Trim();
        var cityLine = string.Join(", ", new[] { r.City, string.Join(' ', new[] { r.State, r.Zip }.Where(s => !string.IsNullOrWhiteSpace(s))) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new CardMerge(new Dictionary<string, string>
        {
            ["restaurant"] = r.Name,
            // "Culver's of Joliet, IL", as on the printed card, unless the name already ends with the state.
            ["restaurantState"] = state.Length > 0 && !r.Name.TrimEnd().EndsWith(state, StringComparison.OrdinalIgnoreCase) ? $"{r.Name}, {state}" : r.Name,
            ["street"] = (r.StreetAddress ?? "").Trim(),
            ["cityStateZip"] = cityLine,
            ["website"] = (look.Website ?? "").Trim(),
            ["offer"] = string.IsNullOrWhiteSpace(c.CouponOffer) ? "a treat on us" : c.CouponOffer.Trim(),
            ["dateIssued"] = Day(c.DateIssued),
            ["goodUntil"] = Day(c.ExpiresOn),
            ["months"] = $"{validityMonths} month{(validityMonths == 1 ? "" : "s")}",
            ["authorizedBy"] = (c.AuthorizedBy ?? "").Trim(),
            ["guest"] = $"{c.GuestFirstName} {c.GuestLastName}".Trim(),
            ["legal"] = string.IsNullOrWhiteSpace(look.LegalText) ? CardBacks.DefaultLegal : look.LegalText.Trim(),
            ["note"] = (look.BackMessage ?? "").Trim()
        });
    }

    /// <summary>
    /// The words with their fields filled in. A line that held only fields and comes out empty is dropped, so an
    /// address block does not leave a gap for a web address that was never set.
    /// </summary>
    public string Fill(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = new List<string>();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var hadField = false;
            var filled = Regex.Replace(line, @"\{([A-Za-z]+)\}", m =>
            {
                if (!Values.TryGetValue(m.Groups[1].Value, out var v)) return m.Value;
                hadField = true;
                return v;
            });
            if (hadField && string.IsNullOrWhiteSpace(filled)) continue;
            lines.Add(filled);
        }
        return string.Join('\n', lines);
    }
}

public static class CardElements
{
    public const string Text = "text", FillIn = "fill", Line = "line", Box = "box", Image = "image", Qr = "qr";
    public static readonly string[] Types = [Text, FillIn, Line, Box, Image, Qr];
    public const string Ink = "ink";
    public const string QrCaption = "Staff: scan to check";
    public const int Max = 40;
    public const double CardWidth = 6, CardHeight = 4, Bleed = 0.125;
    /// <summary>Where the mail house prints on the back, in inches from the trimmed card's top-left corner: the address and postage from this far across, and the barcode strip below and to the right of this point.</summary>
    public const double AddressFromX = 3.645, BarcodeFromX = 1.375, BarcodeFromY = 3.395;

    /// <summary>The typefaces a card can use. Each is served by the site, so the preview and the printed card match.</summary>
    public static readonly Dictionary<string, (string Family, string Fallback)> Fonts = new()
    {
        ["sans"] = ("Arimo", "Arial, Helvetica, sans-serif"),
        ["geometric"] = ("Montserrat", "'Helvetica Neue', Arial, sans-serif"),
        ["narrow"] = ("Roboto Condensed", "'Arial Narrow', Arial, sans-serif")
    };

    private static readonly (string Family, string File, int Weight, bool Italic)[] FontFiles =
    [
        ("Arimo", "arimo-latin-400-normal", 400, false), ("Arimo", "arimo-latin-700-normal", 700, false),
        ("Arimo", "arimo-latin-400-italic", 400, true), ("Arimo", "arimo-latin-700-italic", 700, true),
        ("Montserrat", "montserrat-latin-500-normal", 400, false), ("Montserrat", "montserrat-latin-700-normal", 700, false),
        ("Montserrat", "montserrat-latin-500-italic", 400, true),
        ("Roboto Condensed", "roboto-condensed-latin-400-normal", 400, false), ("Roboto Condensed", "roboto-condensed-latin-700-normal", 700, false)
    ];

    public static readonly string[] FontFileNames = FontFiles.Select(f => f.File + ".woff2").ToArray();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?> FontData = new();

    /// <summary>One of the typeface files built into the site, or null for any other name.</summary>
    public static byte[]? FontFile(string name) => !FontFileNames.Contains(name) ? null : FontData.GetOrAdd(name, n =>
    {
        using var s = typeof(CardElements).Assembly.GetManifestResourceStream("CardFonts." + n);
        if (s is null) return null;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    /// <summary>
    /// The @font-face rules for a card, with each typeface it uses carried inside the page itself, so the printed card
    /// matches the preview without the mail house having to download anything.
    /// </summary>
    public static string FontCss(IEnumerable<CardElement> elements)
    {
        // What each item needs: its family, and whether bold or italic. A fill-in line prints its value bold, or italic for a signature.
        var need = new HashSet<(string Family, int Weight, bool Italic)>();
        foreach (var e in elements.Where(e => e.Type is Text or FillIn))
        {
            var family = Fonts.TryGetValue(e.Font, out var f) ? f.Family : Fonts["sans"].Family;
            need.Add((family, e.Bold ? 700 : 400, e.Type == Text && e.Italic));
            if (e.Type == FillIn) need.Add((family, e.Italic ? 400 : 700, e.Italic));
        }
        var files = new HashSet<string>();
        foreach (var (family, weight, italic) in need)
        {
            // The nearest file the family has: the upright one stands in where it has no italic.
            var pick = FontFiles.Where(f => f.Family == family && f.Weight == weight).OrderBy(f => f.Italic == italic ? 0 : 1).Select(f => f.File).FirstOrDefault();
            if (pick is not null) files.Add(pick);
        }
        var sb = new StringBuilder();
        foreach (var f in FontFiles.Where(f => files.Contains(f.File)))
            if (FontFile(f.File + ".woff2") is { } bytes)
                sb.Append($"@font-face{{font-family:'{f.Family}';font-style:{(f.Italic ? "italic" : "normal")};font-weight:{f.Weight};src:url(data:font/woff2;base64,{Convert.ToBase64String(bytes)}) format('woff2')}}");
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<CardElement>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<CardElement>>(json, Json); }
        catch (JsonException) { return null; }
    }

    public static string? Serialize(List<CardElement>? elements) => elements is null ? null : JsonSerializer.Serialize(elements, Json);

    public static bool IsImageKind(string? kind) => kind is not null && (CardImageKinds.All.Contains(kind) || Regex.IsMatch(kind, "^el-[a-z0-9]{4,12}$"));

    /// <summary>
    /// Checks a layout from the designer and tidies it: sizes kept on the card, unknown settings put back to safe
    /// ones. Returns what is wrong in plain words, or null when it can be saved.
    /// </summary>
    public static string? Clean(List<CardElement> elements)
    {
        if (elements.Count > Max) return $"A card can hold up to {Max} items. Remove some and save again.";
        var ids = new HashSet<string>();
        foreach (var e in elements)
        {
            if (!Types.Contains(e.Type)) return "One of the items on the card is of a kind this site does not know.";
            if (string.IsNullOrWhiteSpace(e.Id) || !Regex.IsMatch(e.Id, "^[a-z0-9]{1,12}$") || !ids.Add(e.Id)) e.Id = NewId(ids);
            if ((e.Text?.Length ?? 0) > 600) return "Keep each block of words to 600 characters.";
            if ((e.Value?.Length ?? 0) > 120) return "Keep what goes on a fill-in line to 120 characters.";
            foreach (var n in new[] { e.X, e.Y, e.W, e.H, e.Size, e.LineHeight, e.Thickness })
                if (double.IsNaN(n) || double.IsInfinity(n)) return "One of the sizes or positions is not a number.";
            e.W = Math.Round(Math.Clamp(e.W, 0.05, CardWidth), 3);
            e.H = Math.Round(Math.Clamp(e.H, 0, CardHeight), 3);
            e.X = Math.Round(Math.Clamp(e.X, 0, CardWidth - 0.05), 3);
            e.Y = Math.Round(Math.Clamp(e.Y, 0, CardHeight - 0.05), 3);
            e.Size = Math.Round(Math.Clamp(e.Size, 4, 60), 1);
            e.LineHeight = Math.Round(Math.Clamp(e.LineHeight, 0.9, 2.5), 2);
            e.Thickness = Math.Round(Math.Clamp(e.Thickness, 0.25, 6), 2);
            if (!Fonts.ContainsKey(e.Font)) e.Font = "sans";
            if (e.Align is not ("left" or "center" or "right")) e.Align = "left";
            if (e.Color != Ink && !Regex.IsMatch(e.Color ?? "", "^#[0-9a-fA-F]{6}$")) e.Color = Ink;
            if (e.Type == Image && !IsImageKind(e.Image)) return "One of the pictures on the card has no picture chosen. Upload one for it or remove it.";
            if (e.Type != Image) e.Image = null;
            if (e.Type is Line or Box or Image or Qr) { e.Text = null; e.Value = null; }
            if (e.Type != FillIn) e.LineUnderLabel = false;
            if (e.Type == Qr) e.H = e.W = Math.Clamp(e.W, 0.5, 2);
        }
        return null;
    }

    private static string NewId(HashSet<string> taken)
    {
        string id;
        do { id = Guid.NewGuid().ToString("N")[..8]; } while (!taken.Add(id));
        return id;
    }

    private static CardElement El(string id, string type, double x, double y, double w, double h = 0) => new() { Id = id, Type = type, X = x, Y = y, W = w, H = h };

    /// <summary>
    /// The classic coupon card back, laid out from the restaurants' own printed cards: logo and address across the
    /// top, the three fill-in lines, the small print, and the web address with a small picture in the bottom corner.
    /// Everything stays left of the mail house's address area; the QR code sits beside the signature line.
    /// </summary>
    public static List<CardElement> ClassicBack() =>
    [
        // As wide as the logo on the printed card, and tall enough for the script logo's own shape (about 2.14 to 1).
        new() { Id = "logo", Type = Image, X = 0.29, Y = 0.253, W = 1.44, H = 0.674, Image = CardImageKinds.BackLogo },
        new() { Id = "address", Type = Text, X = 1.915, Y = 0.305, W = 1.7, Font = "geometric", Size = 11.1, Bold = true, LineHeight = 0.94, Text = "{restaurantState}\n{street}\n{cityStateZip}\n{website}" },
        new() { Id = "note", Type = Text, X = 0.285, Y = 1.04, W = 3.3, Size = 8, Italic = true, Text = "{note}" },
        new() { Id = "enjoy", Type = FillIn, X = 0.285, Y = 1.32, W = 3.3, Size = 8.3, Thickness = 0.5, Text = "Please enjoy:", Value = "{offer}" },
        new() { Id = "enjoy2", Type = Text, X = 0.28, Y = 1.484, W = 3.3, Size = 8.3, Text = "the next time you visit us at {restaurant}." },
        // On the printed card these two lines run under their labels. The date's line stops short to leave room for the QR code.
        new() { Id = "issued", Type = FillIn, X = 0.28, Y = 1.801, W = 2.62, Size = 8.3, Thickness = 0.5, LineUnderLabel = true, Text = "Date Issued:", Value = "{dateIssued}" },
        new() { Id = "issued2", Type = Text, X = 0.28, Y = 1.964, W = 2.72, Size = 8.3, Text = "This coupon is good for {months} from date of issue." },
        new() { Id = "sign", Type = FillIn, X = 0.275, Y = 2.314, W = 2.975, Size = 8.3, Thickness = 0.5, LineUnderLabel = true, Italic = true, Text = "Authorized Signature:", Value = "{authorizedBy}" },
        new() { Id = "qr", Type = Qr, X = 2.99, Y = 1.7, W = 0.6, H = 0.6 },
        new() { Id = "qrnote", Type = Text, X = 2.94, Y = 2.31, W = 0.7, Size = 4.5, Align = "center", Text = QrCaption },
        new() { Id = "legal", Type = Text, X = 1.24, Y = 2.752, W = 2.3, Font = "narrow", Size = 7, Bold = true, LineHeight = 1.12, Text = "{legal}" },
        new() { Id = "icons", Type = Image, X = 0.317, Y = 3.073, W = 0.693, H = 0.153, Image = CardImageKinds.Footer },
        new() { Id = "site", Type = Text, X = 0.31, Y = 3.316, W = 1.0, Font = "geometric", Size = 8, Bold = true, Text = "{website}" }
    ];

    /// <summary>A starting point for a front laid out in the designer: the logo, a headline, a message and the restaurant's name.</summary>
    public static List<CardElement> StarterFront() =>
    [
        new() { Id = "logo", Type = Image, X = 2.0, Y = 0.45, W = 2.0, H = 0.9, Align = "center", Image = CardImageKinds.Logo },
        new() { Id = "headline", Type = Text, X = 0.5, Y = 1.6, W = 5.0, Font = "geometric", Size = 28, Bold = true, Align = "center", LineHeight = 1.1, Text = "Thank you!" },
        new() { Id = "message", Type = Text, X = 0.75, Y = 2.25, W = 4.5, Size = 13, Align = "center", LineHeight = 1.3, Text = "We appreciate you letting us know about your visit." },
        new() { Id = "name", Type = Text, X = 0.5, Y = 3.35, W = 5.0, Font = "geometric", Size = 10, Align = "center", Text = "{restaurant}" }
    ];

    /// <summary>
    /// One side of the card as the page the mail house prints: each item at its place, 6.25 x 4.25 inches with an
    /// eighth of an inch of bleed all round. Every word is escaped; nothing typed in the designer can become markup.
    /// </summary>
    /// <param name="ink">What "ink" means on this side.</param>
    /// <param name="pictures">The address of each uploaded picture, by kind.</param>
    /// <param name="qr">The card's QR code as a picture address. Without one, the code and its caption are left off.</param>
    public static string Render(IEnumerable<CardElement> elements, CardMerge merge, string ink, string background, IReadOnlyDictionary<string, string> pictures, string? qr)
    {
        var list = elements.ToList();
        static string In(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "in";
        static string Pt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "pt";
        static string H(string s) => WebUtility.HtmlEncode(s);
        string Colour(CardElement e) => e.Color == Ink ? ink : e.Color;
        string Place(CardElement e) => $"left:{In(e.X + Bleed)};top:{In(e.Y + Bleed)};width:{In(e.W)}";
        string Type(CardElement e)
        {
            var (family, fallback) = Fonts.TryGetValue(e.Font, out var f) ? f : Fonts["sans"];
            return $"font-family:'{family}',{fallback};font-size:{Pt(e.Size)};line-height:{e.LineHeight.ToString("0.##", CultureInfo.InvariantCulture)};color:{Colour(e)}";
        }

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><style>");
        sb.Append(FontCss(list));
        sb.Append($"@page{{size:6.25in 4.25in;margin:0}}html,body{{margin:0;padding:0;width:6.25in;height:4.25in;overflow:hidden;position:relative;background:{background}}}");
        sb.Append(".e{position:absolute;box-sizing:border-box;margin:0}.t{white-space:pre-wrap;overflow-wrap:break-word}");
        sb.Append(".f{display:flex;align-items:flex-end}.f .l{flex:none;padding-right:.05in}.f .v{flex:1;min-width:0;padding:0 .04in;white-space:nowrap;overflow:hidden;color:#1d1d1f;font-size:1.1em}");
        sb.Append("img.e{object-fit:contain}");
        sb.Append("</style></head><body>");
        foreach (var e in list)
        {
            switch (e.Type)
            {
                case Text:
                    var words = merge.Fill(e.Text);
                    if (words.Length == 0 || (words == QrCaption && string.IsNullOrWhiteSpace(qr))) break;
                    sb.Append($"<div class=\"e t\" style=\"{Place(e)};{Type(e)};text-align:{e.Align};font-weight:{(e.Bold ? 700 : 400)};font-style:{(e.Italic ? "italic" : "normal")}\">{H(words)}</div>");
                    break;
                case FillIn:
                    // The label is plain; what is filled in stands out in bold, or in italics for a signature.
                    var rule = $"border-bottom:{Pt(e.Thickness)} solid {Colour(e)}";
                    sb.Append($"<div class=\"e f\" style=\"{Place(e)};{Type(e)}{(e.LineUnderLabel ? $";padding-bottom:.01in;{rule}" : "")}\"><span class=\"l\" style=\"font-weight:{(e.Bold ? 700 : 400)}\">{H(merge.Fill(e.Text))}</span>");
                    sb.Append($"<span class=\"v\" style=\"{(e.LineUnderLabel ? "" : rule + ";")}font-weight:{(e.Italic ? 400 : 700)};font-style:{(e.Italic ? "italic" : "normal")}\">{H(merge.Fill(e.Value))}&nbsp;</span></div>");
                    break;
                case Line:
                    sb.Append($"<div class=\"e\" style=\"{Place(e)};border-top:{Pt(e.Thickness)} solid {Colour(e)}\"></div>");
                    break;
                case Box:
                    sb.Append($"<div class=\"e\" style=\"{Place(e)};height:{In(Math.Max(e.H, 0.05))};border:{Pt(e.Thickness)} solid {Colour(e)}\"></div>");
                    break;
                case Image:
                    if (e.Image is not null && pictures.TryGetValue(e.Image, out var url))
                        sb.Append($"<img class=\"e\" style=\"{Place(e)};height:{In(Math.Max(e.H, 0.05))};object-position:{e.Align} center\" src=\"{H(url)}\" alt=\"\">");
                    break;
                case Qr:
                    if (!string.IsNullOrWhiteSpace(qr))
                        sb.Append($"<img class=\"e\" style=\"{Place(e)};height:{In(e.W)}\" src=\"{H(qr)}\" alt=\"\">");
                    break;
            }
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
