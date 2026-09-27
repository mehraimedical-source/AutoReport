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

        private sealed class ExtractionLine
        {
            public string Text;
            public string Evidence;
            public double Confidence;
            public int LineIndex;
        }

        private static List<ExtractionLine> BuildExtractionLines(SourcePage page)
        {
            var indexed = page.Lines.Select((line, index) => new { Line = line, Index = index }).ToList();
            // PaddleOCR returns table cells as separate blocks. Rebuild visual rows from bounding boxes
            // before applying regex rules so "RI" and "0.64" can be matched on the same row.
            if (indexed.Count < 2 || indexed.Count(x => x.Line.Height > 0 || x.Line.Width > 0) < 2)
                return indexed.Select(x => new ExtractionLine { Text = x.Line.Text ?? "", Evidence = x.Line.Text,
                    Confidence = x.Line.Confidence, LineIndex = x.Index }).ToList();

            var rows = new List<List<Tuple<TextLine, int>>>();
            foreach (var item in indexed.OrderBy(x => x.Line.Y + x.Line.Height / 2.0))
            {
                double center = item.Line.Y + item.Line.Height / 2.0;
                List<Tuple<TextLine, int>> best = null;
                double bestDistance = double.MaxValue;
                foreach (var row in rows)
                {
                    double rowCenter = row.Average(x => (double)x.Item1.Y + (double)x.Item1.Height / 2.0);
                    double rowHeight = Math.Max(1.0, row.Average(x => (double)Math.Max(1, x.Item1.Height)));
                    double tolerance = Math.Max(4.0, Math.Max(rowHeight, Math.Max(1, item.Line.Height)) * 0.60);
                    double distance = Math.Abs(center - rowCenter);
                    if (distance <= tolerance && distance < bestDistance) { best = row; bestDistance = distance; }
                }
                if (best == null) { best = new List<Tuple<TextLine, int>>(); rows.Add(best); }
                best.Add(Tuple.Create(item.Line, item.Index));
            }

            var result = rows.Select(row =>
            {
                var ordered = row.OrderBy(x => x.Item1.X).ToList();
                string text = string.Join(" ", ordered.Select(x => x.Item1.Text ?? "").Where(x => !string.IsNullOrWhiteSpace(x)));
                return new ExtractionLine { Text = text, Evidence = text,
                    Confidence = ordered.Count == 0 ? 0 : ordered.Min(x => x.Item1.Confidence),
                    LineIndex = ordered.Count == 0 ? 0 : ordered.Min(x => x.Item2) };
            }).Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();

            // Some PaddleOCR builds expose each table cell as an independent block and their
            // bounding boxes are not reliable enough to reconstruct rows. Add short sequential
            // windows as a fallback: e.g. "RI" + "0.64" becomes "RI 0.64".
            // Rules are anchored to known field labels, so unrelated OCR text is ignored.
            for (int i = 0; i < indexed.Count; i++)
            {
                var parts = new List<string>();
                double confidence = 100.0;
                for (int j = i; j < indexed.Count && j < i + 4; j++)
                {
                    string part = indexed[j].Line.Text ?? "";
                    if (string.IsNullOrWhiteSpace(part)) continue;
                    parts.Add(part.Trim());
                    confidence = Math.Min(confidence, indexed[j].Line.Confidence);
                    if (parts.Count >= 2)
                    {
                        string text = string.Join(" ", parts);
                        result.Add(new ExtractionLine { Text = text, Evidence = text,
                            Confidence = confidence, LineIndex = indexed[i].Index });
                    }
                }
            }
            return result;
        }

        public List<Observation> Extract(SourcePage page)
        {
            var result = new List<Observation>();
            foreach (var line in BuildExtractionLines(page))
            {
                var normalized = NormalizeDigits(line.Text ?? "");
                foreach (var pair in rules)
                {
                    foreach (Match match in pair.Value.Matches(normalized))
                    {
                        var value = match.Groups["value"].Value.Trim();
                        if (value.Length == 0) continue;
                        var unit = match.Groups["unit"].Value.Trim().ToLowerInvariant();
                        var observation = new Observation { Key = pair.Key.Key, Value = value, Unit = unit,
                            SourceId = page.Id, LineIndex = line.LineIndex, Evidence = line.Evidence, Confidence = line.Confidence };
                        if (line.Confidence < profile.LowConfidenceThreshold) observation.Warnings.Add("LowOcrConfidence");
                        if (!string.IsNullOrEmpty(pair.Key.RequiredUnit) && !Units.Compatible(unit, pair.Key.RequiredUnit))
                            observation.Warnings.Add("MissingOrUnexpectedUnit");
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

    public static class LayoutStructureExtractor
    {
        private static readonly Regex Number = new Regex(@"^[+\\-]?\\d+(?:[.,]\\d+)?(?:[%*])?$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> UnitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "mm", "cm", "m", "ms", "s", "g", "kg", "bpm", "hz", "mhz", "mmhg", "cm/s", "m/s", "cm/s²", "cm/s2", "%" };

        private static bool IsValue(string s)
        { return Number.IsMatch(RuleExtractor.NormalizeDigits((s ?? "").Trim())); }

        private static bool IsKnownSection(string text)
        {
            string t = (text ?? "").Trim().ToLowerInvariant();
            return t == "ob" || t.Contains("biometr") || t.Contains("doppler") || t.Contains("fetal") ||
                t.Contains("anatom") || t.Contains("amniotic") || t.Contains("placenta") ||
                t.Contains("cervix") || t.Contains("measurement");
        }

        private static bool IsSubsection(string text)
        {
            string t = (text ?? "").Trim().ToLowerInvariant();
            return t.Contains("uterine") || t.Contains("umbilical") || t.Contains("cerebral") ||
                t.Contains("ductus") || t.Contains("artery") || t.Contains("vein");
        }

        public static List<StructuredField> Extract(SourcePage page)
        {
            var output = new List<StructuredField>();
            string section = "General", subsection = "";
            var cells = page.Lines.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();

            for (int i = 0; i < cells.Count; i++)
            {
                string text = (cells[i].Text ?? "").Trim();
                if (IsKnownSection(text) && !IsSubsection(text))
                {
                    section = text; subsection = "";
                    continue; // headings provide context; they are not data rows
                }
                if (IsSubsection(text))
                {
                    subsection = text;
                    continue; // e.g. Rt. Uterine A
                }

                // A data row starts with a textual label and must be followed immediately by a value.
                // This prevents table headers (Last/1/2/3/Pctl.) and neighboring labels becoming fields.
                if (IsValue(text) || UnitNames.Contains(text) || text.Length > 50) continue;
                if (i + 1 >= cells.Count || !IsValue(cells[i + 1].Text)) continue;

                var values = new List<string>();
                string unit = "";
                int j = i + 1;
                while (j < cells.Count && values.Count < 5)
                {
                    string next = (cells[j].Text ?? "").Trim();
                    if (IsValue(next)) { values.Add(RuleExtractor.NormalizeDigits(next)); j++; continue; }
                    if (UnitNames.Contains(next)) unit = next;
                    break;
                }

                output.Add(new StructuredField {
                    Section = section, Subsection = subsection, Type = "Row", Key = text,
                    Values = values, Unit = unit,
                    RawText = text + " " + string.Join(" ", values) + (unit.Length == 0 ? "" : " " + unit),
                    Confidence = cells.Skip(i).Take(Math.Max(1, j - i)).Min(x => x.Confidence)
                });
                i = Math.Max(i, j - 1);
            }
            return output.GroupBy(x => (x.Section ?? "") + "\u001f" + (x.Subsection ?? "") + "\u001f" + (x.Key ?? ""),
                StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
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
                    study.StructuredFields.AddRange(LayoutStructureExtractor.Extract(page));
                }
            }
            if (study.Sources.Count == 0) throw new InvalidOperationException("No source images were processed.");
            if (study.Observations.Count == 0 && study.StructuredFields.Count == 0)
                study.Warnings.Add("No structured fields were recognized. Inspect raw OCR text.");
            foreach (var field in study.Observations.GroupBy(x => x.Key))
                if (field.Select(x => x.Value + "|" + x.Unit).Distinct().Count() > 1)
                    study.Warnings.Add("Conflicting candidates require review: " + field.Key);
            study.Warnings.Add("Confirm all input images belong to this examination; image filenames are not patient identity.");
            return study;
        }
    }
}
