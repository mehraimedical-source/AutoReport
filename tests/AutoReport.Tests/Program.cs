using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AutoReport.Tests
{
    internal static class Program
    {
        private static int count;
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); count++; }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { count++; return; } throw new Exception("Expected " + typeof(T).Name); }

        private static int Main(string[] args)
        {
            // Child-process fixture exercises real pipe draining, quoting, timeout and cancellation.
            if (args.Length > 2 && args[1] == "stdout")
            {
                if (args.Contains("timeout")) Thread.Sleep(10000);
                Console.Error.Write(new string('x', 100000));
                Console.WriteLine("5\t1\t1\t1\t1\t1\t20\t40\t50\t20\t95\tBPD");
                return 0;
            }
            string temp = Path.Combine(Path.GetTempPath(), "AutoReport-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                TestExtraction(args.Length > 0 ? args[0] : "config/extraction.default.json");
                TestTsv(); TestTemplates(temp); TestPipeline(temp).GetAwaiter().GetResult();
                TestProcessAdapter(temp).GetAwaiter().GetResult();
                Console.WriteLine("PASS: " + count + " assertions."); return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
            finally { Directory.Delete(temp, true); }
        }

        private static SourcePage Page(params string[] lines)
        { return new SourcePage { Lines = lines.Select(x => new TextLine { Text = x, Confidence = 96 }).ToList() }; }

        private static void TestExtraction(string path)
        {
            var profile = JsonFile.Read<ExtractionProfile>(path);
            var extractor = new RuleExtractor(profile);
            var page = Page("BPD (Hadlock) 82.10 mm 82.10 last 32w4d", "HC* (Hadlock) 297.22 mm", "HC (Hadlock) ۳۰۵٫۲۳ mm",
                "FL/HC (Hadlock) 0.22 (0.20 - 0.22)", "CI (BPD/OFD) 79%", "EFW (Hadlock) 24009", "EFW (Hadlock) 2400 g",
                "GA(LMP) | 34w6d EDD(LMP) | 01.11.2026", "GA(AUA) | 34w1d", "D1 25.85 mm", "PI 1.23", "FL 6.75 cm");
            var found = extractor.Extract(page);
            Check(found.Single(x => x.Key == "Obstetric.BPD").Value == "82.10", "BPD reading must use first value, not GA.");
            Check(found.Single(x => x.Key == "Obstetric.HC").Value == "305.23", "HC must not consume HC*; normalize Persian digits.");
            Check(found.Single(x => x.Key == "Obstetric.HCStar").Value == "297.22", "HC* is separate.");
            Check(found.Count(x => x.Key == "Obstetric.EFW") == 1, "Missing weight unit must not be guessed from 9.");
            Check(found.Single(x => x.Key == "Obstetric.EFW").Value == "2400", "Explicit g accepted.");
            Check(found.Single(x => x.Key == "Obstetric.GA.LMP").Value != found.Single(x => x.Key == "Obstetric.GA.AUA").Value, "LMP and AUA must remain separate.");
            Check(found.Any(x => x.Key == "Doppler.Unspecified.PI"), "Do not guess vessel identity.");
            Check(!found.Any(x => x.Evidence.StartsWith("D1")), "Do not guess D1 anatomy.");
            Check(page.Lines.Count == 12, "Unmapped text retained.");
            var low = Page("BPD 82.1 mm"); low.Lines[0].Confidence = 31;
            Check(extractor.Extract(low).Single().Warnings.Contains("LowOcrConfidence"), "Low OCR confidence marked.");
            decimal converted;
            Check(Units.TryConvert(6.75m, "cm", "mm", out converted) && converted == 67.5m, "cm to mm conversion.");
            Check(!Units.TryConvert(100m, "", "mm", out converted), "Never invent units.");
            Throws<ArgumentException>(() => new RuleExtractor(new ExtractionProfile { Rules = new List<ExtractionRule> { new ExtractionRule { Key = "a", Pattern = "abc" } } }));
        }

        private static void TestTsv()
        {
            var lines = TesseractReader.ParseTsv("level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                "5\t1\t1\t1\t1\t1\t20\t40\t50\t20\t95.5\tBPD\n" +
                "5\t1\t1\t1\t1\t2\t80\t40\t60\t20\t72.3\t82.1\n" +
                "5\t1\t1\t1\t1\t3\t145\t40\t30\t20\t92\tmm\n" +
                "5\t1\t2\t1\t1\t1\t300\t40\t30\t20\t96\tHC\n", 2);
            Check(lines.Count == 2, "Do not merge different spatial blocks.");
            Check(lines[0].Text == "BPD 82.1 mm" && lines[0].Confidence == 72.3, "TSV word ordering and minimum confidence.");
            Check(lines[0].X == 10 && lines[0].Y == 20, "Coordinates map back to original image.");
        }

        private static void TestTemplates(string temp)
        {
            string source = Path.Combine(temp, "template.docx");
            CreateDoc(source);
            var word = new WordTemplate();
            Check(word.Inspect(source).OrderBy(x => x).SequenceEqual(new[] { "BPD", "HC" }), "Find split tokens and content controls, including header.");
            var profile = new TemplateProfile { Bindings = new List<TemplateBinding> {
                new TemplateBinding { Token = "BPD", Key = "Obstetric.BPD", OutputUnit = "mm" },
                new TemplateBinding { Token = "HC", Key = "Obstetric.HC", OutputUnit = "mm" } } };
            var study = new Study { StudyId = "synthetic-exam" };
            study.Observations.Add(new Observation { Key = "Obstetric.BPD", Value = "8.21", Unit = "cm" });
            var draft = word.RenderDraft(source, Path.Combine(temp, "draft.docx"), profile, study);
            Check(draft.Warnings.Any(x => x.Contains("HC")), "Missing field reported in draft.");
            string draftText = ReadPart(draft.OutputPath, "word/document.xml");
            Check(draftText.Contains("82.1 mm") && draftText.Contains("نیاز به بررسی"), "Unit conversion and missing marker.");
            Check(draftText.Contains("پیش‌نویس") && draftText.Contains("متن ثابت"), "Draft notice and Persian static text preserved.");
            Check(ReadPart(draft.OutputPath, "word/header1.xml").Contains("82.1 mm"), "Header replacement.");
            Check(ReadPart(draft.OutputPath, "word/styles.xml") == "<styles />", "Unrelated parts unchanged.");
            Throws<InvalidOperationException>(() => word.RenderFinal(source, Path.Combine(temp, "blocked.docx"), profile, study, "Reviewer"));
            Check(!File.Exists(Path.Combine(temp, "blocked.docx")), "Failed final leaves no output.");
            study.Approve(study.Observations[0].Id, "Reviewer");
            study.SetReviewed("Obstetric.HC", "305.2", "mm", "Reviewer", "Manually verified source");
            var final = word.RenderFinal(source, Path.Combine(temp, "final.docx"), profile, study, "Reviewer");
            string finalText = ReadPart(final.OutputPath, "word/document.xml");
            Check(!final.IsDraft && finalText.Contains("305.2 mm") && !finalText.Contains("{{"), "Approved final filled completely.");
            Check(finalText.Contains("<w:b") && finalText.Contains("متن ثابت"), "Run formatting and prose retained.");
            Check(word.Inspect(source).Count == 2, "Original template remains intact.");
            Throws<IOException>(() => word.RenderDraft(source, final.OutputPath, profile, study));
            Throws<ArgumentException>(() => word.RenderFinal(source, Path.Combine(temp, "blank-reviewer.docx"), profile, study, ""));
            Throws<NotSupportedException>(() => word.Inspect("legacy.doc"));

            var ambiguous = new Study { StudyId = "other" };
            ambiguous.Observations.Add(new Observation { Key = "Obstetric.BPD", Value = "82.1", Unit = "mm" });
            ambiguous.Observations.Add(new Observation { Key = "Obstetric.BPD", Value = "83.1", Unit = "mm" });
            var result = word.RenderDraft(source, Path.Combine(temp, "conflict.docx"), profile, ambiguous);
            Check(result.Warnings.Contains("Unresolved token: BPD"), "Conflicting readings cannot be silently selected.");
            JsonFile.Write(Path.Combine(temp, "study.json"), study);
            Check(JsonFile.Read<Study>(Path.Combine(temp, "study.json")).ReviewHistory.Count == 2, "JSON review audit round trip.");
            Throws<IOException>(() => JsonFile.Write(Path.Combine(temp, "study.json"), study));
        }

        private sealed class StubReader : IImageTextReader
        {
            public Task<IReadOnlyList<SourcePage>> ReadAsync(string path, CancellationToken token)
            { return Task.FromResult<IReadOnlyList<SourcePage>>(new[] { Page("BPD 82.1 mm") }); }
        }
        private static async Task TestPipeline(string temp)
        {
            string first = Path.Combine(temp, "a.jpg"), second = Path.Combine(temp, "b.jpg");
            File.WriteAllText(first, "synthetic-reader-fixture"); File.Copy(first, second);
            var engine = new AutoReportEngine(new StubReader(), new ExtractionProfile { Rules = new List<ExtractionRule> {
                new ExtractionRule { Key = "BPD", Pattern = @"BPD (?<value>\d+\.\d+) (?<unit>mm)", RequiredUnit = "mm" } } });
            var study = await engine.ExtractAsync("single-exam", new[] { first, second });
            Check(study.Sources.Count == 1 && study.Observations.Count == 1, "Duplicate image hashes skipped.");
            Check(study.Sources[0].Sha256.Length == 64, "Source hash retained.");
            Check(study.ReviewHistory.Count == 0, "Extraction never auto-approves.");
            var cancellation = new CancellationToken(true);
            try { await engine.ExtractAsync("single-exam", new[] { first }, cancellation); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { count++; }
        }

        private static async Task TestProcessAdapter(string temp)
        {
            string folder = Path.Combine(temp, "مسیر دارای فاصله"); Directory.CreateDirectory(folder);
            string image = Path.Combine(folder, "test image.png");
            using (var bitmap = new System.Drawing.Bitmap(80, 60))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(System.Drawing.Color.White);
                bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            }
            File.WriteAllText(Path.Combine(folder, "eng.traineddata"), "process fixture, not an OCR model");
            File.WriteAllText(Path.Combine(folder, "timeout.traineddata"), "process fixture");
            var options = new TesseractOptions { ExecutablePath = typeof(Program).Assembly.Location,
                TessdataDirectory = folder, IncludeYellowTextPass = true, TimeoutSeconds = 15 };
            var pages = await new TesseractReader(options).ReadAsync(image, CancellationToken.None);
            Check(pages.Count == 2 && pages.All(p => p.Lines.Single().Text == "BPD"), "Adapter handles Unicode paths, stderr pipe pressure, original and grayscale passes.");
            options.Languages = "timeout"; options.TimeoutSeconds = 1;
            try { await new TesseractReader(options).ReadAsync(image, CancellationToken.None); throw new Exception("Timeout ignored."); }
            catch (TimeoutException) { count++; }
            options.TimeoutSeconds = 15;
            using (var cancel = new CancellationTokenSource(200))
            {
                try { await new TesseractReader(options).ReadAsync(image, cancel.Token); throw new Exception("Child cancellation ignored."); }
                catch (OperationCanceledException) { count++; }
            }
        }

        private static void CreateDoc(string path)
        {
            var body = new XElement(W + "body",
                new XElement(W + "p", new XElement(W + "r", new XElement(W + "rPr", new XElement(W + "b")), new XElement(W + "t", "متن ثابت {{B")),
                    new XElement(W + "r", new XElement(W + "t", "PD}} / {{HC}} / {{BPD}}"))),
                new XElement(W + "tbl", new XElement(W + "tr", new XElement(W + "tc", new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", "{{HC}}")))))),
                new XElement(W + "p", new XElement(W + "sdt", new XElement(W + "sdtPr", new XElement(W + "tag", new XAttribute(W + "val", "AutoReport:HC"))),
                    new XElement(W + "sdtContent", new XElement(W + "r", new XElement(W + "t", "placeholder"))))));
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WritePart(zip, "word/document.xml", new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), body).ToString());
                WritePart(zip, "word/header1.xml", new XElement(W + "hdr", new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", "{{BPD}}")))).ToString());
                WritePart(zip, "word/styles.xml", "<styles />");
            }
        }
        private static void WritePart(ZipArchive zip, string name, string text)
        { using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text); }
        private static string ReadPart(string path, string part)
        { using (var zip = ZipFile.OpenRead(path)) using (var reader = new StreamReader(zip.GetEntry(part).Open())) return reader.ReadToEnd(); }
    }
}
