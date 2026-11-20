using Deckard.Core.Builds;
using Deckard.Core.Items;

namespace Deckard.Perception.Paragon;

/// <summary>What a mark on the in-game paragon grid asks of the player.</summary>
public enum GuideKind
{
    /// <summary>A node the build takes, on a board attached in game.</summary>
    Take,
    /// <summary>A glyph socket the build fills; the label names the glyph.</summary>
    Socket,
    /// <summary>A node cell of the next board to attach, at its place and turned as the build has it.</summary>
    Ghost,
}

/// <param name="Label">Text beside the mark (the glyph, or which board to attach and how to turn it), or null.</param>
public sealed record GuideMark(double X, double Y, GuideKind Kind, string? Label = null);

/// <summary>
/// The paragon assistant's marks for a build step on the located grid: the build's nodes on the boards already attached,
/// their glyph sockets named with the glyph and level the build uses, and the next board to attach drawn whole where it
/// goes, turned as the build has it, so the in-game preview can be turned until it matches.
/// </summary>
public static class ParagonGuide
{
    /// <param name="covered">Whether something (a game tooltip) is over a cell of an attached board: no mark there.</param>
    /// <param name="taken">Whether the player has taken a cell's node already: its mark is done (a socket keeps its label).</param>
    public static List<GuideMark> Marks(ParagonPlacement grid, ParagonStep step, ParagonData paragon,
        Func<int, int, bool>? covered = null, Func<int, int, bool>? taken = null)
    {
        var marks = new List<GuideMark>();
        for (var b = 0; b < step.Boards.Count; b++)
        {
            var board = step.Boards[b];
            if (!grid.IsAttached(b) || paragon.Boards.GetValueOrDefault(board.Id) is not { } data)
                continue;
            foreach (var i in board.Nodes)
            {
                var cell = board.Cell(i, data.Width);
                var socket = IsSocket(data, i, paragon);
                if (!socket && (covered?.Invoke(cell.X, cell.Y) == true || taken?.Invoke(cell.X, cell.Y) == true))
                    continue; // an empty socket is a dark ring, always "covered": it keeps its mark
                var (x, y) = At(grid, board, i, data);
                marks.Add(socket && board.Glyph is not null
                    ? new GuideMark(x, y, GuideKind.Socket, GlyphLabel(board, paragon))
                    : new GuideMark(x, y, GuideKind.Take));
            }
        }
        if (NextBoard(grid, step) is { } next && paragon.Boards.GetValueOrDefault(step.Boards[next].Id) is { } nextData)
        {
            var board = step.Boards[next];
            for (var i = 0; i < nextData.Nodes.Count; i++)
                if (nextData.Nodes[i] is not null)
                {
                    var (x, y) = At(grid, board, i, nextData);
                    marks.Add(new GuideMark(x, y, GuideKind.Ghost));
                }
            // The label at the board's middle cell (the middle cell is the same whichever way the board is turned).
            var middle = nextData.Width / 2 * nextData.Width + nextData.Width / 2;
            var (mx, my) = At(grid, board, middle, nextData);
            marks.Add(new GuideMark(mx, my, GuideKind.Ghost, $"Attach {nextData.Name} here: {Turns(board.Rotation)}"));
        }
        return marks;
    }

    /// <summary>
    /// What to pick in the game's Board Selection panel: the next board (or, its place out of view, the first in the
    /// build's order not attached that touches one that is), e.g. "Pick Shield Bearer with the arrows, then Preview".
    /// Null when every board is attached.
    /// </summary>
    public static string? PickHint(ParagonPlacement grid, ParagonStep step, ParagonData paragon)
    {
        var attached = Enumerable.Range(0, step.Boards.Count).Where(grid.IsAttached).Select(b => step.Boards[b]).ToList();
        var next = NextBoard(grid, step) ?? Enumerable.Range(0, step.Boards.Count).Cast<int?>().FirstOrDefault(b =>
            !grid.IsAttached(b!.Value) && attached.Any(a => Math.Abs(a.X - step.Boards[b.Value].X) + Math.Abs(a.Y - step.Boards[b.Value].Y) == 1));
        if (next is not { } b)
            return null;
        var name = paragon.Boards.GetValueOrDefault(step.Boards[b].Id)?.Name ?? step.Boards[b].Id;
        return $"Pick {name} with the arrows, then Preview";
    }

