using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaddleOCRSharp;

namespace AutoReport.WinForms
{
    internal sealed class PaddleOcrReader : IImageTextReader, IDisposable
    {
        private readonly PaddleOCREngine engine;

        public PaddleOcrReader()
        {
            var parameter = new OCRParameter
            {
                cpu_math_library_num_threads = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
                enable_mkldnn = true,
                cls = false,
                use_angle_cls = false,
                det_db_score_mode = true,
                det_db_unclip_ratio = 1.6f,
                max_side_len = 2000
            };
            engine = new PaddleOCREngine(null, parameter);
        }

        public Task<IReadOnlyList<SourcePage>> ReadAsync(string imagePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);

            return Task.Run<IReadOnlyList<SourcePage>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                OCRResult result = engine.DetectText(imagePath);
                if (result == null) throw new InvalidOperationException("PaddleOCR returned no result.");

                var page = new SourcePage { Engine = "PaddleOCRSharp", Pass = "paddle" };
                foreach (var block in result.TextBlocks ?? Enumerable.Empty<TextBlock>())
                {
                    if (string.IsNullOrWhiteSpace(block.Text)) continue;
                    int x = 0, y = 0, width = 0, height = 0;
                    if (block.BoxPoints != null && block.BoxPoints.Count > 0)
                    {
                        int minX = block.BoxPoints.Min(p => p.X);
                        int minY = block.BoxPoints.Min(p => p.Y);
                        int maxX = block.BoxPoints.Max(p => p.X);
                        int maxY = block.BoxPoints.Max(p => p.Y);
                        x = minX; y = minY; width = Math.Max(0, maxX - minX); height = Math.Max(0, maxY - minY);
                    }
                    page.Lines.Add(new TextLine
                    {
                        Text = block.Text,
                        Confidence = block.Score * 100.0,
                        X = x, Y = y, Width = width, Height = height
                    });
                }
                return new[] { page };
            }, cancellationToken);
        }

        public void Dispose()
        {
            engine.Dispose();
        }
    }
}
