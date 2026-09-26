using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoReport
{
    // Implement this interface to add another local vision engine or a DICOM/SR reader.
    public interface IImageTextReader
    {
        Task<IReadOnlyList<SourcePage>> ReadAsync(string imagePath, CancellationToken cancellationToken);
    }

    public sealed class RuleExtractor
    {
        private readonly ExtractionProfile profile;
        private readonly List<KeyValuePair<ExtractionRule, Regex>> rules;
        public RuleExtractor(ExtractionProfile profile)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            rules = profile.Rules.Select(rule =>
            {
                if (string.IsNullOrWhiteSpace(rule.Key)) throw new ArgumentException("Rule key is required.");
                var regex = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
                if (!regex.GetGroupNames().Contains("value")) throw new ArgumentException("Rule needs a value group: " + rule.Key);
                return new KeyValuePair<ExtractionRule, Regex>(rule, regex);
            }).ToList();
        }

        public List<Observation> Extract(SourcePage page)
        {
            var result = new List<Observation>();
            for (int i = 0; i < page.Lines.Count; i++)
            {
                var line = page.Lines[i];
                var normalized = NormalizeDigits(line.Text ?? "");
                foreach (var pair in rules)
                {
                    foreach (Match match in pair.Value.Matches(normalized))
                    {
                        var value = match.Groups["value"].Value.Trim();
                        if (value.Length == 0) continue;
                        var unit = match.Groups["unit"].Value.Trim().ToLowerInvariant();
                        var observation = new Observation { Key = pair.Key.Key, Value = value, Unit = unit,
                            SourceId = page.Id, LineIndex = i, Evidence = line.Text, Confidence = line.Confidence };
                        if (line.Confidence < profile.LowConfidenceThreshold) observation.Warnings.Add("LowOcrConfidence");
                        if (!string.IsNullOrEmpty(pair.Key.RequiredUnit) && !Units.Compatible(unit, pair.Key.RequiredUnit))
                            observation.Warnings.Add("MissingOrUnexpectedUnit");
                        // Every candidate remains unreviewed, regardless of OCR confidence.
                        result.Add(observation);
                    }
                }
            }
            return result;
        }

        public static string NormalizeDigits(string input)
        {
            const string fa = "۰۱۲۳۴۵۶۷۸۹";
            const string ar = "٠١٢٣٤٥٦٧٨٩";
            for (int i = 0; i < 10; i++) input = input.Replace(fa[i], (char)('0' + i)).Replace(ar[i], (char)('0' + i));
            return input.Replace('٫', '.');
        }
    }

    public static class Units
    {
        public static bool Compatible(string a, string b)
        {
            decimal ignored;
            return TryConvert(1, a, b, out ignored);
        }
        public static bool TryConvert(decimal value, string from, string to, out decimal converted)
        {
            converted = value;
            from = (from ?? "").Trim().ToLowerInvariant(); to = (to ?? "").Trim().ToLowerInvariant();
            if (from == to) return true;
            if (from == "mm" && to == "cm") { converted = value / 10; return true; }
            if (from == "cm" && to == "mm") { converted = value * 10; return true; }
            if (from == "g" && to == "kg") { converted = value / 1000; return true; }
            if (from == "kg" && to == "g") { converted = value * 1000; return true; }
            return false;
        }
    }

    public sealed class AutoReportEngine
    {
        private readonly IImageTextReader reader;
        private readonly RuleExtractor extractor;
        public AutoReportEngine(IImageTextReader reader, ExtractionProfile profile)
        { this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); extractor = new RuleExtractor(profile); }

        // One call is ONE examination, never a batch of unrelated patients.
        public async Task<Study> ExtractAsync(string studyId, IEnumerable<string> images, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(studyId)) throw new ArgumentException("Explicit study ID is required.");
            var study = new Study { StudyId = studyId };
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in images)
            {
                token.ThrowIfCancellationRequested();
                string hash;
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!hashes.Add(hash)) { study.Warnings.Add("Duplicate image skipped: " + Path.GetFileName(path)); continue; }
                var pages = await reader.ReadAsync(path, token).ConfigureAwait(false);
                foreach (var page in pages)
                {
                    page.Sha256 = hash; page.FileName = Path.GetFileName(path);
                    study.Sources.Add(page); study.Observations.AddRange(extractor.Extract(page));
                }
            }
            if (study.Sources.Count == 0) throw new InvalidOperationException("No source images were processed.");
            if (study.Observations.Count == 0) study.Warnings.Add("No recognized fields. Inspect raw text and extend the profile.");
            foreach (var field in study.Observations.GroupBy(x => x.Key))
                if (field.Select(x => x.Value + "|" + x.Unit).Distinct().Count() > 1)
                    study.Warnings.Add("Conflicting candidates require review: " + field.Key);
            study.Warnings.Add("Confirm all input images belong to this examination; image filenames are not patient identity.");
            return study;
        }
    }
}
