using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ThreeMFToolset
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }

    public class ColorEntry
    {
        public int Extruder { get; set; }
        public string Hex { get; set; }
        public Color Color { get; set; }

        public static Color HexToColor(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
                return Color.FromArgb(255,
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            return Color.White;
        }
    }

    class ZipEntryData
    {
        public string Name;
        public byte[] Data;
        public ZipEntryData(string name, byte[] data) { Name = name; Data = data; }
    }

    public class MainForm : Form
    {
        private ListBox colorListBox;
        private TextBox hexTextBox;
        private Panel swatchPanel;
        private Button pickColorButton;
        private Button moveUpButton;
        private Button moveDownButton;
        private Label statusLabel;
        private string currentFilePath;
        private List<ColorEntry> colorEntries;
        private int dragSourceIndex = -1;
        private Point dragStartPoint;

        private const string FILAMENT_COLOUR_PATTERN = @"""filament_colour"":\s*\[([^\]]*)\]";

        public MainForm(string filePath = null)
        {
            this.Text = "3MF Toolset";
            this.Size = new Size(550, 520);
            this.MinimumSize = new Size(450, 400);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Icon = CreateAppIcon();

            CreateMenu();
            CreateUI();

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                LoadFile(filePath);
        }

        private Icon CreateAppIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Dark rounded square tile background
                using (var path = new GraphicsPath())
                {
                    var r = new Rectangle(1, 2, 30, 30);
                    int radius = 7;
                    path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
                    path.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
                    path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
                    path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
                    path.CloseFigure();
                    using (var brush = new SolidBrush(Color.FromArgb(0x2D, 0x2D, 0x2D)))
                        g.FillPath(brush, path);
                }

                // Isometric 3D cube representing 3D printing / 3MF
                // Top face (light)
                PointF topA = new PointF(16, 4), topB = new PointF(25, 8), topC = new PointF(16, 13), topD = new PointF(7, 8);
                // Left face (medium)
                PointF leftA = new PointF(7, 8), leftB = new PointF(16, 13), leftC = new PointF(16, 22), leftD = new PointF(7, 17);
                // Right face (dark)
                PointF rightA = new PointF(16, 13), rightB = new PointF(25, 8), rightC = new PointF(25, 17), rightD = new PointF(16, 22);

                // Top face
                using (var brush = new SolidBrush(Color.FromArgb(0x4F, 0xA8, 0xCF)))
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[] { topA, topB, topC, topD });
                    g.FillPath(brush, path);
                }

                // Left face
                using (var brush = new SolidBrush(Color.FromArgb(0x2E, 0x86, 0xAE)))
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[] { leftA, leftB, leftC, leftD });
                    g.FillPath(brush, path);
                }

                // Right face
                using (var brush = new SolidBrush(Color.FromArgb(0x1A, 0x5C, 0x7E)))
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[] { rightA, rightB, rightC, rightD });
                    g.FillPath(brush, path);
                }

                // Thin highlight edges
                using (var pen = new Pen(Color.FromArgb(80, Color.White), 1))
                {
                    g.DrawLine(pen, topA, topB);
                    g.DrawLine(pen, topA, topD);
                    g.DrawLine(pen, topB, rightC);
                }

                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void CreateMenu()
        {
            var ms = new MenuStrip();
            var fileMenu = ms.Items.Add("&File") as ToolStripMenuItem;
            fileMenu.DropDownItems.Add("&Open...", null, (s, e) => OpenFile());
            fileMenu.DropDownItems.Add("&Save", null, (s, e) => SaveFile());
            fileMenu.DropDownItems.Add("Save &As...", null, (s, e) => SaveAsFile());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("E&xit", null, (s, e) => Close());

            var helpMenu = ms.Items.Add("&Help") as ToolStripMenuItem;
            helpMenu.DropDownItems.Add("&About", null, (s, e) => ShowAbout());

            this.MainMenuStrip = ms;
            this.Controls.Add(ms);
        }

        private void CreateUI()
        {
            var mainPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10),
                Margin = new Padding(0, 0, 0, 0),
            };
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

            // Top: info
            var infoLabel = new Label
            {
                Text = "Open a .3mf file to edit extruder filament colors.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 11),
                AutoSize = false,
            };
            mainPanel.Controls.Add(infoLabel, 0, 0);

            // Center: color list box (owner-drawn)
            colorListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                DrawMode = DrawMode.OwnerDrawVariable,
                ItemHeight = 48,
                Font = new Font("Segoe UI", 10),
                AllowDrop = true,
            };
            colorListBox.DrawItem += ColorListBox_DrawItem;
            colorListBox.MeasureItem += ColorListBox_MeasureItem;
            colorListBox.SelectedIndexChanged += ColorListBox_SelectedIndexChanged;
            colorListBox.MouseDown += ColorListBox_MouseDown;
            colorListBox.MouseMove += ColorListBox_MouseMove;
            colorListBox.DragOver += ColorListBox_DragOver;
            colorListBox.DragDrop += ColorListBox_DragDrop;
            mainPanel.Controls.Add(colorListBox, 0, 1);

            // Bottom controls
            var bottomPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 0),
                Padding = new Padding(0),
                AutoSize = false,
                Height = 50,
            };

            var hexLabel = new Label
            {
                Text = "HEX:",
                TextAlign = ContentAlignment.MiddleLeft,
                Size = new Size(50, 30),
                Font = new Font("Segoe UI", 9),
                Margin = new Padding(0, 8, 0, 0),
            };
            bottomPanel.Controls.Add(hexLabel);

            hexTextBox = new TextBox
            {
                Font = new Font("Consolas", 11),
                Size = new Size(100, 30),
                MaxLength = 7,
                Text = "#",
                Margin = new Padding(2, 6, 2, 0),
            };
            hexTextBox.TextChanged += HexTextBox_TextChanged;
            bottomPanel.Controls.Add(hexTextBox);

            swatchPanel = new Panel
            {
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                Size = new Size(30, 30),
                Margin = new Padding(2, 6, 6, 0),
            };
            bottomPanel.Controls.Add(swatchPanel);

            pickColorButton = new Button
            {
                Text = "Pick Color...",
                Size = new Size(100, 30),
                Margin = new Padding(2, 6, 2, 0),
                UseVisualStyleBackColor = true,
            };
            pickColorButton.Click += PickColorButton_Click;
            bottomPanel.Controls.Add(pickColorButton);

            var moveGroup = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(6, 6, 0, 0),
            };

            var moveLabel = new Label
            {
                Text = "Move:",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                Margin = new Padding(0, 5, 2, 0),
            };
            moveGroup.Controls.Add(moveLabel);

            moveUpButton = new Button
            {
                Text = "\u25B2",
                Size = new Size(40, 30),
                Margin = new Padding(0),
                UseVisualStyleBackColor = true,
                Font = new Font("Segoe UI", 10),
            };
            moveUpButton.Click += MoveUpButton_Click;
            moveGroup.Controls.Add(moveUpButton);

            moveDownButton = new Button
            {
                Text = "\u25BC",
                Size = new Size(40, 30),
                Margin = new Padding(0),
                UseVisualStyleBackColor = true,
                Font = new Font("Segoe UI", 10),
            };
            moveDownButton.Click += MoveDownButton_Click;
            moveGroup.Controls.Add(moveDownButton);

            bottomPanel.Controls.Add(moveGroup);

            mainPanel.Controls.Add(bottomPanel, 0, 2);

            statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.Gray,
            };
            mainPanel.Controls.Add(statusLabel, 0, 3);

            this.Controls.Add(mainPanel);
        }

        private void ColorListBox_MeasureItem(object sender, MeasureItemEventArgs e)
        {
            e.ItemHeight = 48;
        }

        private void ColorListBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || colorEntries == null || e.Index >= colorEntries.Count) return;
            var entry = colorEntries[e.Index];

            e.DrawBackground();

            var rect = e.Bounds;
            var g = e.Graphics;

            // Draw swatch
            var swatchRect = new Rectangle(rect.X + 8, rect.Y + 6, 36, 36);
            using (var brush = new SolidBrush(entry.Color))
            {
                g.FillRectangle(brush, swatchRect);
            }
            g.DrawRectangle(Pens.DarkGray, swatchRect);

            // Draw extruder number
            using (var font = new Font("Segoe UI", 10, FontStyle.Bold))
            using (var brush = new SolidBrush(e.ForeColor))
            {
                g.DrawString("Extruder " + entry.Extruder, font, brush,
                    rect.X + 52, rect.Y + 6);
            }

            // Draw hex value
            using (var font = new Font("Consolas", 10))
            using (var brush = new SolidBrush(Color.Gray))
            {
                g.DrawString(entry.Hex, font, brush,
                    rect.X + 52, rect.Y + 26);
            }

            // Draw selection highlight
            if ((e.State & DrawItemState.Selected) != 0)
            {
                using (var highlight = new SolidBrush(Color.FromArgb(60, SystemColors.Highlight)))
                {
                    g.FillRectangle(highlight, e.Bounds);
                }
                g.DrawRectangle(SystemPens.Highlight,
                    e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            }

            // Draw drag handle hint
            using (var font = new Font("Segoe UI", 8))
            using (var brush = new SolidBrush(Color.FromArgb(180, 180, 180)))
            {
                g.DrawString("\u2261", font, brush, rect.Right - 20, rect.Y + 14);
            }

            e.DrawFocusRectangle();
        }

        private void ColorListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (colorListBox.SelectedIndex >= 0 && colorEntries != null &&
                colorListBox.SelectedIndex < colorEntries.Count)
            {
                var entry = colorEntries[colorListBox.SelectedIndex];
                hexTextBox.Text = entry.Hex;
                swatchPanel.BackColor = entry.Color;
                hexTextBox.Enabled = true;
                pickColorButton.Enabled = true;
                moveUpButton.Enabled = colorListBox.SelectedIndex > 0;
                moveDownButton.Enabled = colorListBox.SelectedIndex < colorEntries.Count - 1;
            }
            else
            {
                hexTextBox.Enabled = false;
                pickColorButton.Enabled = false;
                moveUpButton.Enabled = false;
                moveDownButton.Enabled = false;
            }
        }

        // Drag and drop reordering
        private void ColorListBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (colorEntries == null) return;
            int index = colorListBox.IndexFromPoint(e.Location);
            if (index >= 0 && index < colorEntries.Count)
            {
                dragSourceIndex = index;
                dragStartPoint = e.Location;
            }
        }

        private void ColorListBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && dragSourceIndex >= 0 && colorEntries != null)
            {
                var dragSize = SystemInformation.DragSize;
                var dragRect = new Rectangle(dragStartPoint.X - dragSize.Width / 2,
                    dragStartPoint.Y - dragSize.Height / 2, dragSize.Width, dragSize.Height);
                if (!dragRect.Contains(e.Location))
                {
                    colorListBox.DoDragDrop(dragSourceIndex, DragDropEffects.Move);
                    dragSourceIndex = -1;
                }
            }
            else if (e.Button == MouseButtons.None)
            {
                dragSourceIndex = -1;
            }
        }

        private void ColorListBox_DragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.Move;
            int index = colorListBox.IndexFromPoint(colorListBox.PointToClient(new Point(e.X, e.Y)));
            if (index >= 0 && index < colorListBox.Items.Count)
                colorListBox.SelectedIndex = index;
        }

        private void ColorListBox_DragDrop(object sender, DragEventArgs e)
        {
            if (dragSourceIndex < 0 || colorEntries == null) return;
            int targetIndex = colorListBox.IndexFromPoint(colorListBox.PointToClient(new Point(e.X, e.Y)));
            if (targetIndex < 0 || targetIndex >= colorEntries.Count) return;
            if (dragSourceIndex == targetIndex) return;

            var item = colorEntries[dragSourceIndex];
            colorEntries.RemoveAt(dragSourceIndex);
            colorEntries.Insert(targetIndex, item);

            // Renumber extruders
            for (int i = 0; i < colorEntries.Count; i++)
                colorEntries[i].Extruder = i + 1;

            RefreshList();
            colorListBox.SelectedIndex = targetIndex;
            dragSourceIndex = -1;
            SetModified();
        }

        private void MoveUpButton_Click(object sender, EventArgs e)
        {
            int idx = colorListBox.SelectedIndex;
            if (idx <= 0 || colorEntries == null) return;

            var item = colorEntries[idx];
            colorEntries.RemoveAt(idx);
            colorEntries.Insert(idx - 1, item);
            for (int i = 0; i < colorEntries.Count; i++)
                colorEntries[i].Extruder = i + 1;
            RefreshList();
            colorListBox.SelectedIndex = idx - 1;
            SetModified();
        }

        private void MoveDownButton_Click(object sender, EventArgs e)
        {
            int idx = colorListBox.SelectedIndex;
            if (idx < 0 || idx >= colorEntries.Count - 1 || colorEntries == null) return;

            var item = colorEntries[idx];
            colorEntries.RemoveAt(idx);
            colorEntries.Insert(idx + 1, item);
            for (int i = 0; i < colorEntries.Count; i++)
                colorEntries[i].Extruder = i + 1;
            RefreshList();
            colorListBox.SelectedIndex = idx + 1;
            SetModified();
        }

        private void HexTextBox_TextChanged(object sender, EventArgs e)
        {
            if (colorListBox.SelectedIndex < 0 || colorEntries == null ||
                colorListBox.SelectedIndex >= colorEntries.Count) return;

            var text = hexTextBox.Text.Trim();
            if (!Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$")) return;

            var entry = colorEntries[colorListBox.SelectedIndex];
            entry.Hex = text.ToUpper();
            entry.Color = ColorEntry.HexToColor(text);
            swatchPanel.BackColor = entry.Color;
            RefreshList();
            SetModified();
        }

        private void PickColorButton_Click(object sender, EventArgs e)
        {
            if (colorListBox.SelectedIndex < 0 || colorEntries == null ||
                colorListBox.SelectedIndex >= colorEntries.Count) return;

            using (var cd = new ColorDialog())
            {
                cd.Color = colorEntries[colorListBox.SelectedIndex].Color;
                cd.AnyColor = true;
                cd.FullOpen = true;
                if (cd.ShowDialog() == DialogResult.OK)
                {
                    var hex = "#" + cd.Color.R.ToString("X2") + cd.Color.G.ToString("X2") + cd.Color.B.ToString("X2");
                    hexTextBox.Text = hex;
                }
            }
        }

        private void OpenFile()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "3MF Files (*.3mf)|*.3mf|All Files (*.*)|*.*";
                ofd.Title = "Open 3MF File";
                if (ofd.ShowDialog() == DialogResult.OK)
                    LoadFile(ofd.FileName);
            }
        }

        private void LoadFile(string filePath)
        {
            try
            {
                var entries = new List<ColorEntry>();
                string jsonContent = null;

                using (var zip = ZipFile.OpenRead(filePath))
                {
                    var configEntry = zip.Entries
                        .FirstOrDefault(e => e.FullName.EndsWith("project_settings.config", StringComparison.OrdinalIgnoreCase)
                            || e.FullName.EndsWith("Metadata/project_settings.config", StringComparison.OrdinalIgnoreCase));

                    if (configEntry == null)
                    {
                        // Try to find filament_colour in any config
                        configEntry = zip.Entries
                            .FirstOrDefault(e => e.Name.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
                                && e.FullName.Contains("project"));
                    }

                    if (configEntry == null)
                    {
                        MessageBox.Show("Could not find project_settings.config in the 3MF file.\n\nThe file may not contain slicer color data.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    using (var reader = new StreamReader(configEntry.Open()))
                    {
                        jsonContent = reader.ReadToEnd();
                    }
                }

                // Parse filament_colour array
                var match = Regex.Match(jsonContent, FILAMENT_COLOUR_PATTERN);
                if (!match.Success)
                {
                    MessageBox.Show("Could not find filament_colour data in the configuration.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var hexValues = match.Groups[1].Value
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(h => h.Trim().Trim('"', ' ', '\r', '\n', '\t'))
                    .Where(h => Regex.IsMatch(h, "^#[0-9A-Fa-f]{6}$"))
                    .ToList();

                if (hexValues.Count == 0)
                {
                    MessageBox.Show("No valid hex color values found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                for (int i = 0; i < hexValues.Count; i++)
                {
                    entries.Add(new ColorEntry
                    {
                        Extruder = i + 1,
                        Hex = hexValues[i].ToUpper(),
                        Color = ColorEntry.HexToColor(hexValues[i]),
                    });
                }

                colorEntries = entries;
                currentFilePath = filePath;
                RefreshList();
                if (colorEntries.Count > 0)
                    colorListBox.SelectedIndex = 0;

                statusLabel.Text = "Loaded: " + Path.GetFileName(filePath) + " — " + colorEntries.Count + " extruders";
                statusLabel.ForeColor = Color.Gray;
                this.Text = "3MF Toolset — " + Path.GetFileName(filePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading file:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshList()
        {
            colorListBox.Items.Clear();
            if (colorEntries == null) return;
            foreach (var entry in colorEntries)
                colorListBox.Items.Add(entry);
        }

        private bool modified = false;
        private void SetModified()
        {
            modified = true;
            if (!this.Text.EndsWith(" *"))
                this.Text += " *";
        }

        private void SaveFile()
        {
            if (string.IsNullOrEmpty(currentFilePath))
            {
                SaveAsFile();
                return;
            }
            SaveTo(currentFilePath);
        }

        private void SaveAsFile()
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "3MF Files (*.3mf)|*.3mf|All Files (*.*)|*.*";
                sfd.Title = "Save 3MF File";
                sfd.FileName = !string.IsNullOrEmpty(currentFilePath)
                    ? Path.GetFileName(currentFilePath)
                    : "output.3mf";
                if (sfd.ShowDialog() == DialogResult.OK)
                    SaveTo(sfd.FileName);
            }
        }

        private void SaveTo(string filePath)
        {
            try
            {
                if (colorEntries == null || string.IsNullOrEmpty(currentFilePath))
                {
                    MessageBox.Show("No file loaded.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Build new filament_colour JSON array
                var newColors = "[" + string.Join(", ",
                    colorEntries.Select(e => "\"" + e.Hex + "\"")) + "]";

                // Read original zip
                string jsonContent;
                string targetEntryPath = null;

                using (var zip = ZipFile.OpenRead(currentFilePath))
                {
                    var configEntry = zip.Entries
                        .FirstOrDefault(e => e.FullName.EndsWith("project_settings.config", StringComparison.OrdinalIgnoreCase)
                            || e.FullName.EndsWith("Metadata/project_settings.config", StringComparison.OrdinalIgnoreCase));

                    if (configEntry == null)
                    {
                        MessageBox.Show("Could not find project_settings.config to update.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    targetEntryPath = configEntry.FullName;

                    using (var reader = new StreamReader(configEntry.Open()))
                    {
                        jsonContent = reader.ReadToEnd();
                    }
                }

                // Replace filament_colour in JSON
                jsonContent = Regex.Replace(jsonContent, FILAMENT_COLOUR_PATTERN,
                    "\"filament_colour\": " + newColors);

                // Repack with proper OPC structure (Content_Types.xml first)
                if (File.Exists(filePath))
                    File.Delete(filePath);

                using (var zip = ZipFile.Open(filePath, ZipArchiveMode.Create))
                {
                    // First pass: collect all entries from original zip
                    var originalEntries = new List<ZipEntryData>();
                    using (var originalZip = ZipFile.OpenRead(currentFilePath))
                    {
                        foreach (var entry in originalZip.Entries)
                        {
                            using (var ms = new MemoryStream())
                            using (var stream = entry.Open())
                            {
                                stream.CopyTo(ms);
                                originalEntries.Add(new ZipEntryData(entry.FullName, ms.ToArray()));
                            }
                        }
                    }

                    // Write [Content_Types].xml first, then everything else
                    var contentTypes = originalEntries.FirstOrDefault(e =>
                        e.Name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
                    if (contentTypes != null)
                    {
                        var ce = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
                        using (var writer = new BinaryWriter(ce.Open()))
                            writer.Write(contentTypes.Data);
                    }

                    foreach (var entryData in originalEntries)
                    {
                        if (entryData.Name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var entryName = entryData.Name;
                        // If this is the config we modified, write new content
                        if (entryData.Name.Equals(targetEntryPath, StringComparison.OrdinalIgnoreCase))
                        {
                            var ce = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                            using (var writer = new StreamWriter(ce.Open()))
                                writer.Write(jsonContent);
                        }
                        else
                        {
                            var ce = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                            using (var writer = new BinaryWriter(ce.Open()))
                                writer.Write(entryData.Data);
                        }
                    }
                }

                currentFilePath = filePath;
                modified = false;
                this.Text = "3MF Toolset — " + Path.GetFileName(filePath);
                statusLabel.Text = "Saved: " + Path.GetFileName(filePath);
                statusLabel.ForeColor = Color.DarkGreen;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving file:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                "3MF Toolset v1.0\n\n" +
                "Edit extruder filament colors in .3mf files.\n\n" +
                "Built with .NET Framework 4.x + WinForms\n" +
                "3MF is a trademark of the 3MF Consortium",
                "About 3MF Toolset",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (modified)
            {
                var result = MessageBox.Show("Save changes before closing?", "Unsaved Changes",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (result == DialogResult.Yes)
                    SaveFile();
                else if (result == DialogResult.Cancel)
                    e.Cancel = true;
            }
            base.OnFormClosing(e);
        }
    }
}
