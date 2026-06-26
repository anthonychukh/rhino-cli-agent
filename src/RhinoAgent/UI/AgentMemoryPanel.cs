using System.Runtime.InteropServices;
using Eto.Forms;
using Rhino;
using Rhino.UI;
using RhinoAgent.Memory;

namespace RhinoAgent.UI;

[Guid("D8BAE3B3-53EA-42BB-8586-BC6976433C3B")]
public sealed class AgentMemoryPanel : Eto.Forms.Panel
{
    private static bool Registered;
    private static bool RegisterOnIdlePending;
    private static System.Drawing.Icon? PanelIcon;
    private static RhinoAgentPlugin? RegisteredPlugin;

    private readonly TextArea _editor = new();
    private readonly Label _status = new();
    private bool _disposed;
    private bool _loading;

    public AgentMemoryPanel()
    {
        BuildUi();
        AgentMemoryStore.MemoryChanged += OnMemoryChanged;
        RefreshFromDocument();
    }

    public static void Register(RhinoAgentPlugin plugin)
    {
        RegisteredPlugin = plugin;
        if (!TryRegisterPanel(logFailure: false))
            ScheduleRegisterOnIdle();
    }

    public static void Shutdown()
    {
        if (RegisterOnIdlePending)
        {
            RhinoApp.Idle -= OnRhinoIdle;
            RegisterOnIdlePending = false;
        }

        try
        {
            if (Registered)
                Panels.ClosePanel(typeof(AgentMemoryPanel));
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"RhinoAgent could not close the Agent Memory panel during shutdown: {ex.Message}");
        }

