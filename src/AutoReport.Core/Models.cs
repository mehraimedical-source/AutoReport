using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace AutoReport
{
    public sealed class TextLine
    {
        public string Text { get; set; }
        public double Confidence { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class SourcePage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; }
        public string Sha256 { get; set; }
        public string Engine { get; set; }
        public string Pass { get; set; }
        public List<TextLine> Lines { get; set; } = new List<TextLine>();
    }

    public sealed class Observation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Key { get; set; }
        public string Value { get; set; }
        public string Unit { get; set; }
        public string SourceId { get; set; }
        public int LineIndex { get; set; }
        public string Evidence { get; set; }
        public double Confidence { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class StructuredField
    {
        public string Section { get; set; }
        public string Subsection { get; set; }
        public string Type { get; set; }
        public string Key { get; set; }
        public List<string> Values { get; set; } = new List<string>();
        public Dictionary<string, string> Cells { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; set; } = new List<string>();
        public string Unit { get; set; }
        public string RawText { get; set; }
        public double Confidence { get; set; }
    }

    public sealed class ReviewedValue
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public string Unit { get; set; }
        public string ObservationId { get; set; }
        public string Reviewer { get; set; }
        public string ReviewedUtc { get; set; }
        public string Reason { get; set; }
    }

    public sealed class Study
    {
        public string SchemaVersion { get; set; } = "1.0";
        public string StudyId { get; set; }
        public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");
        public List<SourcePage> Sources { get; set; } = new List<SourcePage>();
        public List<Observation> Observations { get; set; } = new List<Observation>();
        public List<StructuredField> StructuredFields { get; set; } = new List<StructuredField>();
        public List<ReviewedValue> ReviewHistory { get; set; } = new List<ReviewedValue>();
        public List<string> Warnings { get; set; } = new List<string>();

        public void Approve(string observationId, string reviewer)
        {
            var item = Observations.Single(x => x.Id == observationId);
            SetReviewed(item.Key, item.Value, item.Unit, reviewer, "Verified against source", item.Id);
        }

        public void SetReviewed(string key, string value, string unit, string reviewer, string reason,
            string observationId = null)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value) ||
                string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Key, value, reviewer and reason are required.");
            if (observationId != null && !Observations.Any(x => x.Id == observationId && x.Key == key))
                throw new ArgumentException("Observation does not belong to this field.");
            ReviewHistory.Add(new ReviewedValue { Key = key, Value = value, Unit = unit ?? "",
                Reviewer = reviewer, Reason = reason, ObservationId = observationId,
                ReviewedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
        }
    }

    public sealed class ExtractionRule
    {
        public string Key { get; set; }
        // Must contain named group 'value'; may contain named group 'unit'.
        public string Pattern { get; set; }
        public string RequiredUnit { get; set; }
    }

    public sealed class ExtractionProfile
    {
        public string Name { get; set; }
        public double LowConfidenceThreshold { get; set; } = 85;
        public List<ExtractionRule> Rules { get; set; } = new List<ExtractionRule>();
    }

    public sealed class TemplateBinding
    {
        public string Token { get; set; }
        public string Key { get; set; }
        public string OutputUnit { get; set; }
        public bool IncludeUnit { get; set; } = true;
        public string NumberFormat { get; set; } = "0.##";
    }

    public sealed class TemplateProfile
    {
        public string CenterId { get; set; }
        public string TemplateVersion { get; set; }
        public List<TemplateBinding> Bindings { get; set; } = new List<TemplateBinding>();
    }

    public static class JsonFile
    {
        private static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 64 }; }
        public static T Read<T>(string path) { return Serializer().Deserialize<T>(File.ReadAllText(path)); }
        public static void Write<T>(string path, T value)
        {
            if (File.Exists(path)) throw new IOException("Output already exists: " + path);
            File.WriteAllText(path, Serializer().Serialize(value), new System.Text.UTF8Encoding(false));
        }
    }
}
