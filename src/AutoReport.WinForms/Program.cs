using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace AutoReport.WinForms
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--self-test") return SelfTest(args[1]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        // Runs the deployed adapter and its native model on synthetic data, without opening the UI.
        private static int SelfTest(string logPath)
        {
            string image = Path.Combine(Path.GetTempPath(), "AutoReport-smoke-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (var bitmap = new Bitmap(800, 160))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var font = new Font("Arial", 32))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawString("BPD 82.1 mm", font, Brushes.Black, 30, 40);
                    bitmap.Save(image, ImageFormat.Png);
                }
                using (var reader = new PaddleOcrReader())
                {
                    var pages = reader.ReadAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                    if (!pages.SelectMany(p => p.Lines).Any(line => !string.IsNullOrWhiteSpace(line.Text)))
                        throw new InvalidOperationException("Native OCR returned no text for the synthetic smoke-test image.");
                    File.WriteAllText(logPath, "PASS: bundled Paddle engine loaded and recognized synthetic image text.");
                }
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(logPath, error.ToString());
                return 1;
            }
            finally { if (File.Exists(image)) File.Delete(image); }
        }
    }
}
