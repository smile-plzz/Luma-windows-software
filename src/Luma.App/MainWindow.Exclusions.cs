using Luma.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Luma.App;
public sealed partial class MainWindow
{
    private MediaCard? contextCard;
    private void ExclusionMenuClosed(object sender,object e)=>contextCard=null;
    private async void ExcludeFolder(object sender,RoutedEventArgs e)=>await ExcludeSelected("folder");
    private async void ExcludeSource(object sender,RoutedEventArgs e)=>await ExcludeSelected("source");
    private async void ExcludeType(object sender,RoutedEventArgs e)=>await ExcludeSelected("type");
    private async Task ExcludeSelected(string kind)
    {
        if(operation || scanning) { Notify("Pause the current work in Activity before changing exclusions."); return; }
        var media=(contextCard ?? PrimarySelectedCard())?.Item.Media;if(media is null)return;
        var value=kind=="type" ? Path.GetExtension(media.RelativePath) : kind=="folder" ? Path.GetDirectoryName(media.RelativePath) ?? "" : "";
        if(kind=="folder" && value.Length==0)kind="source";
        await Guard(()=>ConfirmExclusion(new(kind,kind=="type" ? "" : media.SourceId,value)),true);
    }
    private async Task ConfirmExclusion(LibraryExclusion rule)
    {
        var sources=await Task.Run(()=>catalog.SourcesAsync());
        var root=sources.FirstOrDefault(s=>s.Id==rule.SourceId)?.RootPath ?? rule.SourceId;
        var scope=rule.Kind switch { "type"=>$"All {rule.Value.ToLowerInvariant()} files across every source", "source"=>$"Entire source: {root}",_=>$"Folder and subfolders: {Path.Combine(root,rule.Value)}" };
        if(!await Confirm("Exclude from Luma?",scope+"\n\nHide matching media and skip it in future scans. Originals and saved organization stay intact. Affected paused scan checkpoints will restart. Restore the rule in Manage exclusions to show saved media again.","Exclude"))return;
        await Task.Run(()=>catalog.SetExclusionAsync(rule,true));
        await LoadAlbums(); await Refresh();
        Notify("Exclusion saved. Originals are untouched. Restore it in Manage exclusions.");
    }
    private async void ManageExclusions(object sender,RoutedEventArgs e)=>await Guard(OpenExclusions,true);
    private async Task OpenExclusions()
    {
        bool again=true;
        while(again)
        {
            again=false;Func<Task>? action=null;
            var rules=await catalog.ExclusionsAsync();var sources=await Task.Run(()=>catalog.SourcesAsync());
            var panel=new StackPanel { Spacing=12,MinWidth=360 };
            panel.Children.Add(Description("Excluded media is hidden and skipped during scanning and preview preparation. Restore keeps saved organization; refresh the source to discover files skipped while excluded. Reset filters does not remove these rules."));
            var dialog=new ContentDialog { RequestedTheme=Root.ActualTheme, XamlRoot=Root.XamlRoot,Title="Library exclusions",Content=new ScrollViewer {Content=panel,MaxHeight=460},CloseButtonText="Done" };
            void Choose(Func<Task> next) { action=next;again=true;dialog.Hide(); }
            if(rules.Count==0)panel.Children.Add(Description("No exclusions. Everything supported is included."));
            foreach(var rule in rules)
            {
                var root=sources.FirstOrDefault(s=>s.Id==rule.SourceId)?.RootPath ?? "Saved source";
                var label=rule.Kind switch { "type"=>$"File type: {rule.Value} · all sources", "source"=>$"Source: {root}",_=>$"Folder: {Path.Combine(root,rule.Value)}" };
                panel.Children.Add(Description(label));
                var restore=ActionButton("Restore",()=>Choose(async ()=>
                {
                    await Task.Run(()=>catalog.SetExclusionAsync(rule,false));await LoadAlbums();await Refresh();
                    Notify("Exclusion restored. Saved media is visible unless another rule excludes it. Refresh to discover previously skipped files.");
                }));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(restore,"Restore "+label);panel.Children.Add(restore);
            }
            var type=new TextBox {Header="Exclude a file type across all sources",PlaceholderText=".jpg or .mp4",MaxLength=20};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(type,"ExcludedExtension");panel.Children.Add(type);
            panel.Children.Add(ActionButton("Exclude file type",()=>Choose(async ()=>
            {
                var extension=type.Text.Trim().ToLowerInvariant();if(!extension.StartsWith('.'))extension="."+extension;
                if(extension.Length<2 || !extension[1..].All(char.IsAsciiLetterOrDigit)) {Notify("Enter an extension such as .jpg or .mp4.",true);return;}
                await ConfirmExclusion(new("type","",extension));
            })));
            if(sources.Count>0)
            {
                var picker=new ComboBox { Header="Registered source",ItemsSource=sources.Select(s=>new SourceChoice(s.Id,s.RootPath)).ToList(),SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch };
                panel.Children.Add(picker);panel.Children.Add(ActionButton("Exclude selected source",()=>Choose(()=>ConfirmExclusion(new("source",((SourceChoice)picker.SelectedItem).Id!,"")))));
                panel.Children.Add(ActionButton("Exclude a folder…",()=>Choose(async ()=>
                {
                    var folder=await PickFolder();if(folder is null)return;
                    var source=sources.OrderByDescending(s=>s.RootPath.Length).FirstOrDefault(s=>folder.Equals(Path.TrimEndingDirectorySeparator(s.RootPath),StringComparison.OrdinalIgnoreCase) || folder.StartsWith(Path.TrimEndingDirectorySeparator(s.RootPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
                    if(source is null) {Notify("Choose a folder within a registered source.",true);return;}
                    var relative=Path.GetRelativePath(source.RootPath,folder);
                    await ConfirmExclusion(new(relative=="." ? "source" : "folder",source.Id,relative=="." ? "" : relative));
                })));
            }
            await dialog.ShowAsync();if(action is not null)await action();
        }
    }
}
