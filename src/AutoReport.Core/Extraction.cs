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

    // Canonical, evidence-only ultrasound layout pipeline:
    // OCR cells -> visual rows -> table/header detection -> cell assignment -> validation.
    public static class LayoutStructureExtractor
    {
        private static readonly Regex Number = new Regex(@"^[+\\-]?\\d+(?:[.,]\\d+)?(?:[%*])?$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Units = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "mm", "cm", "m", "ms", "s", "g", "kg", "bpm", "hz", "mhz", "mmhg", "cm/s", "m/s", "cm/s²", "cm/s2", "%" };

        private sealed class VisualRow
        {
            public readonly List<TextLine> Cells = new List<TextLine>();
            public double Y { get { return Cells.Count == 0 ? 0 : Cells.Average(CenterY); } }
            public double Height { get { return Cells.Count == 0 ? 1 : Cells.Average(x => (double)Math.Max(1, x.Height)); } }
        }
        private sealed class TableContext
        {
            public string Section = "";
            public VisualRow Header;
            public double HeaderY;
        }

        private static string T(TextLine x) { return (x.Text ?? "").Trim(); }
        private static bool IsValue(TextLine x) { return IsValue(T(x)); }
        private static bool IsValue(string x) { return Number.IsMatch(RuleExtractor.NormalizeDigits((x ?? "").Trim())); }
        private static bool IsUnit(TextLine x) { return Units.Contains(T(x)); }
        private static double CenterX(TextLine x) { return x.X + x.Width / 2.0; }
        private static double CenterY(TextLine x) { return x.Y + x.Height / 2.0; }

        private static List<VisualRow> BuildRows(IEnumerable<TextLine> source)
        {
            var rows = new List<VisualRow>();
            foreach (var cell in source.Where(x => !string.IsNullOrWhiteSpace(x.Text)).OrderBy(CenterY))
            {
                VisualRow best = null; double bestDistance = double.MaxValue;
                foreach (var row in rows)
                {
                    double tolerance = Math.Max(3.0, Math.Min(row.Height, Math.Max(1, cell.Height)) * 0.45);
                    double distance = Math.Abs(CenterY(cell) - row.Y);
                    if (distance <= tolerance && distance < bestDistance) { best = row; bestDistance = distance; }
                }
                if (best == null) { best = new VisualRow(); rows.Add(best); }
                best.Cells.Add(cell);
            }
            foreach (var row in rows) row.Cells.Sort((a,b) => a.X.CompareTo(b.X));
            return rows.OrderBy(x => x.Y).ToList();
        }

        private static bool LooksLikeHeader(VisualRow row)
        {
            if (row.Cells.Count < 2) return false;
            // Header evidence comes only from OCR. It must be a horizontal band of short tokens,
            // mostly non-units, and cannot look like a normal label+measurements data row.
            var usable = row.Cells.Where(x => !IsUnit(x)).ToList();
            if (usable.Count < 2) return false;
            int textual = usable.Count(x => !IsValue(x));
            int shortTokens = usable.Count(x => T(x).Length > 0 && T(x).Length <= 12);
            bool beginsWithTextAndHasSeveralValues = !IsValue(usable[0]) && usable.Skip(1).Count(IsValue) >= 2;
            return shortTokens >= 2 && textual >= 1 && !beginsWithTextAndHasSeveralValues;
        }

        private static string FindSection(List<VisualRow> rows, int headerIndex)
        {
            // Search only nearby rows above the table. Return exact OCR text, never a medical
            // classification. Prefer a single-cell title; otherwise leave it blank.
            for (int i = headerIndex - 1; i >= 0 && i >= headerIndex - 3; i--)
            {
                var row = rows[i];
                if (row.Cells.Count != 1) continue;
                var cell = row.Cells[0];
                if (!IsValue(cell) && !IsUnit(cell) && T(cell).Length > 0 && T(cell).Length <= 80)
                    return T(cell);
            }
            return "";
        }

        private static StructuredField ParseDataRow(VisualRow row, TableContext table)
        {
            if (table == null || table.Header == null || row.Y <= table.HeaderY) return null;
            var labels = row.Cells.Where(x => !IsValue(x) && !IsUnit(x)).ToList();
            var values = row.Cells.Where(IsValue).ToList();
            if (labels.Count == 0 || values.Count == 0) return null;

            // Row label is the left-most textual cell. A unit is evidence, not a label.
            var label = labels.OrderBy(x => x.X).First();
            if (CenterX(label) >= values.Min(CenterX)) return null;

            var headers = table.Header.Cells.Where(x => !IsUnit(x)).OrderBy(CenterX).ToList();
            var mapped = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            foreach (var value in values)
            {
                var ranked = headers.Select(h => new { Header = h, Distance = Math.Abs(CenterX(h) - CenterX(value)) })
                    .OrderBy(x => x.Distance).ToList();
                if (ranked.Count == 0) { warnings.Add("NoHeaderForCell:" + T(value)); continue; }
                var nearest = ranked[0];
                // Reject implausibly distant matches instead of inventing a column assignment.
                double typicalWidth = Math.Max(12.0, headers.Average(h => (double)Math.Max(1, h.Width)));
                if (nearest.Distance > typicalWidth * 3.0) { warnings.Add("AmbiguousColumn:" + T(value)); continue; }
                string column = T(nearest.Header);
                if (mapped.ContainsKey(column)) { warnings.Add("DuplicateColumn:" + column); continue; }
                mapped[column] = RuleExtractor.NormalizeDigits(T(value));
            }
            if (mapped.Count == 0) return null;
            string unit = row.Cells.Where(IsUnit).Select(T).FirstOrDefault() ?? "";
            return new StructuredField {
                Section = table.Section, Subsection = "", Type = "TableRow", Key = T(label),
                Cells = mapped, Values = mapped.Values.ToList(), Unit = unit,
                RawText = string.Join(" ", row.Cells.Select(T)), Confidence = row.Cells.Min(x => x.Confidence),
                Warnings = warnings
            };
        }

        public static List<StructuredField> Extract(SourcePage page)
        {
            var rows = BuildRows(page.Lines);
            var output = new List<StructuredField>();
            TableContext active = null;
            for (int i = 0; i < rows.Count; i++)
            {
                if (LooksLikeHeader(rows[i]))
                {
                    active = new TableContext { Header = rows[i], HeaderY = rows[i].Y, Section = FindSection(rows, i) };
                    continue;
                }
                var field = ParseDataRow(rows[i], active);
                if (field != null) output.Add(field);
            }

            // Canonical result: one evidence-backed row per section/key. Conflicting reconstructions
            // are not silently merged; keep the strongest row and flag it for review.
            return output.GroupBy(x => (x.Section ?? "") + "\u001f" + (x.Key ?? ""), StringComparer.OrdinalIgnoreCase)
                .Select(g => {
                    var best = g.OrderByDescending(x => x.Cells.Count).ThenByDescending(x => x.Confidence).First();
                    if (g.Count() > 1) best.Warnings.Add("MultipleLayoutCandidates");
                    return best;
                }).ToList();
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
