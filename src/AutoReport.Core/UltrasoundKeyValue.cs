using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoReport
{
    // Adds vessel/side context without asking the OCR model to infer medical meaning.
    // The context must be explicitly present in OCR text (for example "[Lt. Uterine A]").
    public static class UltrasoundContext
    {
        private static readonly Regex LeftUterine = new Regex(@"\b(?:Lt\.?|Left)\s*\.?\s*Uterine\s*A(?:rtery)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RightUterine = new Regex(@"\b(?:Rt\.?|Right)\s*\.?\s*Uterine\s*A(?:rtery)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static void Apply(SourcePage page, IList<Observation> observations)
        {
            if (page == null || observations == null || observations.Count == 0) return;
            string all = string.Join(" ", page.Lines.Select(x => x.Text ?? ""));
            string prefix = LeftUterine.IsMatch(all) ? "Doppler.LeftUterineArtery."
                : RightUterine.IsMatch(all) ? "Doppler.RightUterineArtery." : null;
            if (prefix == null) return;

            foreach (var item in observations.Where(x => x.Key != null && x.Key.StartsWith("Doppler.Unspecified.", StringComparison.OrdinalIgnoreCase)))
                item.Key = prefix + item.Key.Substring("Doppler.Unspecified.".Length);
        }
    }

    public static class KeyValueBuilder
    {
        public static Dictionary<string, KeyValueMeasurement> Build(IEnumerable<Observation> observations)
        {
            var result = new Dictionary<string, KeyValueMeasurement>(StringComparer.OrdinalIgnoreCase);
            if (observations == null) return result;

            foreach (var group in observations.Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Never silently average medical measurements. Keep the strongest OCR candidate and flag conflicts.
                var best = group.OrderBy(x => x.Warnings == null ? 0 : x.Warnings.Count).ThenByDescending(x => x.Confidence).First();
                var warnings = (best.Warnings ?? new List<string>()).ToList();
                var distinct = group.Select(x => (x.Value ?? "") + "|" + (x.Unit ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinct.Count > 1) warnings.Add("ConflictingCandidates");

                result[group.Key] = new KeyValueMeasurement {
                    Value = best.Value, Unit = best.Unit ?? "", Confidence = best.Confidence,
                    Evidence = best.Evidence, Warnings = warnings.Distinct().ToList()
                };
            }
            return result;
        }
    }
}
