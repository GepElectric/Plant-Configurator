using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TiaTraceStudio;

/// <summary>
/// Page numbers of the table of contents are known only to Word: when Word is installed, the saved report is opened
/// invisibly, its contents and page references are updated and it is saved again. Nothing happens without Word;
/// a Word instance that does not answer in time is closed (only the one started here).
/// </summary>
public static class WordFields
{
    /// <summary>Why the last update did not succeed (for the status line).</summary>
    public static string LastError { get; private set; } = "";

    public static bool Update(string path, TimeSpan timeout)
    {
        LastError = "";
        if (Type.GetTypeFromProgID("Word.Application") is not Type wordType) return false;
        var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        bool done = false; var started = new List<int>();
        var thread = new Thread(() =>
        {
            dynamic? word = null, document = null;
            try
            {
                word = Activator.CreateInstance(wordType)!;
                lock (started) started.AddRange(Process.GetProcessesByName("WINWORD").Select(p => p.Id).Where(id => !before.Contains(id)));
                word.Visible = false; word.DisplayAlerts = 0;
                document = word.Documents.Open(System.IO.Path.GetFullPath(path), false, false, false);
                foreach (dynamic toc in document.TablesOfContents) toc.Update();
                foreach (dynamic list in document.TablesOfFigures) list.Update();
                document.Fields.Update();
                document.Save();
                done = true;
            }
            catch (Exception ex) { LastError = ex.Message; }
            finally
            {
                try { document?.Close(0); } catch (Exception) { }
                try { word?.Quit(0); } catch (Exception) { }
                if (document != null) Marshal.FinalReleaseComObject(document);
                if (word != null) Marshal.FinalReleaseComObject(word);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(timeout))
        {
            lock (started)
                foreach (int id in started)
                    try { Process.GetProcessById(id).Kill(); } catch (Exception) { }
            return false;
        }
        return done;
    }
}
