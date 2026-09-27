using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoReport.WinForms
{
    public sealed class MainForm : Form
    {
        private readonly TextBox imagePath = new TextBox();
        private readonly PictureBox preview = new PictureBox();
        private readonly DataGridView observations = new DataGridView();
        private readonly TextBox rawText = new TextBox();
        private readonly DataGridView structured = new DataGridView();
        private readonly TextBox warnings = new TextBox();
        private readonly Label status = new Label();
        private readonly Button runButton = new Button();
        private readonly Button saveButton = new Button();
        private Study lastStudy;

        public MainForm()
        {
            Text = "AutoReport - Ultrasound OCR";
            Width = 1200; Height = 760; StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, Padding = new Padding(8) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            root.Controls.Add(top, 0, 0);

            top.Controls.Add(new Label { Text = "Image", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
            imagePath.Dock = DockStyle.Fill; imagePath.ReadOnly = true;
            top.Controls.Add(imagePath, 1, 0);
            var browse = new Button { Text = "Browse...", Dock = DockStyle.Fill };
            browse.Click += BrowseImage; top.Controls.Add(browse, 2, 0);
            runButton.Text = "Run OCR"; runButton.Dock = DockStyle.Fill;
            runButton.Click += async (s,e) => await RunOcrAsync(); top.Controls.Add(runButton, 3, 0);
            saveButton.Text = "Save JSON"; saveButton.Dock = DockStyle.Fill; saveButton.Enabled = false;
            saveButton.Click += SaveJson; top.Controls.Add(saveButton, 4, 0);

            status.Text = "Ready - select an ultrasound image.";
            status.TextAlign = ContentAlignment.MiddleLeft; status.Dock = DockStyle.Fill;
            top.Controls.Add(status, 0, 1); top.SetColumnSpan(status, 5);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 430 };
            root.Controls.Add(split, 0, 1);
            preview.Dock = DockStyle.Fill; preview.SizeMode = PictureBoxSizeMode.Zoom; preview.BorderStyle = BorderStyle.FixedSingle;
            split.Panel1.Controls.Add(preview);

            var tabs = new TabControl { Dock = DockStyle.Fill }; split.Panel2.Controls.Add(tabs);
            var resultTab = new TabPage("Legacy rule candidates");
            observations.Dock = DockStyle.Fill; observations.ReadOnly = true; observations.AllowUserToAddRows = false;
            observations.AllowUserToDeleteRows = false; observations.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            observations.Columns.Add("Key","Field"); observations.Columns.Add("Value","Value"); observations.Columns.Add("Unit","Unit");
            observations.Columns.Add("Confidence","OCR confidence"); observations.Columns.Add("Evidence","Evidence"); observations.Columns.Add("Warnings","Warnings");
            resultTab.Controls.Add(observations); tabs.TabPages.Add(resultTab);

            var structureTab = new TabPage("Structured report");
            structured.Dock = DockStyle.Fill; structured.ReadOnly = true; structured.AllowUserToAddRows = false;
            structured.AllowUserToDeleteRows = false; structured.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            structured.Columns.Add("Section","Section"); structured.Columns.Add("Subsection","Subsection");
            structured.Columns.Add("Type","Type"); structured.Columns.Add("Key","Key / Row");
            structured.Columns.Add("Values","Values"); structured.Columns.Add("Unit","Unit");
            structureTab.Controls.Add(structured); tabs.TabPages.Insert(0, structureTab);
            tabs.SelectedTab = structureTab;

            var textTab = new TabPage("Raw OCR text");
            rawText.Dock = DockStyle.Fill; rawText.Multiline = true; rawText.ScrollBars = ScrollBars.Both; rawText.ReadOnly = true;
            rawText.Font = new Font(FontFamily.GenericMonospace, 10); textTab.Controls.Add(rawText); tabs.TabPages.Add(textTab);

            var warningTab = new TabPage("Warnings");
            warnings.Dock = DockStyle.Fill; warnings.Multiline = true; warnings.ScrollBars = ScrollBars.Vertical; warnings.ReadOnly = true;
            warningTab.Controls.Add(warnings); tabs.TabPages.Add(warningTab);
        }

        private void BrowseImage(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                imagePath.Text = dialog.FileName;
                if (preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
                using (var source = Image.FromFile(dialog.FileName)) preview.Image = new Bitmap(source);
                status.Text = "Image selected. Click Run OCR.";
            }
        }

        private static string FindRepoRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "config")) && Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private async Task RunOcrAsync()
        {
            if (!File.Exists(imagePath.Text)) { MessageBox.Show(this, "Select an image first."); return; }
            runButton.Enabled = false; saveButton.Enabled = false; observations.Rows.Clear(); structured.Rows.Clear(); rawText.Clear(); warnings.Clear();
            status.Text = "Running PaddleOCR...";
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string repoRoot = FindRepoRoot(baseDir);
                string rules = repoRoot == null ? Path.Combine(baseDir, "config", "extraction.default.json") :
                    Path.Combine(repoRoot, "config", "extraction.default.json");
                if (!File.Exists(rules)) throw new FileNotFoundException("extraction.default.json was not found. Keep the config folder with the application.");

                using (var reader = new PaddleOcrReader())
                {
                    var engine = new AutoReportEngine(reader, JsonFile.Read<ExtractionProfile>(rules));
                    lastStudy = await engine.ExtractAsync("gui-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
                        new[] { imagePath.Text }, CancellationToken.None);
                }

                foreach (var item in lastStudy.Observations)
                    observations.Rows.Add(item.Key, item.Value, item.Unit, item.Confidence.ToString("0.0"),
                        item.Evidence, string.Join(", ", item.Warnings));

                foreach (var item in lastStudy.StructuredFields)
                    structured.Rows.Add(item.Section, item.Subsection, item.Type, item.Key,
                        string.Join(" | ", item.Values), item.Unit);

                rawText.Text = string.Join(Environment.NewLine + Environment.NewLine,
                    lastStudy.Sources.Select(page => "[" + page.Pass + "]" + Environment.NewLine +
                    string.Join(Environment.NewLine, page.Lines.Select(line => line.Text))));
                warnings.Text = string.Join(Environment.NewLine, lastStudy.Warnings);
                saveButton.Enabled = true;
                status.Text = "OCR complete - " + lastStudy.StructuredFields.Count + " structured item(s), " +
                    lastStudy.Observations.Count + " configured candidate(s).";
                if (lastStudy.StructuredFields.Count == 0 && lastStudy.Observations.Count == 0)
                    MessageBox.Show(this, "OCR finished, but no structured fields were recognized. Check Raw OCR text.");
            }
            catch (Exception ex)
            {
                lastStudy = null; status.Text = "OCR failed.";
                MessageBox.Show(this, ex.Message, "AutoReport", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { runButton.Enabled = true; }
        }

        private void SaveJson(object sender, EventArgs e)
        {
            if (lastStudy == null) return;
            using (var dialog = new SaveFileDialog { Filter = "JSON files|*.json", FileName = "study.json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { JsonFile.Write(dialog.FileName, lastStudy); MessageBox.Show(this, "Saved."); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "AutoReport", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && preview.Image != null) preview.Image.Dispose();
            base.Dispose(disposing);
        }
    }
}
