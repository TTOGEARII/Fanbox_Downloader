using System.Windows;

namespace Updater.App;

/// <summary>선택 작가의 게시물을 지정 날짜 이후로 다시 받을지 묻는 창. ShowDialog()가 true면 <see cref="Since"/> 사용.</summary>
public partial class RedownloadDialog : Wpf.Ui.Controls.FluentWindow
{
    public DateTime Since { get; private set; }

    public RedownloadDialog(Window owner, string folderName, string creatorId)
    {
        InitializeComponent();
        Owner = owner;
        TxtTarget.Text = $"{folderName}  ({creatorId})";
        Picker.SelectedDate = DateTime.Today.AddMonths(-3);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var months = int.Parse((string)((FrameworkElement)sender).Tag);
        Picker.SelectedDate = months == 0 ? new DateTime(2000, 1, 1) : DateTime.Today.AddMonths(-months);
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (Picker.SelectedDate == null) return;
        Since = Picker.SelectedDate.Value;
        DialogResult = true;
    }
}
