using System;

namespace OmniTwin.Perception.Tracking;

internal static class MinimumCostAssignment
{
    // Rectangular Hungarian assignment. Each row receives one distinct column; columns >= rows.
    internal static int[] Solve(double[,] costs)
    {
        var rows = costs.GetLength(0); var columns = costs.GetLength(1);
        if (rows > columns) throw new ArgumentException("Assignment requires at least one column per row.", nameof(costs));
        var u = new double[rows + 1]; var v = new double[columns + 1];
        var p = new int[columns + 1]; var way = new int[columns + 1];
        for (var row = 1; row <= rows; row++)
        {
            p[0] = row;
            var column = 0;
            var minimum = new double[columns + 1]; var used = new bool[columns + 1];
            for (var j = 1; j <= columns; j++) minimum[j] = double.PositiveInfinity;
            do
            {
                used[column] = true;
                var activeRow = p[column]; var next = 0; var delta = double.PositiveInfinity;
                for (var j = 1; j <= columns; j++)
                {
                    if (used[j]) continue;
                    var reduced = costs[activeRow - 1, j - 1] - u[activeRow] - v[j];
                    if (reduced < minimum[j]) { minimum[j] = reduced; way[j] = column; }
                    if (minimum[j] < delta) { delta = minimum[j]; next = j; }
                }
                for (var j = 0; j <= columns; j++)
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minimum[j] -= delta;
                column = next;
            } while (p[column] != 0);
            do { var previous = way[column]; p[column] = p[previous]; column = previous; } while (column != 0);
        }
        var assignment = new int[rows];
        for (var j = 1; j <= columns; j++) if (p[j] != 0) assignment[p[j] - 1] = j - 1;
        return assignment;
    }
}
