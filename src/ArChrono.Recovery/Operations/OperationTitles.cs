using ArChrono.Localization;

namespace ArChrono.Recovery.Operations;

/// <summary>
/// Undo/redo başlıkları. İşlem başlıkları oluşturuldukları dilde saklanır;
/// dil sonradan değişse de önekler her iki dilde tanınır.
/// </summary>
public static class OperationTitles
{
    private const string UndoEnglish = "Undo ";
    private const string UndoTurkish = "Geri al: ";
    private const string RedoEnglish = "Redo ";
    private const string RedoTurkish = "Yinele: ";

    public static string UndoPrefix => Loc.T(UndoEnglish, UndoTurkish);

    public static string RedoPrefix => Loc.T(RedoEnglish, RedoTurkish);

    public static string Undo(string title) => UndoPrefix + title;

    /// <summary>Bir undo işleminin başlığından yineleme başlığı üretir.</summary>
    public static string Redo(string undoTitle) => RedoPrefix + StripUndoPrefix(undoTitle);

    public static string StripUndoPrefix(string title) =>
        title.StartsWith(UndoEnglish, StringComparison.Ordinal) ? title[UndoEnglish.Length..]
        : title.StartsWith(UndoTurkish, StringComparison.Ordinal) ? title[UndoTurkish.Length..]
        : title;
}