    /// <summary>
    /// The next board to attach: the first in the build's order whose place is in view and empty and that touches an
    /// attached board (boards attach gate to gate, so only a neighbour can come next). A board's place out of view says
    /// nothing (it may be attached and scrolled off), so no hint goes there. Null when there's none.
    /// </summary>
    public static int? NextBoard(ParagonPlacement grid, ParagonStep step)
    {
        var attached = Enumerable.Range(0, step.Boards.Count).Where(grid.IsAttached).Select(b => step.Boards[b]).ToList();
        for (var b = 0; b < step.Boards.Count; b++)
            if (!grid.IsAttached(b) && grid.IsEmpty(b) && attached.Any(a => Math.Abs(a.X - step.Boards[b].X) + Math.Abs(a.Y - step.Boards[b].Y) == 1))
                return b;
        return null;
    }

    /// <summary>
    /// The step as a checklist, one line per board in the build's order: where to attach it (next to which earlier
    /// board), how to turn it, how many points it takes and which glyph goes in its socket; then the points in all.
    /// </summary>
    public static List<string> Checklist(ParagonStep step, ParagonData paragon)
    {
        var lines = new List<string>();
        for (var b = 0; b < step.Boards.Count; b++)
        {
            var board = step.Boards[b];
            var name = paragon.Boards.GetValueOrDefault(board.Id)?.Name ?? board.Id;
            var where = b == 0 ? name : step.Boards.Take(b).FirstOrDefault(a => Math.Abs(a.X - board.X) + Math.Abs(a.Y - board.Y) == 1) is { } before
                ? $"{name}, attached {Side(before, board)} {paragon.Boards.GetValueOrDefault(before.Id)?.Name ?? before.Id}, {Turns(board.Rotation)}"
                : $"{name}, {Turns(board.Rotation)}";
            var glyph = board.Glyph is null ? "" : $"; {GlyphLabel(board, paragon)} in its socket";
            lines.Add($"{b + 1}. {where}: {board.Nodes.Count} point{(board.Nodes.Count == 1 ? "" : "s")}{glyph}.");
        }
        lines.Add($"{step.Boards.Sum(b => b.Nodes.Count)} points in all.");
        return lines;
    }

    private static string Side(PlacedBoard from, PlacedBoard to) =>
        (to.X - from.X, to.Y - from.Y) switch
        {
            (0, -1) => "above",
            (0, 1) => "below",
            (-1, 0) => "left of",
            _ => "right of",
        };

    /// <summary>
    /// The build's turn for a board, as the player turns it while attaching it: the game's Rotate Board turns the preview a
    /// quarter clockwise a press (seen in game: the build's 3 quarter turns matched after 3 presses).
    /// </summary>
    public static string Turns(int rotation) => (((rotation % 4) + 4) % 4) switch
    {
        0 => "don't rotate it",
        1 => "press Rotate Board once",
        2 => "press Rotate Board twice",
        _ => "press Rotate Board 3 times",
    };

    /// <summary>The glyph's name and the level the build has it at, e.g. "Apostle (level 100)".</summary>
    public static string GlyphLabel(PlacedBoard board, ParagonData paragon) =>
        (paragon.Glyphs.GetValueOrDefault(board.Glyph ?? "")?.Name ?? board.Glyph ?? "").Trim() + (board.GlyphLevel > 0 ? $" (level {board.GlyphLevel})" : "");

    private static bool IsSocket(ParagonBoard data, int i, ParagonData paragon) =>
        data.Nodes[i] is { } key && paragon.Nodes.GetValueOrDefault(key)?.Kind == "socket";

    private static (double X, double Y) At(ParagonPlacement grid, PlacedBoard board, int i, ParagonBoard data)
    {
        var (cx, cy) = board.Cell(i, data.Width);
        return grid.ToScreen(cx, cy);
    }
}
