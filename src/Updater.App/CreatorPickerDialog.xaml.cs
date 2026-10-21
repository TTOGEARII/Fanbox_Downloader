using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Updater.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Updater.App;

/// <summary>팔로우/후원 작가 목록에서 폴더에 매핑할 작가를 고르는 창.
/// ShowDialog()가 true면 <see cref="Selected"/>가 매핑할 작가, <see cref="Unmapped"/>가 true면 매핑 해제 요청.</summary>
public partial class CreatorPickerDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<CreatorInfo> _all;

    public CreatorInfo? Selected { get; private set; }
    public bool Unmapped { get; private set; }

    public CreatorPickerDialog(Window owner, string folderName, IEnumerable<CreatorInfo> creators, string? currentId)
    {
        InitializeComponent();
        Owner = owner;
        _all = creators.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        TxtPrompt.Text = $"'{folderName}' 폴더와 연결할 작가를 선택하세요.";
        BtnUnmap.Visibility = string.IsNullOrEmpty(currentId) ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter();

        if (!string.IsNullOrEmpty(currentId))
        {
            var cur = _all.FirstOrDefault(c => string.Equals(c.CreatorId, currentId, StringComparison.OrdinalIgnoreCase));
            if (cur != null) { List.SelectedItem = cur; List.ScrollIntoView(cur); }
        }
        Loaded += (_, _) => TxtSearch.Focus();
    }

    private void ApplyFilter()
    {
        var q = TxtSearch.Text.Trim();
        var items = q.Length == 0
            ? _all
            : _all.Where(c => c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || c.CreatorId.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var keep = List.SelectedItem;
        List.ItemsSource = items;
        if (keep != null && items.Contains(keep)) List.SelectedItem = keep;
        else if (q.Length > 0 && items.Count > 0) List.SelectedIndex = 0;
        TxtCount.Text = q.Length == 0 ? $"작가 {_all.Count}명" : $"{items.Count} / {_all.Count}명";
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (List.SelectedItem is not CreatorInfo c) return;
        Selected = c;
        DialogResult = true;
    }

    private void BtnUnmap_Click(object sender, RoutedEventArgs e)
    {
        Unmapped = true;
        DialogResult = true;
    }
}
