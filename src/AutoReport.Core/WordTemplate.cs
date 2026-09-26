using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AutoReport
{
    public sealed class RenderResult
    {
        public string OutputPath { get; set; }
        public bool IsDraft { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class WordTemplate
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly Regex TokenPattern = new Regex(@"\{\{([^{}\r\n]+)\}\}", RegexOptions.CultureInvariant);
        private static bool IsPart(string name)
        {
            return name == "word/document.xml" || name == "word/footnotes.xml" || name == "word/endnotes.xml" ||
                Regex.IsMatch(name, @"\Aword/(?:header|footer)[0-9]*\.xml\z");
        }
        private static XDocument ReadXml(ZipArchiveEntry entry)
        {
            if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Word XML part is too large.");
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }))
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        private static List<XElement> TextNodes(XElement paragraph)
        {
            return paragraph.Descendants(W + "t").Where(t => t.Ancestors(W + "p").FirstOrDefault() == paragraph).ToList();
        }
        private static string ControlToken(XElement control)
        {
            string tag = (string)control.Element(W + "sdtPr")?.Element(W + "tag")?.Attribute(W + "val");
            return tag != null && tag.StartsWith("AutoReport:", StringComparison.Ordinal) ? tag.Substring(11) : null;
        }

        public IReadOnlyList<string> Inspect(string templatePath)
        {
            CheckExtension(templatePath);
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            using (var archive = ZipFile.OpenRead(templatePath))
            {
                if (archive.GetEntry("word/document.xml") == null) throw new InvalidDataException("Not a Word DOCX document.");
                foreach (var part in archive.Entries.Where(x => IsPart(x.FullName)))
                {
                    var xml = ReadXml(part);
                    foreach (var p in xml.Descendants(W + "p"))
                        foreach (Match match in TokenPattern.Matches(string.Concat(TextNodes(p).Select(x => x.Value))))
                            tokens.Add(match.Groups[1].Value.Trim());
                    foreach (var sdt in xml.Descendants(W + "sdt"))
                    { var token = ControlToken(sdt); if (token != null) tokens.Add(token); }
                }
            }
            return tokens.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        public RenderResult RenderDraft(string templatePath, string outputPath, TemplateProfile profile, Study study)
        { return Render(templatePath, outputPath, profile, study, null); }

        // Caller must obtain clinical approval of the WHOLE report, including unchanged normal statements.
        public RenderResult RenderFinal(string templatePath, string outputPath, TemplateProfile profile, Study study, string finalReviewer)
        {
            if (string.IsNullOrWhiteSpace(finalReviewer)) throw new ArgumentException("Whole-report reviewer is required.");
            return Render(templatePath, outputPath, profile, study, finalReviewer);
        }

        private RenderResult Render(string templatePath, string outputPath, TemplateProfile profile, Study study, string reviewer)
        {
            CheckExtension(outputPath);
            if (File.Exists(outputPath)) throw new IOException("Output already exists; choose a new filename.");
            bool draft = reviewer == null;
            var result = new RenderResult { OutputPath = outputPath, IsDraft = draft };
            var bindings = profile.Bindings.ToDictionary(x => x.Token, StringComparer.Ordinal);
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            var tokens = Inspect(templatePath);
            if (tokens.Count == 0) throw new InvalidOperationException("Template has no placeholders. Add {{tokens}} or tagged content controls first.");
            foreach (var token in tokens)
            {
                TemplateBinding binding; string value = null;
                if (bindings.TryGetValue(token, out binding)) value = Resolve(study, binding, draft, result.Warnings);
                if (value == null)
                {
                    if (!draft) throw new InvalidOperationException("Unmapped, missing or unreviewed field: " + token);
                    value = "[نیاز به بررسی: " + token + "]"; result.Warnings.Add("Unresolved token: " + token);
                }
                replacements.Add(token, value);
            }
            string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)), ".autoreport-" + Guid.NewGuid().ToString("N") + ".docx");
            try
            {
                File.Copy(templatePath, temporary, false);
                using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Update))
                {
                    foreach (var part in archive.Entries.Where(x => IsPart(x.FullName)).ToList())
                    {
                        var xml = ReadXml(part);
                        if (xml.Descendants().Any(x => x.Name == W + "ins" || x.Name == W + "del" || x.Name == W + "moveFrom" || x.Name == W + "moveTo"))
                            throw new NotSupportedException("Accept/reject tracked changes in the template before use.");
                        foreach (var sdt in xml.Descendants(W + "sdt").ToList())
                        {
                            string token = ControlToken(sdt);
                            if (token == null) continue;
                            var content = sdt.Element(W + "sdtContent");
                            if (content == null || content.Descendants(W + "tbl").Any() || content.Descendants(W + "p").Count() > 1 || content.Descendants(W + "sdt").Any())
                                throw new NotSupportedException("Use a single-paragraph text content control: " + token);
                            var nodes = content.Descendants(W + "t").ToList();
                            if (nodes.Count == 0) throw new NotSupportedException("Content control needs placeholder text: " + token);
                            nodes[0].Value = replacements[token]; nodes[0].SetAttributeValue(XNamespace.Xml + "space", "preserve");
                            foreach (var node in nodes.Skip(1)) node.Value = "";
                            sdt.Element(W + "sdtPr")?.Element(W + "showingPlcHdr")?.Remove();
                        }
                        foreach (var p in xml.Descendants(W + "p").ToList())
                        {
                            // Tagged controls have already been filled. Do not interpret values as template syntax.
                            var nodes = TextNodes(p).Where(n => !n.Ancestors(W + "sdt").Any(s => ControlToken(s) != null)).ToList();
                            ReplaceTokens(nodes, replacements);
                        }
                        if (part.FullName == "word/document.xml")
                        {
                            var body = xml.Root?.Element(W + "body");
                            if (body == null) throw new InvalidDataException("Word body is missing.");
                            string note = draft ? "پیش‌نویس — داده‌ها و متن گزارش نیاز به تأیید دارند" :
                                "تأیید گزارش: " + reviewer + " | " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
                            body.AddFirst(new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "bidi")),
                                new XElement(W + "r", new XElement(W + "rPr", new XElement(W + "b")), new XElement(W + "t", note))));
                        }
                        using (var stream = part.Open())
                        {
                            stream.SetLength(0);
                            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false })) xml.Save(writer);
                        }
                    }
                }
                File.Move(temporary, outputPath);
                return result;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string Resolve(Study study, TemplateBinding binding, bool draft, List<string> warnings)
        {
            var reviewed = study.ReviewHistory.LastOrDefault(x => x.Key == binding.Key);
            if (reviewed != null) return Format(reviewed.Value, reviewed.Unit, binding);
            if (!draft) return null;
            var candidates = study.Observations.Where(x => x.Key == binding.Key).ToList();
            var values = candidates.Select(x => Format(x.Value, x.Unit, binding)).Distinct().ToList();
            if (values.Count != 1 || values[0] == null) return null;
            warnings.Add("Unreviewed candidate used in draft: " + binding.Key);
            foreach (var warning in candidates.SelectMany(x => x.Warnings).Distinct()) warnings.Add(binding.Key + ": " + warning);
            return values[0];
        }

        private static string Format(string value, string unit, TemplateBinding binding)
        {
            string target = string.IsNullOrWhiteSpace(binding.OutputUnit) ? unit : binding.OutputUnit;
            if (!string.Equals(unit ?? "", target ?? "", StringComparison.OrdinalIgnoreCase))
            {
                decimal number, converted;
                if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number) ||
                    !Units.TryConvert(number, unit, target, out converted)) return null;
                value = converted.ToString(binding.NumberFormat ?? "0.##", CultureInfo.InvariantCulture);
            }
            return value + (binding.IncludeUnit && !string.IsNullOrWhiteSpace(target) ? " " + target : "");
        }

        private static void ReplaceTokens(List<XElement> nodes, Dictionary<string, string> replacements)
        {
            string text = string.Concat(nodes.Select(x => x.Value));
            var matches = TokenPattern.Matches(text).Cast<Match>().Reverse();
            foreach (var match in matches)
            {
                string value;
                if (!replacements.TryGetValue(match.Groups[1].Value.Trim(), out value)) continue;
                int position = 0; bool inserted = false;
                foreach (var node in nodes)
                {
                    int length = node.Value.Length, start = Math.Max(match.Index - position, 0);
                    int end = Math.Min(match.Index + match.Length - position, length);
                    if (start < end)
                    {
                        node.Value = node.Value.Substring(0, start) + (inserted ? "" : value) + node.Value.Substring(end);
                        node.SetAttributeValue(XNamespace.Xml + "space", "preserve"); inserted = true;
                    }
                    position += length;
                }
            }
        }
        private static void CheckExtension(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Only .docx is supported. Convert legacy .doc files to .docx in Word first.");
        }
    }
}
