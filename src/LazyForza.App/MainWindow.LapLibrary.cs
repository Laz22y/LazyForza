using System.Windows;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private async void EditLapRecord(LapAnalysisModule module, LapSummary lap)
    {
        var editor = new LapRecordEditor(this, lap);
        if (editor.ShowDialog() != true) return;
        try
        {
            await module.FlushPendingLapsAsync();
            store.UpdateLapAnnotation(lap.Id, editor.Annotation);
            module.RefreshSelectedTrackHistory();
            RenderSelectedPage(true);
        }
        catch (Exception exception)
        {
            AppDialog.Show(this, AppLocalization.Literal(exception.Message), AppLocalization.Literal("无法保存圈记录"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
