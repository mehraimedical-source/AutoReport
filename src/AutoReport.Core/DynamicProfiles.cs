using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoReport.Core
{
    // C# 7.3 / .NET Framework 4.8 compatible dynamic extraction foundation.
    public sealed class DeviceIdentity
    {
        public DeviceIdentity(string manufacturer, string model, double confidence, string evidence)
        { Manufacturer = manufacturer; Model = model; Confidence = confidence; Evidence = evidence; }
        public string Manufacturer { get; private set; }
        public string Model { get; private set; }
        public double Confidence { get; private set; }
        public string Evidence { get; private set; }
    }

    public sealed class MeasurementValue
    {
        public MeasurementValue(string canonicalKey, decimal value, string unit, double confidence, string rawLabel, string sourceImage)
        { CanonicalKey=canonicalKey; Value=value; Unit=unit; Confidence=confidence; RawLabel=rawLabel; SourceImage=sourceImage; }
        public string CanonicalKey { get; private set; }
        public decimal Value { get; private set; }
        public string Unit { get; private set; }
        public double Confidence { get; private set; }
        public string RawLabel { get; private set; }
        public string SourceImage { get; private set; }
    }

    public sealed class DeviceProfile
    {
        public DeviceProfile(string id, string manufacturer, IList<string> modelPatterns, IDictionary<string, IList<string>> fieldAliases)
        { Id=id; Manufacturer=manufacturer; ModelPatterns=modelPatterns; FieldAliases=fieldAliases; }
        public string Id { get; private set; }
        public string Manufacturer { get; private set; }
        public IList<string> ModelPatterns { get; private set; }
        public IDictionary<string, IList<string>> FieldAliases { get; private set; }
    }

    public sealed class DeviceDetector
    {
        public DeviceIdentity Detect(string text, IDictionary<string,string> metadata = null)
        {
            string maker, model;
            if (metadata != null) {
                metadata.TryGetValue("Manufacturer", out maker);
                metadata.TryGetValue("ManufacturerModelName", out model);
                if (!string.IsNullOrWhiteSpace(maker) || !string.IsNullOrWhiteSpace(model))
                    return new DeviceIdentity(maker ?? "unknown", model ?? "unknown", .99, "metadata");
            }
            var s=(text ?? string.Empty).ToUpperInvariant();
            var samsung=Regex.Match(s, @"\b(WS\s?80A?|H60|HS\s?[0-9]{2})\b");
            if (samsung.Success) return new DeviceIdentity("Samsung", samsung.Value.Replace(" ",""), .92, "image-text");
            var ge=Regex.Match(s, @"\b(VOLUSON\s*)?(730|E8|E10|S8|S10)\b");
            if (ge.Success) return new DeviceIdentity("GE", ge.Value.Trim(), .88, "image-text");
            return new DeviceIdentity("unknown","unknown",0,"generic-fallback");
        }
    }

    public sealed class MeasurementNormalizer
    {
        private readonly Dictionary<string,string> aliases = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public MeasurementNormalizer(IEnumerable<DeviceProfile> profiles) {
            foreach(var p in profiles) foreach(var f in p.FieldAliases) {
                foreach(var a in f.Value) aliases[Norm(a)]=f.Key;
                aliases[Norm(f.Key)]=f.Key;
            }
        }
        public string Canonicalize(string label) {
            string key; return aliases.TryGetValue(Norm(label),out key) ? key : Norm(label);
        }
        private static string Norm(string v) { return Regex.Replace((v ?? "").Trim().ToLowerInvariant(),@"[^a-z0-9]+","_").Trim('_'); }
    }

    public sealed class DynamicMeasurementExtractor
    {
        private static readonly Regex Pair=new Regex(@"(?im)^\s*(?<label>[A-Za-z][A-Za-z0-9 ./_-]{0,30}?)\s*[:=]?\s+(?<value>-?\d+(?:[.,]\d+)?)\s*(?<unit>mmHg|mm|cm/s(?:\^?2|²)?|cm|ms|bpm|%)?\s*$",RegexOptions.Compiled);
        private readonly MeasurementNormalizer normalizer;
        public DynamicMeasurementExtractor(MeasurementNormalizer normalizer) { this.normalizer=normalizer; }

        public IList<MeasurementValue> Extract(string image,string text,double confidence=.8) {
            var values=new List<MeasurementValue>();
            foreach(Match m in Pair.Matches(text ?? string.Empty)) {
                decimal value;
                if(!decimal.TryParse(m.Groups["value"].Value.Replace(',','.'),NumberStyles.Number,CultureInfo.InvariantCulture,out value)) continue;
                var label=m.Groups["label"].Value.Trim(); var unit=m.Groups["unit"].Value;
                values.Add(new MeasurementValue(normalizer.Canonicalize(label),value,string.IsNullOrWhiteSpace(unit)?null:unit,confidence,label,image));
            }
            return values;
        }
    }

    public sealed class TemplateFieldMapper
    {
        private static readonly Regex Token=new Regex(@"\{\{\s*(?<key>[a-zA-Z0-9_.-]+)\s*\}\}",RegexOptions.Compiled);
        public string Fill(string template,IEnumerable<MeasurementValue> measurements,double minimumConfidence=.70) {
            var map=measurements.Where(x=>x.Confidence>=minimumConfidence)
                .GroupBy(x=>x.CanonicalKey,StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g=>g.Key,g=>g.OrderByDescending(x=>x.Confidence).First(),StringComparer.OrdinalIgnoreCase);
            return Token.Replace(template,m => {
                MeasurementValue value;
                return map.TryGetValue(m.Groups["key"].Value,out value) ? value.Value.ToString(CultureInfo.InvariantCulture) : m.Value;
            });
        }
    }
}
