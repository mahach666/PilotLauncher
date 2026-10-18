namespace PilotLauncher.Core.Notes;

/// <param name="Text">Item text without markup; sub-items of a nested list follow on lines starting with "- ".</param>
/// <param name="Product">
/// Product the item is about when the page says so: the "Product:" prefix of a fix or the sub heading above the item.
/// Null for plain items, then the title of their row applies.
/// </param>
public sealed record NoteItem(string Text, string? Product);

/// <param name="Name">Row title as written on the page, e.g. <c>Pilot-ICE Desktop</c>.</param>
public sealed record ProductNotes(string Name, IReadOnlyList<NoteItem> Items);

/// <param name="Notices">Release-wide warnings ("Внимание! ...") set apart from the product rows.</param>
/// <param name="Products">Rows with new features by product.</param>
/// <param name="Fixes">The "Исправлены ошибки" row; each item names its product.</param>
public sealed record ReleaseNotes(
    Version Version,
    DateTime? Date,
    IReadOnlyList<string> Notices,
    IReadOnlyList<ProductNotes> Products,
    IReadOnlyList<NoteItem> Fixes);
