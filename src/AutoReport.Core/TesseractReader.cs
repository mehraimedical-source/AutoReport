using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoReport
{
    public sealed class TesseractOptions
    {
        public string ExecutablePath { get; set; }
        public string TessdataDirectory { get; set; }
        public string Languages { get; set; } = "eng";
        public int TimeoutSeconds { get; set; } = 60;
        public int Threads { get; set; } = 1;
        public bool IncludeYellowTextPass { get; set; } = true;
    }

    public sealed class TesseractReader : IImageTextReader
    {
        private readonly TesseractOptions options;
        public TesseractReader(TesseractOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            if (!File.Exists(options.ExecutablePath)) throw new FileNotFoundException("Local Tesseract executable is required.");
            if (!Regex.IsMatch(options.Languages ?? "", @"\A[a-zA-Z0-9_]+(?:\+[a-zA-Z0-9_]+)*\z"))
                throw new ArgumentException("Invalid language list.");
            if (options.TimeoutSeconds < 1 || options.Threads < 1) throw new ArgumentException("Invalid OCR limits.");
            foreach (var language in options.Languages.Split('+'))
                if (!File.Exists(Path.Combine(options.TessdataDirectory, language + ".traineddata")))
                    throw new FileNotFoundException("Missing local model: " + language + ".traineddata");
        }

        public async Task<IReadOnlyList<SourcePage>> ReadAsync(string imagePath, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(extension))
                throw new NotSupportedException("This adapter accepts JPG, PNG and BMP. Add an adapter for other formats.");
            string temp = Path.Combine(Path.GetTempPath(), "AutoReport-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var result = new List<SourcePage>();
                foreach (bool yellow in options.IncludeYellowTextPass ? new[] { false, true } : new[] { false })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string prepared = Path.Combine(temp, yellow ? "yellow.png" : "gray.png");
                    double scale;
                    if (!Prepare(imagePath, prepared, yellow, out scale)) continue;
                    string tsv = await RunAsync(prepared, yellow ? 11 : 6, cancellationToken).ConfigureAwait(false);
                    result.Add(new SourcePage { Engine = "Tesseract TSV", Pass = yellow ? "yellow-text/psm11" : "grayscale/psm6",
                        Lines = ParseTsv(tsv, scale) });
                }
                return result;
            }
            finally { Directory.Delete(temp, true); }
        }

        private async Task<string> RunAsync(string path, int psm, CancellationToken token)
        {
            var start = new ProcessStartInfo {
                FileName = Path.GetFullPath(options.ExecutablePath),
                Arguments = Quote(path) + " stdout --tessdata-dir " + Quote(Path.GetFullPath(options.TessdataDirectory)) +
                    " -l " + options.Languages + " --psm " + psm.ToString(CultureInfo.InvariantCulture) + " tsv",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["OMP_THREAD_LIMIT"] = options.Threads.ToString(CultureInfo.InvariantCulture);
            using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
            {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (sender, args) => exited.TrySetResult(true);
                token.ThrowIfCancellationRequested();
                process.Start();
                try
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (process.HasExited) exited.TrySetResult(true);
                    using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var delay = Task.Delay(TimeSpan.FromSeconds(options.TimeoutSeconds), limit.Token);
                        var completed = await Task.WhenAny(exited.Task, delay).ConfigureAwait(false);
                        if (completed != exited.Task)
                        {
                            if (!process.HasExited) process.Kill();
                            await exited.Task.ConfigureAwait(false);
                            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            throw new TimeoutException("Local OCR timed out.");
                        }
                        limit.Cancel();
                    }
                    string text = await stdout.ConfigureAwait(false);
                    string error = await stderr.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new InvalidOperationException("Local OCR failed: " + error);
                    return text;
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            }
        }

        private sealed class Word
        {
            public string Group, Text;
            public int X, Y, W, H;
            public double Confidence;
        }

        public static List<TextLine> ParseTsv(string tsv, double scale = 1)
        {
            if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
            var words = new List<Word>();
            foreach (var row in tsv.Split('\n'))
            {
                var c = row.TrimEnd('\r').Split(new[] { '\t' }, 12);
                if (c.Length != 12 || c[0] != "5" || string.IsNullOrWhiteSpace(c[11])) continue;
                int x, y, w, h; double confidence;
                if (!int.TryParse(c[6], out x) || !int.TryParse(c[7], out y) || !int.TryParse(c[8], out w) ||
                    !int.TryParse(c[9], out h) || !double.TryParse(c[10], NumberStyles.Float, CultureInfo.InvariantCulture, out confidence)) continue;
                words.Add(new Word { Group = string.Join("/", c.Skip(1).Take(4)), Text = c[11], X = x, Y = y,
                    W = w, H = h, Confidence = confidence });
            }
            return words.GroupBy(x => x.Group).Select(group => {
                var list = group.OrderBy(x => x.X).ToList();
                int x = list.Min(w => w.X), y = list.Min(w => w.Y);
                return new TextLine { Text = string.Join(" ", list.Select(w => w.Text)),
                    Confidence = list.Min(w => w.Confidence), X = (int)(x / scale), Y = (int)(y / scale),
                    Width = (int)((list.Max(w => w.X + w.W) - x) / scale),
                    Height = (int)((list.Max(w => w.Y + w.H) - y) / scale) };
            }).OrderBy(x => x.Y).ThenBy(x => x.X).ToList();
        }

        // Windows command-line quoting, including trailing backslashes. No shell is involved.
        private static string Quote(string value)
        {
            var output = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); output.Append(c); slashes = 0;
            }
            return output.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static bool Prepare(string input, string output, bool yellow, out double scale)
        {
            using (var source = Image.FromFile(input))
            {
                if ((long)source.Width * source.Height > 32000000) throw new NotSupportedException("Image exceeds 32 megapixels.");
                scale = Math.Min(2.0, 2400.0 / Math.Max(source.Width, source.Height));
                scale = Math.Max(1.0, scale);
                using (var bitmap = new Bitmap((int)(source.Width * scale), (int)(source.Height * scale), PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(source, 0, 0, bitmap.Width, bitmap.Height); }
                    var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
                    int colored = 0;
                    try
                    {
                        int length = data.Stride * bitmap.Height; var bytes = new byte[length];
                        Marshal.Copy(data.Scan0, bytes, 0, length);
                        long brightness = 0;
                        for (int y = 0; y < bitmap.Height; y++)
                            for (int x = 0; x < bitmap.Width; x++) { int i = y * data.Stride + x * 3; brightness += bytes[i + 1]; }
                        bool invert = brightness / ((long)bitmap.Width * bitmap.Height) < 127;
                        for (int y = 0; y < bitmap.Height; y++)
                            for (int x = 0; x < bitmap.Width; x++)
                            {
                                int i = y * data.Stride + x * 3; int b = bytes[i], g = bytes[i + 1], r = bytes[i + 2];
                                bool isYellow = r > 80 && g > 70 && b < Math.Min(r, g) * 0.75;
                                if (isYellow) colored++;
                                int gray = (r * 299 + g * 587 + b * 114) / 1000;
                                byte value = yellow ? (isYellow ? (byte)0 : (byte)255) : (byte)(invert ? 255 - gray : gray);
                                bytes[i] = bytes[i + 1] = bytes[i + 2] = value;
                            }
                        Marshal.Copy(bytes, 0, data.Scan0, length);
                    }
                    finally { bitmap.UnlockBits(data); }
                    if (yellow && colored < 100) return false;
                    bitmap.Save(output, ImageFormat.Png); return true;
                }
            }
        }
    }
}
