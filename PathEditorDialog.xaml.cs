using System.Windows;
using AiSecretary.Desktop.Services;
using Wpf.Ui.Controls;

namespace AiSecretary.Desktop;

/// <summary>Windows 11 Fluent dialog for adding/editing a trusted folder path.</summary>
public partial class PathEditorDialog : FluentWindow
{
    public string? ResultPath { get; private set; }

    public PathEditorDialog(Window owner, string title, string initialPath, bool isDark)
    {
        FluentThemeHelper.EnsureSystemAccent();
        InitializeComponent();
        Owner = owner;
        Title = title;
        DialogTitleBar.Title = title;
        PathBox.Text = initialPath;

        SourceInitialized += (_, _) => FluentThemeHelper.Apply(this, isDark);
        Loaded += (_, _) =>
        {
            FluentThemeHelper.Apply(this, isDark);
            PathBox.Focus();
            PathBox.SelectAll();
        };
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "選擇信任資料夾" };
        if (!string.IsNullOrWhiteSpace(PathBox.Text) && System.IO.Directory.Exists(PathBox.Text))
            picker.InitialDirectory = PathBox.Text;
        if (picker.ShowDialog(this) == true)
            PathBox.Text = picker.FolderName;
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        ResultPath = text;
        DialogResult = true;
    }
}

