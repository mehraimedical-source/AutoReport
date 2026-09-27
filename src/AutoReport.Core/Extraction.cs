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

        private static double CenterX(TextLine x) { return x.X + x.Width / 2.0; }
        private static double CenterY(TextLine x) { return x.Y + x.Height / 2.0; }

        public static List<StructuredField> Extract(SourcePage page)
        {
            var output = new List<StructuredField>();
            var cells = page.Lines.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
            if (cells.Count == 0) return output;

            // Discover table headers from the image itself. No medical section names or invented
            // column names are used. A header band is a horizontal cluster containing >=2 short,
            // non-unit labels (numeric labels such as 1/2/3 are allowed).
            var rows = new List<List<TextLine>>();
            foreach (var cell in cells.OrderBy(CenterY))
            {
                List<TextLine> row = null;
                double best = double.MaxValue;
                foreach (var candidate in rows)
                {
                    double cy = candidate.Average(CenterY);
                    double h = Math.Max(4.0, candidate.Average(x => (double)Math.Max(1, x.Height)) * 0.65);
                    double d = Math.Abs(CenterY(cell) - cy);
                    if (d <= h && d < best) { row = candidate; best = d; }
                }
                if (row == null) { row = new List<TextLine>(); rows.Add(row); }
                row.Add(cell);
            }
            foreach (var row in rows) row.Sort((a,b) => a.X.CompareTo(b.X));

            string section = "";
            List<TextLine> activeHeaders = null;
            double headerY = -1;

            foreach (var row in rows.OrderBy(r => r.Average(CenterY)))
            {
                var nonUnits = row.Where(x => !UnitNames.Contains((x.Text ?? "").Trim())).ToList();
                bool headerBand = nonUnits.Count >= 2 && nonUnits.Count(x => (x.Text ?? "").Trim().Length <= 12) >= 2 &&
                    nonUnits.Count(x => IsValue(x.Text)) <= Math.Max(0, nonUnits.Count - 1);

                if (headerBand)
                {
                    activeHeaders = nonUnits;
                    headerY = row.Average(CenterY);
                    continue;
                }

                // A one-cell textual row immediately before a table is a section title exactly as
                // OCR saw it. It is never renamed/classified (e.g. no inferred "Doppler").
                if (row.Count == 1 && !IsValue(row[0].Text) && !UnitNames.Contains((row[0].Text ?? "").Trim()))
                {
                    var nextHeader = rows.Where(r => r.Average(CenterY) > row.Average(CenterY))
                        .OrderBy(r => r.Average(CenterY)).FirstOrDefault();
                    if (nextHeader != null && nextHeader.Count >= 2)
                        section = (row[0].Text ?? "").Trim();
                    continue;
                }

                if (activeHeaders == null || row.Average(CenterY) <= headerY) continue;
                var label = row.FirstOrDefault(x => !IsValue(x.Text) && !UnitNames.Contains((x.Text ?? "").Trim()));
                var valueCells = row.Where(x => IsValue(x.Text)).ToList();
                if (label == null || valueCells.Count == 0) continue;

                var named = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach (var valueCell in valueCells)
                {
                    var nearest = activeHeaders.OrderBy(h => Math.Abs(CenterX(h) - CenterX(valueCell))).FirstOrDefault();
                    if (nearest == null) continue;
                    string column = (nearest.Text ?? "").Trim();
                    if (column.Length == 0 || named.ContainsKey(column)) continue;
                    named[column] = RuleExtractor.NormalizeDigits((valueCell.Text ?? "").Trim());
                }
                string unit = row.Select(x => (x.Text ?? "").Trim()).FirstOrDefault(x => UnitNames.Contains(x)) ?? "";
                if (named.Count > 0)
                    output.Add(new StructuredField { Section = section, Subsection = "", Type = "TableRow",
                        Key = (label.Text ?? "").Trim(), Cells = named, Values = named.Values.ToList(), Unit = unit,
                        RawText = string.Join(" ", row.Select(x => x.Text)), Confidence = row.Min(x => x.Confidence) });
            }
            return output.GroupBy(x => (x.Section ?? "") + "\u001f" + (x.Key ?? ""), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
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
