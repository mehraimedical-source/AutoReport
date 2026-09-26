using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoReport.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            using (var cancel = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (sender, e) => { e.Cancel = true; cancel.Cancel(); };
                try { return RunAsync(args, cancel.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
        }

        private static async Task<int> RunAsync(string[] args, CancellationToken token)
        {
            if (args.Length == 7 && args[0] == "extract")
            {
                if (Directory.GetDirectories(args[2]).Any())
                    throw new ArgumentException("Select one examination folder, not a parent folder containing multiple examinations.");
                var files = Directory.GetFiles(args[2]).Where(x => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(x).ToLowerInvariant())).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var engine = new AutoReportEngine(new TesseractReader(JsonFile.Read<TesseractOptions>(args[4])), JsonFile.Read<ExtractionProfile>(args[3]));
                if (args[6] != "--single-study") throw new ArgumentException("Confirm that this folder contains one examination with --single-study.");
                var study = await engine.ExtractAsync(args[1], files, token).ConfigureAwait(false);
                JsonFile.Write(args[5], study);
                Console.WriteLine("Saved {0} candidates from {1} OCR passes. Review before final reporting.", study.Observations.Count, study.Sources.Count);
                return 0;
            }
            if (args.Length == 3 && args[0] == "sample-template")
            {
                if (args[2] != "--example") throw new ArgumentException("Use --example for the non-clinical sample template.");
                ExampleTemplate.Create(args[1]); Console.WriteLine("Example template created."); return 0;
            }
            if (args.Length == 2 && args[0] == "inspect")
            { foreach (string field in new WordTemplate().Inspect(args[1])) Console.WriteLine(field); return 0; }
            if (args.Length == 6 && args[0] == "approve")
            {
                var study = JsonFile.Read<Study>(args[1]); study.Approve(args[2], args[3]);
                if (args[5] != "--verified") throw new ArgumentException("Verify the source first, then pass --verified.");
                JsonFile.Write(args[4], study); return 0;
            }
            if (args.Length == 5 && args[0] == "draft")
            {
                var result = new WordTemplate().RenderDraft(args[1], args[4], JsonFile.Read<TemplateProfile>(args[2]), JsonFile.Read<Study>(args[3]));
                foreach (var warning in result.Warnings) Console.WriteLine(warning);
                Console.WriteLine("Draft saved."); return 0;
            }
            if (args.Length == 7 && args[0] == "final")
            {
                if (args[6] != "--whole-report-reviewed") throw new ArgumentException("Confirm review of all report text, including normal statements.");
                new WordTemplate().RenderFinal(args[1], args[4], JsonFile.Read<TemplateProfile>(args[2]), JsonFile.Read<Study>(args[3]), args[5]);
                Console.WriteLine("Final report saved."); return 0;
            }
            Console.WriteLine("AutoReport (.NET Framework 4.8)\n" +
                "extract <study-id> <one-study-folder> <rules.json> <engine.json> <new-study.json> --single-study\n" +
                "sample-template <new-template.docx> --example\n" +
                "inspect <template.docx>\n" +
                "approve <study.json> <observation-id> <reviewer> <new-reviewed.json> --verified\n" +
                "draft <template.docx> <bindings.json> <study.json> <new-output.docx>\n" +
                "final <template.docx> <bindings.json> <reviewed.json> <new-output.docx> <reviewer> --whole-report-reviewed");
            return args.Length == 0 ? 0 : 2;
        }
    }
}
