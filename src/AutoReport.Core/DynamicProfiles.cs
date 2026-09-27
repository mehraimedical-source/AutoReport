using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoReport.Core;

// Vendor/model detection is a hint. Unknown devices deliberately continue through the generic path.
public sealed record DeviceIdentity(string Manufacturer, string Model, double Confidence, string Evidence);
public sealed record MeasurementValue(string CanonicalKey, decimal Value, string? Unit, double Confidence, string RawLabel, string SourceImage);
public sealed record DeviceProfile(string Id, string Manufacturer, IReadOnlyList<string> ModelPatterns, IReadOnlyDictionary<string, IReadOnlyList<string>> FieldAliases);

public sealed class DeviceDetector
{
    public DeviceIdentity Detect(string text, IReadOnlyDictionary<string,string>? metadata = null)
    {
        if (metadata is not null) {
            metadata.TryGetValue("Manufacturer", out var maker);
            metadata.TryGetValue("ManufacturerModelName", out var model);
            if (!string.IsNullOrWhiteSpace(maker) || !string.IsNullOrWhiteSpace(model))
                return new(maker ?? "unknown", model ?? "unknown", .99, "metadata");
        }
        var s=text.ToUpperInvariant();
        var samsung=Regex.Match(s, @"\b(WS\s?80A?|H60|HS\s?[0-9]{2})\b");
        if (samsung.Success) return new("Samsung", samsung.Value.Replace(" ",""), .92, "image-text");
        var ge=Regex.Match(s, @"\b(VOLUSON\s*)?(730|E8|E10|S8|S10)\b");
        if (ge.Success) return new("GE", ge.Value.Trim(), .88, "image-text");
        return new("unknown","unknown",0,"generic-fallback");
    }
}

public sealed class MeasurementNormalizer
{
    private readonly Dictionary<string,string> aliases = new(StringComparer.OrdinalIgnoreCase);
    public MeasurementNormalizer(IEnumerable<DeviceProfile> profiles) {
        foreach(var p in profiles) foreach(var f in p.FieldAliases)
            foreach(var a in f.Value.Append(f.Key)) aliases[Norm(a)]=f.Key;
    }
    public string Canonicalize(string label) => aliases.TryGetValue(Norm(label),out var k) ? k : Norm(label);
    private static string Norm(string v)=>Regex.Replace(v.Trim().ToLowerInvariant(),@"[^a-z0-9]+","_").Trim('_');
}

public sealed class DynamicMeasurementExtractor
{
    private static readonly Regex Pair=new(@"(?im)^\s*(?<label>[A-Za-z][A-Za-z0-9 ./_-]{0,30}?)\s*[:=]?\s+(?<value>-?\d+(?:[.,]\d+)?)\s*(?<unit>mmHg|mm|cm/s(?:\^?2|²)?|cm|ms|bpm|%)?\s*$",RegexOptions.Compiled);
    private readonly MeasurementNormalizer normalizer;
    public DynamicMeasurementExtractor(MeasurementNormalizer normalizer)=>this.normalizer=normalizer;

    public IReadOnlyList<MeasurementValue> Extract(string image,string text,double confidence=.8) {
        var values=new List<MeasurementValue>();
        foreach(Match m in Pair.Matches(text)) {
            if(!decimal.TryParse(m.Groups["value"].Value.Replace(',','.'),NumberStyles.Number,CultureInfo.InvariantCulture,out var v)) continue;
            var label=m.Groups["label"].Value.Trim(); var unit=m.Groups["unit"].Value;
            values.Add(new(normalizer.Canonicalize(label),v,string.IsNullOrWhiteSpace(unit)?null:unit,confidence,label,image));
        }
        return values;
    }
}

public sealed class TemplateFieldMapper
{
    private static readonly Regex Token=new(@"\{\{\s*(?<key>[a-zA-Z0-9_.-]+)\s*\}\}",RegexOptions.Compiled);
    public string Fill(string template,IEnumerable<MeasurementValue> measurements,double minimumConfidence=.70) {
        var map=measurements.Where(x=>x.Confidence>=minimumConfidence)
            .GroupBy(x=>x.CanonicalKey,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g=>g.Key,g=>g.OrderByDescending(x=>x.Confidence).First(),StringComparer.OrdinalIgnoreCase);
        return Token.Replace(template,m=>map.TryGetValue(m.Groups["key"].Value,out var x)?x.Value.ToString(CultureInfo.InvariantCulture):m.Value);
    }
}
