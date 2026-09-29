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
            if (indexed.Count < 2 || indexed.Count(x => x.Line.Height > 0 || x.Line.Width > 0) < 2)
                return indexed.Select(x => new ExtractionLine { Text = x.Line.Text ?? "", Evidence = x.Line.Text,
                    Confidence = x.Line.Confidence, LineIndex = x.Index }).ToList();

            // Bounding boxes are authoritative. Ultrasound report exports commonly contain repeated
            // values in Last/1 columns, so sequential OCR windows create false "conflicts".
            var rows = new List<List<Tuple<TextLine, int>>>();
            foreach (var item in indexed.OrderBy(x => x.Line.Y + x.Line.Height / 2.0))
            {
                double center = item.Line.Y + item.Line.Height / 2.0;
                List<Tuple<TextLine, int>> best = null;
                double bestDistance = double.MaxValue;
                foreach (var row in rows)
                {
                    double rowCenter = row.Average(x => (double)x.Item1.Y + x.Item1.Height / 2.0);
                    double rowHeight = Math.Max(1.0, row.Average(x => (double)Math.Max(1, x.Item1.Height)));
                    double tolerance = Math.Max(5.0, Math.Max(rowHeight, Math.Max(1, item.Line.Height)) * 0.72);
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

            // Only use sequential fallback when bounding boxes produced no plausible multi-cell rows.
            // This preserves support for OCR engines without useful geometry without polluting good Paddle layouts.
            if (result.Any(x => x.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2))
                return result;

            for (int i = 0; i < indexed.Count; i++)
            {
                var parts = new List<string>(); double confidence = 100.0;
                for (int j = i; j < indexed.Count && j < i + 3; j++)
                {
                    string part = indexed[j].Line.Text ?? "";
                    if (string.IsNullOrWhiteSpace(part)) continue;
                    parts.Add(part.Trim()); confidence = Math.Min(confidence, indexed[j].Line.Confidence);
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
        private static readonly Regex Number = new Regex(@"^[+\-]?\d+(?:[.,]\d+)?(?:[%*])?$", RegexOptions.CultureInvariant);
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
        private static bool IsPercentileValue(string x)
        {
            x = RuleExtractor.NormalizeDigits((x ?? "").Trim());
            return Regex.IsMatch(x, @"^[+\-]?\d+(?:[.,]\d+)?(?:[%*])?$", RegexOptions.CultureInvariant);
        }
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

        private static bool LooksLikeHeader(List<VisualRow> rows, int index)
        {
            var row = rows[index];
            if (row.Cells.Count < 2) return false;
            var usable = row.Cells.Where(x => !IsUnit(x)).OrderBy(CenterX).ToList();
            if (usable.Count < 2) return false;

            // Numeric column names such as 1/2/3 are valid header evidence. Distinguish a
            // header from a measurement row by looking ahead: real data rows have a left
            // textual label and numeric cells that align horizontally with header columns.
            int textual = usable.Count(x => !IsValue(x));
            if (textual == 0 || usable.Count(x => T(x).Length > 0 && T(x).Length <= 16) < 2) return false;

            var headerCenters = usable.Select(CenterX).ToList();
            double spacing = headerCenters.Count > 1
                ? headerCenters.Zip(headerCenters.Skip(1), (a,b) => b - a).Where(x => x > 0).DefaultIfEmpty(40.0).Average()
                : 40.0;
            double tolerance = Math.Max(18.0, spacing * 0.60);
            int supportingRows = 0;

            for (int i = index + 1; i < rows.Count && i <= index + 5; i++)
            {
                var next = rows[i];
                var labels = next.Cells.Where(x => !IsValue(x) && !IsUnit(x)).OrderBy(CenterX).ToList();
                var values = next.Cells.Where(IsValue).ToList();
                if (labels.Count == 0 || values.Count == 0) continue;
                if (CenterX(labels[0]) >= values.Min(CenterX)) continue;

                int aligned = values.Count(v => headerCenters.Any(h => Math.Abs(h - CenterX(v)) <= tolerance));
                if (aligned >= Math.Min(2, values.Count)) supportingRows++;
            }
            return supportingRows >= 1;
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

        private static List<TextLine> HeaderColumns(VisualRow header)
        {
            // The first cell of these exports is often the section title, not a data column.
            var cells = header.Cells.Where(x => !IsUnit(x)).OrderBy(CenterX).ToList();
            if (cells.Count >= 2 && !IsValue(cells[0]) && CenterX(cells[0]) < CenterX(cells[1]) - 80)
                cells.RemoveAt(0);
            return cells;
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

            var headers = HeaderColumns(table.Header);
            var mapped = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            foreach (var value in values)
            {
                var ranked = headers.Select(h => new { Header = h, Distance = Math.Abs(CenterX(h) - CenterX(value)) })
                    .OrderBy(x => x.Distance).ToList();
                if (ranked.Count == 0) { warnings.Add("NoHeaderForCell:" + T(value)); continue; }
                var nearest = ranked[0];
                // Reject implausibly distant matches instead of inventing a column assignment.
                double typicalSpacing = headers.Count > 1
                    ? headers.Zip(headers.Skip(1), (a,b) => CenterX(b) - CenterX(a)).Where(x => x > 0).DefaultIfEmpty(60.0).Average()
                    : 80.0;
                if (nearest.Distance > Math.Max(45.0, typicalSpacing * 0.75)) { warnings.Add("AmbiguousColumn:" + T(value)); continue; }
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
                if (LooksLikeHeader(rows, i))
                {
                    var header = rows[i];
                    string section = FindSection(rows, i);

                    // Ultrasound exports often print the table title on the same horizontal
                    // band as the column header. When the first textual cell is separated
                    // from the remaining header cells by a large horizontal gap, preserve
                    // it as the exact section title and exclude it from column assignment.
                    var ordered = header.Cells.OrderBy(CenterX).ToList();
                    if (ordered.Count >= 3 && !IsValue(ordered[0]) && !IsUnit(ordered[0]))
                    {
                        double firstGap = CenterX(ordered[1]) - CenterX(ordered[0]);
                        var laterGaps = ordered.Skip(1).Zip(ordered.Skip(2), (a,b) => CenterX(b) - CenterX(a))
                            .Where(x => x > 0).OrderBy(x => x).ToList();
                        double typicalGap = laterGaps.Count == 0 ? 0 : laterGaps[laterGaps.Count / 2];
                        if (firstGap > Math.Max(80.0, typicalGap * 1.8))
                        {
                            section = T(ordered[0]);
                            var columnsOnly = new VisualRow();
                            columnsOnly.Cells.AddRange(ordered.Skip(1));
                            header = columnsOnly;
                        }
                    }

                    active = new TableContext { Header = header, HeaderY = rows[i].Y, Section = section };
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

    public static class KeywordGeometryExtractor
    {
        private static readonly HashSet<string> Units = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "mm", "cm", "m", "ms", "s", "g", "kg", "bpm", "hz", "mhz", "mmhg", "cm/s", "m/s", "cm/s²", "cm/s2", "%" };

        private static double Cx(TextLine x) { return x.X + x.Width / 2.0; }
        private static double Cy(TextLine x) { return x.Y + x.Height / 2.0; }
        private static bool Number(string text)
        {
            decimal d;
            text = RuleExtractor.NormalizeDigits((text ?? "").Trim()).TrimEnd('*', '%');
            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
        }
        private static KeywordDefinition MatchKeyword(string text, ExtractionProfile profile)
        {
            string t = (text ?? "").Trim();
            return (profile.Keywords ?? new List<KeywordDefinition>()).FirstOrDefault(k =>
                (k.Match ?? new List<string>()).Concat(new[] { k.Key }).Where(x => !string.IsNullOrWhiteSpace(x))
                .Any(x => string.Equals(x.Trim(), t, StringComparison.OrdinalIgnoreCase)));
        }

        public static void Extract(SourcePage page, Study study, ExtractionProfile profile)
        {
            var lines = page.Lines.Where(x => !string.IsNullOrWhiteSpace(x.Text)).OrderBy(x => x.Y).ThenBy(x => x.X).ToList();
            var keywordLines = lines.Select(x => new { Line = x, Def = MatchKeyword(x.Text, profile) })
                .Where(x => x.Def != null).ToList();
            var consumed = new HashSet<TextLine>();

            // Simple metadata: keyword -> nearest non-keyword token on the same visual row.
            foreach (var item in keywordLines)
            {
                var key = item.Line;
                var right = lines.Where(v => v != key && v.X > key.X && MatchKeyword(v.Text, profile) == null &&
                    Math.Abs(Cy(v) - Cy(key)) <= Math.Max(6.0, Math.Max(key.Height, v.Height) * .65))
                    .OrderBy(v => v.X - key.X).FirstOrDefault();
                if (right == null || right.X - (key.X + key.Width) > 190) continue;

                // Numeric measurement keywords are handled below as table/sequence rows.
                if (Number(right.Text) && keywordLines.Any(k => k.Line.Y > key.Y + key.Height / 2)) continue;
                study.Fields.Add(new ExtractedPair { Key = key.Text.Trim(), Value = right.Text.Trim(),
                    RawText = key.Text.Trim() + " " + right.Text.Trim(), Confidence = Math.Min(key.Confidence, right.Confidence) });
                consumed.Add(key); consumed.Add(right);
            }

            // Measurement rows: keyword followed by every numeric token on the same Y band.
            // Column names are discovered from the nearest header band above; no medical meaning is inferred.
            var measurementRows = new List<StructuredField>();
            foreach (var item in keywordLines)
            {
                var key = item.Line;
                var values = lines.Where(v => v != key && v.X > key.X && Number(v.Text) &&
                    Math.Abs(Cy(v) - Cy(key)) <= Math.Max(7.0, Math.Max(key.Height, v.Height) * .72))
                    .OrderBy(v => v.X).ToList();
                if (values.Count == 0) continue;

                var unit = lines.Where(v => v.X > key.X && Units.Contains((v.Text ?? "").Trim()) &&
                    Math.Abs(Cy(v) - Cy(key)) <= Math.Max(8.0, Math.Max(key.Height, v.Height) * .85))
                    .OrderBy(v => Math.Abs(Cy(v) - Cy(key))).FirstOrDefault();

                var above = lines.Where(h => h.Y < key.Y && h.X > key.X && !Number(h.Text) &&
                    MatchKeyword(h.Text, profile) == null && !Units.Contains((h.Text ?? "").Trim()) &&
                    key.Y - h.Y < 100).ToList();
                // Numeric headers such as 1/2/3 are valid headers too.
                above.AddRange(lines.Where(h => h.Y < key.Y && h.X > key.X && Number(h.Text) && key.Y - h.Y < 100));
                var headers = above.GroupBy(h => (int)(Cy(h) / 8)).OrderByDescending(g => g.Count())
                    .Select(g => g.OrderBy(Cx).ToList()).FirstOrDefault() ?? new List<TextLine>();

                var cells = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in values)
                {
                    var h = headers.OrderBy(x => Math.Abs(Cx(x) - Cx(v))).FirstOrDefault();
                    string column = h != null && Math.Abs(Cx(h) - Cx(v)) < 55 ? h.Text.Trim() : "Value" + (cells.Count + 1);
                    if (!cells.ContainsKey(column)) cells[column] = RuleExtractor.NormalizeDigits(v.Text.Trim());
                }
                string section = "";
                var title = lines.Where(t => t.Y < key.Y && t.X <= key.X + 20 && MatchKeyword(t.Text, profile) == null &&
                    !Number(t.Text) && !Units.Contains((t.Text ?? "").Trim()) && key.Y - t.Y < 100)
                    .OrderByDescending(t => t.Y).FirstOrDefault();
                if (title != null) section = title.Text.Trim();

                var field = new StructuredField { Section = section, Type = "TableRow", Key = key.Text.Trim(),
                    Cells = cells, Values = cells.Values.ToList(), Unit = unit == null ? "" : unit.Text.Trim(),
                    RawText = string.Join(" ", new[] { key.Text }.Concat(values.Select(v => v.Text)).Concat(unit == null ? new string[0] : new[] { unit.Text })),
                    Confidence = new[] { key.Confidence }.Concat(values.Select(v => v.Confidence)).Min() };
                measurementRows.Add(field);
                consumed.Add(key); foreach (var v in values) consumed.Add(v); if (unit != null) consumed.Add(unit);
            }

            study.StructuredFields.AddRange(measurementRows);
            study.Tables.AddRange(UltrasoundTableBuilder.Build(measurementRows));
            foreach (var line in lines.Where(x => !consumed.Contains(x)))
                study.Unassigned.Add(new UnassignedText { Text = line.Text.Trim(), Confidence = line.Confidence, X = line.X, Y = line.Y });
        }
    }

    public static class UltrasoundTableBuilder
    {
        public static List<ReportTable> Build(IEnumerable<StructuredField> fields)
        {
            return fields.Where(x => string.Equals(x.Type, "TableRow", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Section ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new ReportTable {
                    Section = g.Key,
                    Columns = g.SelectMany(x => x.Cells == null ? Enumerable.Empty<string>() : x.Cells.Keys)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Rows = g.ToList(),
                    Confidence = g.Count() == 0 ? 0 : g.Min(x => x.Confidence),
                    Warnings = g.SelectMany(x => x.Warnings ?? new List<string>()).Distinct().ToList()
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
        private readonly ExtractionProfile profile;
        public AutoReportEngine(IImageTextReader reader, ExtractionProfile profile)
        {
            this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            extractor = new RuleExtractor(profile);
        }

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
                    study.Sources.Add(page);
                    var pageObservations = extractor.Extract(page);
                    study.Observations.AddRange(pageObservations);
                    KeywordGeometryExtractor.Extract(page, study, profile);
                }
            }
            if (study.Sources.Count == 0) throw new InvalidOperationException("No source images were processed.");
            if (study.Observations.Count == 0 && study.StructuredFields.Count == 0)
                study.Warnings.Add("No structured fields were recognized. Inspect raw OCR text.");
            // The public structured result is evidence-only. Do not invent semantic namespaces,
            // expand abbreviations, translate labels, or infer medical meaning not printed in the image.
            study.KeyValues.Clear();
            return study;
        }
    }
}