        PanelIcon?.Dispose();
        PanelIcon = null;
        RegisteredPlugin = null;
        Registered = false;
    }

    public static bool OpenPanel()
    {
        if (!TryRegisterPanel(logFailure: true))
            return false;

        Panels.OpenPanel(typeof(AgentMemoryPanel));
        return true;
    }

    private static void ScheduleRegisterOnIdle()
    {
        if (RegisterOnIdlePending)
            return;

        RegisterOnIdlePending = true;
        RhinoApp.Idle += OnRhinoIdle;
    }

    private static void OnRhinoIdle(object? sender, EventArgs e)
    {
        RhinoApp.Idle -= OnRhinoIdle;
        RegisterOnIdlePending = false;
        if (!Registered)
            TryRegisterPanel(logFailure: true);
    }

    private static bool TryRegisterPanel(bool logFailure)
    {
        if (Registered)
            return true;

        try
        {
            var plugin = RegisteredPlugin ?? RhinoAgentPlugin.Instance;
            if (plugin is null)
                throw new InvalidOperationException("RhinoAgent plugin instance is not available.");

            PanelIcon ??= CreatePanelIcon();
            Panels.RegisterPanel(plugin, typeof(AgentMemoryPanel), "Agent Memory", PanelIcon, PanelType.PerDoc);
            Registered = true;
            return true;
        }
        catch (Exception ex)
        {
            if (logFailure)
                RhinoApp.WriteLine($"RhinoAgent could not register the Agent Memory panel: {ex.Message}");
            return false;
        }
    }

    private static System.Drawing.Icon? CreatePanelIcon()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var bitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 67, 150, 96));
                using var rim = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 31, 92, 61), 2);
                using var note = new System.Drawing.Pen(System.Drawing.Color.White, 3)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                };

                graphics.FillEllipse(fill, 2, 2, 28, 28);
                graphics.DrawEllipse(rim, 2, 2, 28, 28);
                graphics.DrawLine(note, 10, 21, 10, 10);
                graphics.DrawLine(note, 10, 10, 16, 15);
                graphics.DrawLine(note, 16, 15, 22, 9);
                graphics.DrawLine(note, 22, 9, 22, 21);
            }

            var iconHandle = bitmap.GetHicon();
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(iconHandle);
                return (System.Drawing.Icon)icon.Clone();
            }
            finally
            {
                DestroyIcon(iconHandle);
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"RhinoAgent could not create the Agent Memory panel icon: {ex.Message}");
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
                AgentMemoryStore.MemoryChanged -= OnMemoryChanged;

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        _editor.Wrap = true;

        var save = Button("Save", SaveToDocument);
        var refresh = Button("Refresh", RefreshFromDocument);
        var undo = Button("Undo", Undo);
        var history = Button("History", ShowHistory);
        var import = Button("Import", Import);
        var export = Button("Export", Export);
        var reset = Button("Reset", Reset);
        var toggle = Button("On/Off", ToggleEnabled);

        var buttons = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Items = { save, refresh, undo, history, import, export, reset, toggle }
        };

        var layout = new DynamicLayout
        {
            Padding = 6,
            DefaultSpacing = new Eto.Drawing.Size(4, 4)
        };
        layout.Add(buttons);
        layout.Add(_status);
        layout.Add(_editor, yscale: true);
        Content = layout;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text };
        button.Click += (_, _) => action();
        return button;
    }

    private void RefreshFromDocument()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            _editor.Text = "";
            _status.Text = "No active document.";
            return;
        }

        _loading = true;
        try
        {
            var state = AgentMemoryStore.EnsureCreated(doc);
            _editor.Text = state.Markdown;
            _status.Text = BuildStatus(state);
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveToDocument()
    {
        if (_loading)
            return;

        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
            return;

        var result = AgentMemoryStore.SaveUserMarkdown(doc, _editor.Text, "Saved from Agent Memory panel.");
        _status.Text = $"{result.Message} {BuildStatus(result.State)}";
    }

    private void Undo()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
            return;

        var result = AgentMemoryStore.Undo(doc, 1);
        _status.Text = result.Message;
        RefreshFromDocument();
    }

    private void ShowHistory()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
            return;

        MessageBox.Show(this, AgentMemoryStore.DescribeHistory(doc), "Agent Memory History");
    }

    private void Import()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
            return;

        var dialog = new Eto.Forms.OpenFileDialog
        {
            Title = "Import RhinoAgent Memory",
            MultiSelect = false
        };
        dialog.Filters.Add(new FileFilter("Markdown", ".md", ".markdown", ".txt"));
        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        var result = AgentMemoryStore.ImportMarkdown(doc, dialog.FileName);
        _status.Text = result.Message;
        RefreshFromDocument();
    }

    private void Export()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
            return;

        var dialog = new Eto.Forms.SaveFileDialog
        {
            Title = "Export RhinoAgent Memory",
            FileName = "RhinoAgentMemory.md"
        };
        dialog.Filters.Add(new FileFilter("Markdown", ".md"));
        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        var path = AgentMemoryStore.ExportMarkdown(doc, dialog.FileName);
        _status.Text = $"Exported {path}";
    }

    private void Reset()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
            return;

        var answer = MessageBox.Show(
            this,
            "Reset this document's RhinoAgent memory? The current memory will be kept in history.",
            "Reset Agent Memory",
            MessageBoxButtons.YesNo,
            MessageBoxType.Question);
        if (answer != DialogResult.Yes)
            return;

        var result = AgentMemoryStore.Reset(doc);
        _status.Text = result.Message;
        RefreshFromDocument();
    }

    private void ToggleEnabled()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
            return;

        var state = AgentMemoryStore.EnsureCreated(doc);
        var result = AgentMemoryStore.SetEnabled(doc, !state.Enabled);
        _status.Text = result.Message;
        RefreshFromDocument();
    }

    private void OnMemoryChanged(object? sender, EventArgs e)
    {
        Application.Instance.AsyncInvoke(RefreshFromDocument);
    }

    private static string BuildStatus(AgentMemoryState state)
    {
        var enabled = state.Enabled ? "on" : "off";
        var updated = state.LastUpdatedUtc.HasValue ? state.LastUpdatedUtc.Value.ToLocalTime().ToString("g") : "never";
        var hash = string.IsNullOrWhiteSpace(state.CurrentHash)
            ? "none"
            : state.CurrentHash[..Math.Min(8, state.CurrentHash.Length)];
        return $"Memory {enabled}. {state.Markdown.Length} chars. Updated {updated}. {state.History.Count} snapshots. {hash}.";
    }
}
