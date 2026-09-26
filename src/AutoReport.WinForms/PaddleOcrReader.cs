using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;\nusing System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace AutoReport.WinForms
{
    internal sealed class PaddleOcrReader : IImageTextReader, IDisposable
    {
        private readonly object engine;
        private readonly MethodInfo detectText;

        public PaddleOcrReader()
        {
            var asm = Assembly.Load("PaddleOCRSharp");
            var engineType = asm.GetType("PaddleOCRSharp.PaddleOCREngine", true);
            var configType = asm.GetType("PaddleOCRSharp.OCRModelConfig", true);
            var parameterType = asm.GetType("PaddleOCRSharp.OCRParameter", true);
            var parameter = Activator.CreateInstance(parameterType);

            Set(parameterType, parameter, "cpu_math_library_num_threads", Math.Max(1, Math.Min(4, Environment.ProcessorCount)));
            Set(parameterType, parameter, "enable_mkldnn", true);
            Set(parameterType, parameter, "cls", false);
            Set(parameterType, parameter, "use_angle_cls", false);
            Set(parameterType, parameter, "det_db_score_mode", true);
            Set(parameterType, parameter, "det_db_unclip_ratio", 1.6f);
            Set(parameterType, parameter, "max_side_len", 2000);

            var ctor = engineType.GetConstructor(new[] { configType, parameterType });
            if (ctor == null) throw new MissingMethodException("Compatible PaddleOCREngine constructor was not found.");
            try { engine = ctor.Invoke(new[] { null, parameter }); }\n            catch (TargetInvocationException ex) { ThrowInner(ex); throw; }
            detectText = engineType.GetMethod("DetectText", new[] { typeof(string) });
            if (detectText == null) throw new MissingMethodException("PaddleOCR DetectText(string) was not found.");
        }

        private static void Set(Type type, object target, string name, object value)
        {
            var property = type.GetProperty(name);
            if (property != null && property.CanWrite) { property.SetValue(target, value, null); return; }
            var field = type.GetField(name);
            if (field != null) field.SetValue(target, value);
        }

        private static object Get(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var property = type.GetProperty(name);
            if (property != null) return property.GetValue(target, null);
            var field = type.GetField(name);
            return field == null ? null : field.GetValue(target);
        }

        public Task<IReadOnlyList<SourcePage>> ReadAsync(string imagePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);

            return Task.Run<IReadOnlyList<SourcePage>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                object result;\n                try { result = detectText.Invoke(engine, new object[] { imagePath }); }\n                catch (TargetInvocationException ex) { ThrowInner(ex); throw; }
                if (result == null) throw new InvalidOperationException("PaddleOCR returned no result.");

                var page = new SourcePage { Engine = "PaddleOCRSharp", Pass = "paddle" };
                var blocks = Get(result, "TextBlocks") as IEnumerable;
                if (blocks != null)
                {
                    foreach (object block in blocks)
                    {
                        string text = Convert.ToString(Get(block, "Text"));
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        double confidence = Convert.ToDouble(Get(block, "Score") ?? 0) * 100.0;
                        int minX = 0, minY = 0, maxX = 0, maxY = 0;
                        bool first = true;
                        var points = Get(block, "BoxPoints") as IEnumerable;
                        if (points != null)
                        {
                            foreach (object point in points)
                            {
                                int x = Convert.ToInt32(Get(point, "X") ?? 0);
                                int y = Convert.ToInt32(Get(point, "Y") ?? 0);
                                if (first) { minX = maxX = x; minY = maxY = y; first = false; }
                                else { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
                            }
                        }
                        page.Lines.Add(new TextLine { Text = text, Confidence = confidence, X = minX, Y = minY,
                            Width = first ? 0 : maxX - minX, Height = first ? 0 : maxY - minY });
                    }
                }
                return new[] { page };
            }, cancellationToken);
        }

        private static void ThrowInner(TargetInvocationException ex)\n        {\n            if (ex.InnerException != null) ExceptionDispatchInfo.Capture(ex.InnerException).Throw();\n        }\n\n        public void Dispose()
        {
            var disposable = engine as IDisposable;
            if (disposable != null) disposable.Dispose();
        }
    }
}
