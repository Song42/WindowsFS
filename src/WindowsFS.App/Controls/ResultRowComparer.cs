using System.Collections;
using System.ComponentModel;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App.Controls;

public sealed class ResultRowComparer(string property, ListSortDirection direction) : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not ResultRow left || y is not ResultRow right) return 0;
        static int Rank(ResultRow row) => row.Navigation switch
        { FolderNavigationKind.Current => 0, FolderNavigationKind.Parent => 1, _ => 2 };
        var rank = Rank(left).CompareTo(Rank(right));
        if (rank != 0) return rank;
        var comparison = property switch
        {
            nameof(ResultRow.Bytes) => left.Bytes.CompareTo(right.Bytes),
            nameof(ResultRow.Share) => left.Share.CompareTo(right.Share),
            _ => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)
        };
        return direction == ListSortDirection.Descending ? -comparison : comparison;
    }
}
