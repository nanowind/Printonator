using System.Collections.Generic;
using System.Linq;

namespace Printonator.Core.Printing;

/// <summary>
/// Shared page grouping algorithm — consolidates:
/// - CdpPrintParams.CompactRanges() → returns string "1-3, 5, 8-9"
/// - OfficeComPrintEngine.GroupPages() → returns List<(int From, int To)>
/// Single implementation that both can use.
/// </summary>
public static class PageGrouping
{
    /// <summary>
    /// Group consecutive pages into (From, To) ranges.
    /// Input must be sorted ascending. [1,2,3,5,8,9] → [(1,3), (5,5), (8,9)].
    /// </summary>
    public static List<(int From, int To)> GroupConsecutive(int[] pages)
    {
        var groups = new List<(int, int)>();
        if (pages.Length == 0) return groups;

        int start = pages[0], prev = pages[0];
        for (var i = 1; i < pages.Length; i++)
        {
            if (pages[i] == prev + 1)
            {
                prev = pages[i];
                continue;
            }
            groups.Add((start, prev));
            start = pages[i];
            prev = pages[i];
        }
        groups.Add((start, prev));
        return groups;
    }

    /// <summary>
    /// Compact pages into a display string like "1-3, 5, 8-9".
    /// Used by CdpPrintParams and UI display.
    /// </summary>
    public static string CompactRanges(int[] pages)
    {
        var groups = GroupConsecutive(pages);
        return string.Join(", ", groups.Select(g =>
            g.From == g.To ? g.From.ToString() : $"{g.From}-{g.To}"));
    }

    /// <summary>
    /// Map global page numbers to per-sheet ranges for Excel/PowerPoint printing.
    /// </summary>
    public static List<(dynamic Sheet, List<(int From, int To)> Groups)> MapGlobalPages(
        int[] pages, List<(dynamic Sheet, int Pages, int Start)> sheetPages)
    {
        var result = new List<(dynamic, List<(int, int)>)>();
        foreach (var sp in sheetPages)
        {
            var local = new List<int>();
            foreach (var p in pages)
            {
                var l = p - sp.Start;
                if (l >= 1 && l <= sp.Pages) local.Add(l);
            }
            if (local.Count == 0) continue;
            result.Add((sp.Sheet, GroupConsecutive(local.ToArray())));
        }
        return result;
    }
}