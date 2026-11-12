using Luma.Core;
using Microsoft.UI.Xaml;
namespace Luma.App;

public sealed partial class MainWindow
{
    private string workDetail="",workPhase="";
    private DateTime lastWorkUpdate=DateTime.UtcNow;
    private void ImportFeedback(string phase,string summary,string detail,double? percent=null)
    {
        if(!scanning)return;
        Status.Text=summary; workDetail=detail; lastWorkUpdate=DateTime.UtcNow;
        ImportProgress.Visibility=Visibility.Visible;
        ImportProgress.IsIndeterminate=percent is null;
        ImportProgress.Value=Math.Clamp(percent ?? 0,0,100);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(Status,summary+"\n"+detail);
        if(workPhase!=phase)
        {
            workPhase=phase;
            recentActivity.Enqueue($"{DateTime.Now:t} · {summary}");
            if(AppSettings.IsSmokeTest)File.AppendAllText(Path.Combine(AppSettings.LocalRoot,"import-phases.txt"),phase+Environment.NewLine);
        }
    }
    private IProgress<MetadataProgress> MetadataFeedback(string source,string stage="Step 2 of 2")=>new Progress<MetadataProgress>(p=>
        ImportFeedback("metadata",$"{stage} · Reading dates · {p.Checked:N0} / {p.Total:N0} checked",
            $"{source}\n{p.Path}\n{p.Saved:N0} updates saved · {p.Skipped:N0} unavailable or changed files skipped.\nThe library is ready to browse. Pause safely; Refresh metadata continues remaining work.",
            p.Total==0 ? 100 : 100d*p.Checked/p.Total));
    private string ActivityStatus()=>Status.Text+(scanning ? $"\nElapsed {workClock.Elapsed:hh\\:mm\\:ss}\n{workDetail}"+
        ((DateTime.UtcNow-lastWorkUpdate).TotalSeconds>=10 ? $"\nNo new progress report for {(DateTime.UtcNow-lastWorkUpdate).TotalSeconds:N0}s. Drive or codec access can take time; Pause takes effect when the current operation returns." : "") : "");
    private void EndImportFeedback()
    {
        ImportProgress.Visibility=Visibility.Collapsed;workPhase="";workDetail="";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(Status,Status.Text);
    }
}
