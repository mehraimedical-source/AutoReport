using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace AutoReport.Cli
{
    internal static class ExampleTemplate
    {
        public static void Create(string path)
        {
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var body = new XElement(w + "body");
            foreach (string text in new[] { "نمونه اتصال گزارش سونوگرافی", "این قالب صرفاً نمونه فنی است.",
                "BPD: {{BPD}}", "HC: {{HC}}", "AC: {{AC}}", "FL: {{FL}}", "EFW: {{EFW}}", "GA (LMP): {{GA_LMP}}" })
                body.Add(new XElement(w + "p", new XElement(w + "pPr", new XElement(w + "bidi")),
                    new XElement(w + "r", new XElement(w + "rPr", new XElement(w + "rFonts", new XAttribute(w + "ascii", "Tahoma"),
                        new XAttribute(w + "hAnsi", "Tahoma"), new XAttribute(w + "cs", "Tahoma"))), new XElement(w + "t", text))));
            body.Add(new XElement(w + "sectPr", new XElement(w + "pgSz", new XAttribute(w + "w", "12240"), new XAttribute(w + "h", "15840")),
                new XElement(w + "pgMar", new XAttribute(w + "top", "1440"), new XAttribute(w + "bottom", "1440"), new XAttribute(w + "left", "1440"), new XAttribute(w + "right", "1440"))));
            using (var file = new FileStream(path, FileMode.CreateNew))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
                Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
                Write(zip, "word/document.xml", new XDocument(new XElement(w + "document", new XAttribute(XNamespace.Xmlns + "w", w), body)).ToString());
            }
        }
        private static void Write(ZipArchive zip, string name, string content)
        { using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(content); }
    }
}
