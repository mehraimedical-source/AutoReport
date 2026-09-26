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
        private readonly TextBox enginePath = new TextBox();
        private readonly TextBox rulesPath = new TextBox();
        private readonly PictureBox preview = new PictureBox();
        private readonly DataGridView observations = new DataGridView();
        private readonly TextBox rawText = new TextBox();
        private readonly TextBox warnings = new TextBox();
        private readonly Button runButton = new Button();
        private readonly Button saveButton = new Button();
        private Study lastStudy;

        public MainForm()
        {
            Text = "AutoReport - Image OCR Test";
            Width = 1200;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4, Padding = new Padding(8) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            root.Controls.Add(top, 0, 0);

            AddPathRow(top, 0, "Image", imagePath, "Browse...", BrowseImage);
            AddPathRow(top, 1, "Engine JSON", enginePath, "Browse...", (s,e) => BrowseJson(enginePath));
            AddPathRow(top, 2, "Rules JSON", rulesPath, "Browse...", (s,e) => BrowseJson(rulesPath));

            runButton.Text = "Run OCR";
            runButton.Dock = DockStyle.Fill;
            runButton.Click += async (s, e) => await RunOcrAsync();
            top.Controls.Add(runButton, 2, 3);

            saveButton.Text = "Save JSON";
            saveButton.Dock = DockStyle.Fill;
            saveButton.Enabled = false;
            saveButton.Click += SaveJson;
            top.Controls.Add(saveButton, 3, 3);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 430 };
            root.Controls.Add(split, 0, 1);

            preview.Dock = DockStyle.Fill;
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            preview.BorderStyle = BorderStyle.FixedSingle;
            split.Panel1.Controls.Add(preview);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            split.Panel2.Controls.Add(tabs);

            var resultTab = new TabPage("Extracted fields");
            observations.Dock = DockStyle.Fill;
            observations.ReadOnly = true;
            observations.AllowUserToAddRows = false;
            observations.AllowUserToDeleteRows = false;
            observations.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            observations.Columns.Add("Key", "Field");
            observations.Columns.Add("Value", "Value");
            observations.Columns.Add("Unit", "Unit");
            observations.Columns.Add("Confidence", "OCR confidence");
            observations.Columns.Add("Evidence", "Evidence");
            observations.Columns.Add("Warnings", "Warnings");
            resultTab.Controls.Add(observations);
            tabs.TabPages.Add(resultTab);

            var textTab = new TabPage("Raw OCR text");
            rawText.Dock = DockStyle.Fill;
            rawText.Multiline = true;
            rawText.ScrollBars = ScrollBars.Both;
            rawText.ReadOnly = true;
            rawText.Font = new Font(FontFamily.GenericMonospace, 10);
            textTab.Controls.Add(rawText);
            tabs.TabPages.Add(textTab);

            var warningTab = new TabPage("Warnings");
            warnings.Dock = DockStyle.Fill;
            warnings.Multiline = true;
            warnings.ScrollBars = ScrollBars.Vertical;
            warnings.ReadOnly = true;
            warningTab.Controls.Add(warnings);
            tabs.TabPages.Add(warningTab);

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var repoRoot = FindRepoRoot(baseDir);
            if (repoRoot != null)
            {
                rulesPath.Text = Path.Combine(repoRoot, "config", "extraction.default.json");
                enginePath.Text = Path.Combine(repoRoot, "config", "engine.json");
                if (!File.Exists(enginePath.Text))
                    enginePath.Text = Path.Combine(repoRoot, "config", "engine.example.json");
            }
        }

        private static string FindRepoRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "config")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static void AddPathRow(TableLayoutPanel panel, int row, string label, TextBox box, string buttonText, EventHandler click)
        {
            panel.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);
            box.Dock = DockStyle.Fill;
            panel.Controls.Add(box, 1, row);
            panel.SetColumnSpan(box, 2);
            var button = new Button { Text = buttonText, Dock = DockStyle.Fill };
            button.Click += click;
            panel.Controls.Add(button, 3, row);
        }

        private void BrowseImage(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                imagePath.Text = dialog.FileName;
                if (preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
                using (var source = Image.FromFile(dialog.FileName))
                    preview.Image = new Bitmap(source);
            }
        }

        private void BrowseJson(TextBox target)
        {
            using (var dialog = new OpenFileDialog { Filter = "JSON files|*.json|All files|*.*" })
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
        }

        private async Task RunOcrAsync()
        {
            if (!File.Exists(imagePath.Text)) { MessageBox.Show(this, "Select an image first."); return; }
            if (!File.Exists(enginePath.Text)) { MessageBox.Show(this, "Select a valid engine JSON file."); return; }
            if (!File.Exists(rulesPath.Text)) { MessageBox.Show(this, "Select a valid extraction rules JSON file."); return; }

            runButton.Enabled = false;
            saveButton.Enabled = false;
            observations.Rows.Clear();
            rawText.Clear();
            warnings.Clear();
            try
            {
                var options = JsonFile.Read<TesseractOptions>(enginePath.Text);
                var profile = JsonFile.Read<ExtractionProfile>(rulesPath.Text);
                var engine = new AutoReportEngine(new TesseractReader(options), profile);
                lastStudy = await engine.ExtractAsync("gui-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
                    new[] { imagePath.Text }, CancellationToken.None);

                foreach (var item in lastStudy.Observations)
                    observations.Rows.Add(item.Key, item.Value, item.Unit,
                        item.Confidence.ToString("0.0"), item.Evidence, string.Join(", ", item.Warnings));

                rawText.Text = string.Join(Environment.NewLine + Environment.NewLine,
                    lastStudy.Sources.Select(page => "[" + page.Pass + "]" + Environment.NewLine +
                        string.Join(Environment.NewLine, page.Lines.Select(line => line.Text))));

                warnings.Text = string.Join(Environment.NewLine, lastStudy.Warnings);
                saveButton.Enabled = true;
                if (lastStudy.Observations.Count == 0)
                    MessageBox.Show(this, "OCR finished, but no configured fields matched. Check the Raw OCR text tab.");
            }
            catch (Exception ex)
            {
                lastStudy = null;
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
                try
                {
                    JsonFile.Write(dialog.FileName, lastStudy);
                    MessageBox.Show(this, "Saved.");
                }
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
